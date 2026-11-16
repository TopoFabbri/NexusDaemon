using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;

/// <summary>
/// Phase 4 Runner for installed Antigravity v2.32+:
/// Launches a headless conversation via `agentapi.bat new-conversation --model=...`,
/// tracks the CascadeId/PID in StateManager, awaits completion via the local Antigravity
/// Hub ConnectRPC endpoints (`WaitForConversationFullyIdle` + `GetCascadeTrajectory`),
/// and fires `AgentTaskCompletedEvent` into `NexusEventBus`.
/// </summary>
public sealed partial class AntigravityDaemonRunner : IAgentRunner
{
    private readonly NexusOptions _options;
    private readonly StateManager _stateManager;
    private readonly NexusEventBus _eventBus;
    private readonly ModelCatalog _modelCatalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AntigravityDaemonRunner> _logger;

    public AntigravityDaemonRunner(
        IOptions<NexusOptions> options,
        StateManager stateManager,
        NexusEventBus eventBus,
        ModelCatalog modelCatalog,
        IHttpClientFactory httpClientFactory,
        ILogger<AntigravityDaemonRunner> logger)
    {
        _options = options.Value;
        _stateManager = stateManager;
        _eventBus = eventBus;
        _modelCatalog = modelCatalog;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public WorkerStatus StartWorkerInBackground(
        string projectName,
        string prompt,
        string? model = null,
        CancellationToken stoppingToken = default)
    {
        var workerId = $"agy-{Guid.NewGuid().ToString("N")[..8]}";
        var effectiveModel = _modelCatalog.NormalizeModelId(model ?? _stateManager.ActiveWorkerModel);
        var status = _stateManager.RegisterWorker(workerId, projectName, prompt, effectiveModel);

        _ = Task.Run(() => ExecuteConversationAsync(workerId, projectName, prompt, effectiveModel, stoppingToken), CancellationToken.None);
        return status;
    }

    private async Task ExecuteConversationAsync(
        string workerId,
        string projectName,
        string prompt,
        string model,
        CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        int? processId = null;
        string? cascadeId = null;
        bool success = false;
        string finalJson;

        try
        {
            var workingDir = ResolveProjectWorkingDirectory(projectName);
            var fullPrompt = BuildWorkerPrompt(projectName, workingDir, prompt);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.Runner.WorkerTimeoutSeconds)));

            // Discover live Hub endpoint BEFORE spawning child conversation
            var hubEndpoint = TryDiscoverLatestHubEndpoint();
            var discoveredAddr = hubEndpoint is not null ? RunnerOptions.NormalizeAddress(hubEndpoint.BaseUri) : null;
            var effectiveLsAddress = _options.Runner.ResolveLsAddress(discoveredAddr);
            var effectiveCsrfToken = _options.Runner.ResolveCsrfToken(hubEndpoint?.CsrfToken);

            var cliModelArg = _modelCatalog.ResolveAntigravityCliModelArgument(model);
            var (spawnExitCode, stdout, stderr, spawnedPid) = await SpawnAgentApiNewConversationAsync(
                workingDir,
                projectName,
                fullPrompt,
                cliModelArg,
                effectiveLsAddress,
                effectiveCsrfToken,
                timeoutCts.Token);

            processId = hubEndpoint?.ProcessId ?? spawnedPid;
            cascadeId = ExtractConversationId(stdout);
            _stateManager.UpdateWorkerProcessInfo(workerId, processId, cascadeId);

            if (spawnExitCode != 0)
            {
                success = false;
                var diagnosticGuidance = BuildDiagnosticGuidance(spawnExitCode, stdout, stderr, hubEndpoint, effectiveLsAddress);
                finalJson = JsonSerializer.Serialize(new
                {
                    type = "error",
                    is_error = true,
                    exitCode = spawnExitCode,
                    stdout = stdout.Trim(),
                    stderr = stderr.Trim(),
                    diagnostic = diagnosticGuidance
                });
            }
            else if (!string.IsNullOrWhiteSpace(cascadeId))
            {
                (success, finalJson) = await AwaitConversationIdleAndFetchTrajectoryAsync(
                    cascadeId,
                    stdout,
                    hubEndpoint,
                    timeoutCts.Token);
            }
            else
            {
                success = true;
                finalJson = JsonSerializer.Serialize(new
                {
                    type = "result",
                    status = "completed",
                    model,
                    cliModel = cliModelArg,
                    project = projectName,
                    output = stdout.Trim()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AntigravityDaemonRunner worker {WorkerId} failed on {ProjectName}", workerId, projectName);
            success = false;
            finalJson = JsonSerializer.Serialize(new
            {
                type = "error",
                is_error = true,
                workerId,
                project = projectName,
                error = ex.Message
            });
        }
        finally
        {
            sw.Stop();
            _stateManager.CompleteWorker(workerId, out _);
        }

        var completedEvent = new AgentTaskCompletedEvent(
            WorkerId: workerId,
            ProcessId: processId,
            CascadeId: cascadeId,
            ProjectName: projectName,
            TaskDescription: prompt,
            Model: model,
            Success: success,
            JsonResult: finalJson,
            Duration: sw.Elapsed,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await _eventBus.PublishAsync(completedEvent, CancellationToken.None);
    }

    public static void InjectAntigravityEnvironment(ProcessStartInfo psi, string? lsAddress, string? csrfToken)
    {
        if (!string.IsNullOrWhiteSpace(lsAddress))
        {
            var normalized = RunnerOptions.NormalizeAddress(lsAddress);
            psi.Environment["ANTIGRAVITY_LS_ADDRESS"] = normalized;
            psi.EnvironmentVariables["ANTIGRAVITY_LS_ADDRESS"] = normalized;
        }

        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            var token = csrfToken.Trim();
            psi.Environment["ANTIGRAVITY_CSRF_TOKEN"] = token;
            psi.EnvironmentVariables["ANTIGRAVITY_CSRF_TOKEN"] = token;
            psi.Environment["ANTIGRAVITY_SERVER_CSRF_TOKEN"] = token;
            psi.EnvironmentVariables["ANTIGRAVITY_SERVER_CSRF_TOKEN"] = token;
        }
    }

    private static string BuildDiagnosticGuidance(
        int exitCode,
        string stdout,
        string stderr,
        HubEndpointInfo? hubEndpoint,
        string lsAddress)
    {
        var sb = new StringBuilder();
        var combined = $"{stdout}\n{stderr}";

        if (combined.Contains("missing CSRF token", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("invalid CSRF token", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Authentication failed: Missing or invalid CSRF token. Ensure Antigravity Hub is running and ANTIGRAVITY_CSRF_TOKEN is configured or discoverable.");
        }
        else if (combined.Contains("ANTIGRAVITY_LS_ADDRESS is not set", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("ANTIGRAVITY_LS_ADDRESS", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Antigravity Language Server address error. Configured LS Address: '{lsAddress}'. Set ANTIGRAVITY_LS_ADDRESS in environment or .env file.");
        }

        if (hubEndpoint is null)
        {
            sb.AppendLine("Warning: Antigravity Hub is not currently running or no active hub_*.json was found in daemon directory.");
        }

        if (sb.Length == 0)
        {
            sb.Append($"Process exited with code {exitCode}.");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Formats and sanitizes the worker prompt so cmd.exe / batch wrappers cannot truncate it at newlines or break quoting.
    /// </summary>
    public static string BuildWorkerPrompt(string projectName, string workingDir, string prompt)
    {
        var safeProject = SanitizeCmdArgument(projectName);
        var safePrompt = SanitizeCmdArgument(prompt);
        return $"[Project: {safeProject} ({workingDir})] {safePrompt}";
    }

    public static string SanitizeCmdArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // cmd.exe and .bat %* truncate command-line arguments at CR/LF and misparse \" inside quotes.
        return NewlineRegex().Replace(value, " ").Replace('"', '\'').Trim();
    }

    public static ProcessStartInfo BuildAgentApiStartInfo(
        string batchPath,
        string workingDirectory,
        string projectName,
        string prompt,
        string model,
        string? lsAddress,
        string? csrfToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        InjectAntigravityEnvironment(psi, lsAddress, csrfToken);

        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(batchPath);
        psi.ArgumentList.Add("new-conversation");
        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add($"--model={SanitizeCmdArgument(model)}");
        }

        psi.ArgumentList.Add($"--title=Nexus: {SanitizeCmdArgument(projectName)}");
        psi.ArgumentList.Add(SanitizeCmdArgument(prompt));
        return psi;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr, int Pid)> SpawnAgentApiNewConversationAsync(
        string workingDirectory,
        string projectName,
        string prompt,
        string model,
        string? lsAddress,
        string? csrfToken,
        CancellationToken ct)
    {
        var psi = BuildAgentApiStartInfo(
            _options.Runner.AgentApiBatchPath,
            workingDirectory,
            projectName,
            prompt,
            model,
            lsAddress,
            csrfToken);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_options.Runner.AgentApiBatchPath}'.");

        var pid = process.Id;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, await stdoutTask, await stderrTask, pid);
        }
        finally
        {
            TryKillProcessTree(process);
        }
    }

    public async Task<(bool IsSuccess, string StructuredJson)> AwaitConversationIdleAndFetchTrajectoryAsync(
        string cascadeId,
        string initialOutput,
        HubEndpointInfo? hubEndpoint,
        CancellationToken ct)
    {
        hubEndpoint ??= TryDiscoverLatestHubEndpoint();
        if (hubEndpoint is null)
        {
            var msg = "Antigravity Hub is not running; cannot monitor conversation status or trajectory.";
            _logger.LogWarning("{Message} (CascadeId: {CascadeId})", msg, cascadeId);
            return (false, JsonSerializer.Serialize(new
            {
                type = "error",
                is_error = true,
                status = "FAILED",
                cascadeId,
                error = msg,
                initialOutput = initialOutput.Trim()
            }));
        }

        try
        {
            using var client = _httpClientFactory.CreateClient("AntigravityHub");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(30, _options.Runner.WorkerTimeoutSeconds));

            var csrfToken = _options.Runner.ResolveCsrfToken(hubEndpoint.CsrfToken);
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                client.DefaultRequestHeaders.Remove("x-codeium-csrf-token");
                client.DefaultRequestHeaders.Add("x-codeium-csrf-token", csrfToken);
            }

            // 1. WaitForConversationFullyIdle (field: conversationId)
            var waitPayload = JsonSerializer.Serialize(new { conversationId = cascadeId });
            using var waitContent = new StringContent(waitPayload, Encoding.UTF8, "application/json");
            var waitResp = await client.PostAsync(
                $"{hubEndpoint.BaseUri}/exa.language_server_pb.LanguageServerService/WaitForConversationFullyIdle",
                waitContent,
                ct);

            if (waitResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogInformation("WaitForConversationFullyIdle received HTTP 401; attempting CSRF token refresh.");
                var refreshedEndpoint = TryDiscoverLatestHubEndpoint();
                var refreshedToken = _options.Runner.ResolveCsrfToken(refreshedEndpoint?.CsrfToken);
                if (!string.IsNullOrWhiteSpace(refreshedToken) && refreshedToken != csrfToken)
                {
                    csrfToken = refreshedToken;
                    hubEndpoint = refreshedEndpoint ?? hubEndpoint;
                    client.DefaultRequestHeaders.Remove("x-codeium-csrf-token");
                    client.DefaultRequestHeaders.Add("x-codeium-csrf-token", csrfToken);

                    waitResp.Dispose();
                    using var retryWaitContent = new StringContent(waitPayload, Encoding.UTF8, "application/json");
                    waitResp = await client.PostAsync(
                        $"{hubEndpoint.BaseUri}/exa.language_server_pb.LanguageServerService/WaitForConversationFullyIdle",
                        retryWaitContent,
                        ct);
                }
            }

            using (waitResp)
            {
                if (!waitResp.IsSuccessStatusCode)
                {
                    var waitError = await waitResp.Content.ReadAsStringAsync(ct);
                    var errMsg = $"WaitForConversationFullyIdle failed with HTTP {(int)waitResp.StatusCode} ({waitResp.StatusCode}): {waitError}".Trim();
                    _logger.LogWarning("Conversation idle wait failed: {Message}", errMsg);
                    return (false, JsonSerializer.Serialize(new
                    {
                        type = "error",
                        is_error = true,
                        status = "FAILED",
                        cascadeId,
                        statusCode = (int)waitResp.StatusCode,
                        error = errMsg
                    }));
                }
            }

            // 2. GetCascadeTrajectory
            var trajPayload = JsonSerializer.Serialize(new { cascadeId });
            using var trajContent = new StringContent(trajPayload, Encoding.UTF8, "application/json");
            var trajResp = await client.PostAsync(
                $"{hubEndpoint.BaseUri}/exa.language_server_pb.LanguageServerService/GetCascadeTrajectory",
                trajContent,
                ct);

            if (trajResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogInformation("GetCascadeTrajectory received HTTP 401; attempting CSRF token refresh.");
                var refreshedEndpoint = TryDiscoverLatestHubEndpoint();
                var refreshedToken = _options.Runner.ResolveCsrfToken(refreshedEndpoint?.CsrfToken);
                if (!string.IsNullOrWhiteSpace(refreshedToken) && refreshedToken != csrfToken)
                {
                    csrfToken = refreshedToken;
                    hubEndpoint = refreshedEndpoint ?? hubEndpoint;
                    client.DefaultRequestHeaders.Remove("x-codeium-csrf-token");
                    client.DefaultRequestHeaders.Add("x-codeium-csrf-token", csrfToken);

                    trajResp.Dispose();
                    using var retryTrajContent = new StringContent(trajPayload, Encoding.UTF8, "application/json");
                    trajResp = await client.PostAsync(
                        $"{hubEndpoint.BaseUri}/exa.language_server_pb.LanguageServerService/GetCascadeTrajectory",
                        retryTrajContent,
                        ct);
                }
            }

            using (trajResp)
            {
                if (trajResp.IsSuccessStatusCode)
                {
                    var trajectoryJson = await trajResp.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(trajectoryJson))
                    {
                        return ExtractStructuredTrajectoryResult(cascadeId, trajectoryJson, initialOutput);
                    }
                }
                else
                {
                    var trajError = await trajResp.Content.ReadAsStringAsync(ct);
                    var errMsg = $"GetCascadeTrajectory failed with HTTP {(int)trajResp.StatusCode} ({trajResp.StatusCode}): {trajError}".Trim();
                    _logger.LogWarning("Trajectory retrieval failed: {Message}", errMsg);
                    return (false, JsonSerializer.Serialize(new
                    {
                        type = "error",
                        is_error = true,
                        status = "FAILED",
                        cascadeId,
                        statusCode = (int)trajResp.StatusCode,
                        error = errMsg
                    }));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hub RPC lookup for cascade {CascadeId} failed", cascadeId);
            return (false, JsonSerializer.Serialize(new
            {
                type = "error",
                is_error = true,
                status = "FAILED",
                cascadeId,
                error = ex.Message
            }));
        }

        return (true, JsonSerializer.Serialize(new
        {
            type = "result",
            status = "completed",
            cascadeId,
            output = initialOutput.Trim()
        }));
    }

    /// <summary>
    /// Extracts a compact, bounded structured result from GetCascadeTrajectory JSON so multi-hundred-KB
    /// raw protobuf trajectories (including thinkingSignature blobs) do not bloat the sliding window.
    /// </summary>
    public static (bool IsSuccess, string StructuredJson) ExtractStructuredTrajectoryResult(
        string cascadeId,
        string trajectoryJson,
        string fallbackOutput = "")
    {
        const int maxResponseLength = 6000;
        try
        {
            using var doc = JsonDocument.Parse(trajectoryJson);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString() ?? "CASCADE_RUN_STATUS_IDLE"
                : "CASCADE_RUN_STATUS_IDLE";

            var isError = status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                          status.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                          status.Contains("CANCEL", StringComparison.OrdinalIgnoreCase);

            var numTotalSteps = root.TryGetProperty("numTotalSteps", out var stepsCountProp) && stepsCountProp.TryGetInt32(out var n)
                ? n
                : 0;

            string? finalPlannerResponse = null;
            var toolsInvoked = new List<string>();

            if (root.TryGetProperty("trajectory", out var trajElem) &&
                trajElem.ValueKind == JsonValueKind.Object &&
                trajElem.TryGetProperty("steps", out var stepsArray) &&
                stepsArray.ValueKind == JsonValueKind.Array)
            {
                if (numTotalSteps == 0)
                {
                    numTotalSteps = stepsArray.GetArrayLength();
                }

                foreach (var step in stepsArray.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (step.TryGetProperty("plannerResponse", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    {
                        var text = pr.TryGetProperty("modifiedResponse", out var modResp) && modResp.ValueKind == JsonValueKind.String
                            ? modResp.GetString()
                            : pr.TryGetProperty("response", out var rawResp) && rawResp.ValueKind == JsonValueKind.String
                                ? rawResp.GetString()
                                : null;

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            finalPlannerResponse = text.Trim();
                        }

                        if (pr.TryGetProperty("toolCalls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcArray.EnumerateArray())
                            {
                                if (tc.ValueKind == JsonValueKind.Object &&
                                    tc.TryGetProperty("name", out var tcName) &&
                                    tcName.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(tcName.GetString()))
                                {
                                    toolsInvoked.Add(tcName.GetString()!);
                                }
                            }
                        }
                    }
                }
            }

            var effectiveOutput = !string.IsNullOrWhiteSpace(finalPlannerResponse)
                ? finalPlannerResponse!
                : fallbackOutput.Trim();

            if (effectiveOutput.Length > maxResponseLength)
            {
                effectiveOutput = effectiveOutput[..maxResponseLength] + "\n...[TRUNCATED]...";
            }

            var compactJson = JsonSerializer.Serialize(new
            {
                type = "result",
                status,
                is_error = isError,
                cascadeId,
                numTotalSteps,
                toolsInvoked = toolsInvoked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                finalResponse = effectiveOutput
            });

            return (!isError, compactJson);
        }
        catch (JsonException)
        {
            var truncatedRaw = trajectoryJson.Length > maxResponseLength
                ? trajectoryJson[..maxResponseLength] + "...[TRUNCATED]..."
                : trajectoryJson;
            return (true, truncatedRaw);
        }
    }

    public HubEndpointInfo? TryDiscoverLatestHubEndpoint(string? daemonDirOverride = null) =>
        DiscoverLatestHubEndpoint(daemonDirOverride ?? _options.Runner.DaemonDirectory);

    public static HubEndpointInfo? DiscoverLatestHubEndpoint(string? daemonDir = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(daemonDir) || !Directory.Exists(daemonDir))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var defaultDir = Path.Combine(userProfile, ".gemini", "antigravity", "daemon");
                if (Directory.Exists(defaultDir))
                {
                    daemonDir = defaultDir;
                }
                else
                {
                    return null;
                }
            }

            var hubFiles = Directory.GetFiles(daemonDir, "hub_*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            HubEndpointInfo? fallbackDeadEndpoint = null;

            foreach (var file in hubFiles)
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    int? port = null;
                    if (root.TryGetProperty("httpPort", out var hp) && hp.TryGetInt32(out var httpPortVal))
                    {
                        port = httpPortVal;
                    }
                    else if (root.TryGetProperty("port", out var p) && p.TryGetInt32(out var portVal))
                    {
                        port = portVal;
                    }

                    int? pid = null;
                    if (root.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var pidVal))
                    {
                        pid = pidVal;
                    }

                    string? csrf = null;
                    if (root.TryGetProperty("csrfToken", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        csrf = c.GetString();
                    }
                    else if (root.TryGetProperty("csrf_token", out var c2) && c2.ValueKind == JsonValueKind.String)
                    {
                        csrf = c2.GetString();
                    }

                    if (!port.HasValue || port.Value <= 0)
                    {
                        continue;
                    }

                    var candidate = new HubEndpointInfo($"http://127.0.0.1:{port.Value}", csrf, pid);
                    if (pid.HasValue && IsProcessAlive(pid.Value))
                    {
                        return candidate;
                    }

                    fallbackDeadEndpoint ??= candidate;
                }
                catch (Exception)
                {
                    // Skip malformed or locked hub file
                }
            }

            return fallbackDeadEndpoint;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process cleanup
        }
    }

    public static string? ExtractConversationId(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = UuidRegex().Match(output);
        return match.Success ? match.Value : null;
    }

    private string ResolveProjectWorkingDirectory(string projectName)
    {
        if (_options.Projects.TryGetValue(projectName, out var proj) &&
            !string.IsNullOrWhiteSpace(proj.RootPath) &&
            Directory.Exists(proj.RootPath))
        {
            return proj.RootPath;
        }

        return Directory.GetCurrentDirectory();
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex NewlineRegex();

    public sealed record HubEndpointInfo(string BaseUri, string? CsrfToken, int? ProcessId = null);
}
