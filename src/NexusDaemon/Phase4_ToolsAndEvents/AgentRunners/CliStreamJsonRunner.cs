using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;

/// <summary>
/// Phase 4 Runner: Spawns a headless `agy` process in `--input-format stream-json` mode,
/// registers the active worker in StateManager immediately, parses stdout via StreamJsonParser
/// in the background, and publishes an AgentTaskCompletedEvent upon completion.
/// </summary>
public sealed class CliStreamJsonRunner : IAgentRunner
{
    private readonly NexusOptions _options;
    private readonly StateManager _stateManager;
    private readonly NexusEventBus _eventBus;
    private readonly ModelCatalog _modelCatalog;
    private readonly ILogger<CliStreamJsonRunner> _logger;

    public CliStreamJsonRunner(
        IOptions<NexusOptions> options,
        StateManager stateManager,
        NexusEventBus eventBus,
        ModelCatalog modelCatalog,
        ILogger<CliStreamJsonRunner> logger)
    {
        _options = options.Value;
        _stateManager = stateManager;
        _eventBus = eventBus;
        _modelCatalog = modelCatalog;
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

        _ = Task.Run(() => ExecuteWorkerAsync(workerId, projectName, prompt, effectiveModel, stoppingToken), CancellationToken.None);
        return status;
    }

    private async Task ExecuteWorkerAsync(
        string workerId,
        string projectName,
        string prompt,
        string model,
        CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        int? processId = null;
        bool success = false;
        string finalJson;

        try
        {
            var workingDir = ResolveProjectWorkingDirectory(projectName);
            var hubEndpoint = AntigravityDaemonRunner.DiscoverLatestHubEndpoint(_options.Runner.DaemonDirectory);
            var discoveredAddr = hubEndpoint is not null ? RunnerOptions.NormalizeAddress(hubEndpoint.BaseUri) : null;
            var effectiveLsAddress = _options.Runner.ResolveLsAddress(discoveredAddr);
            var effectiveCsrfToken = _options.Runner.ResolveCsrfToken(hubEndpoint?.CsrfToken);

            var psi = new ProcessStartInfo
            {
                FileName = _options.Runner.AgyExecutablePath,
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AntigravityDaemonRunner.InjectAntigravityEnvironment(psi, effectiveLsAddress, effectiveCsrfToken);

            foreach (var arg in _options.Runner.AgyArgumentsTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(arg);
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to launch '{_options.Runner.AgyExecutablePath}'.");

            try
            {
                processId = process.Id;
                _stateManager.UpdateWorkerProcessInfo(workerId, processId, null);

                // Start draining stderr asynchronously right away so it never blocks stdout or hangs after terminal JSON
                var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

                // Send initial stream-json user prompt frame
                var inputFrame = JsonSerializer.Serialize(new
                {
                    type = "user",
                    model,
                    project = projectName,
                    message = new
                    {
                        role = "user",
                        content = prompt
                    }
                });

                await process.StandardInput.WriteLineAsync(inputFrame);
                await process.StandardInput.FlushAsync(stoppingToken);
                process.StandardInput.Close();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.Runner.WorkerTimeoutSeconds)));

                var parseOutcome = await StreamJsonParser.ParseStreamToCompletionAsync(process.StandardOutput, timeoutCts.Token);

                // Once a terminal JSON frame is received, wait briefly for graceful process exit and then terminate if still open
                if (!process.HasExited)
                {
                    using var exitGraceCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                    exitGraceCts.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.WaitForExitAsync(exitGraceCts.Token);
                    }
                    catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
                    {
                        // Process kept pipe open after emitting terminal JSON; kill below
                    }
                }

                TryKillProcessTree(process);
                var stderr = await stderrTask;

                success = parseOutcome.IsSuccess && (parseOutcome.IsTerminalFound || process.ExitCode == 0);
                finalJson = parseOutcome.FinalResultJson;

                if (!success && !string.IsNullOrWhiteSpace(stderr))
                {
                    var diagnostic = string.Empty;
                    if (stderr.Contains("ANTIGRAVITY_LS_ADDRESS", StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic = $"Antigravity Language Server address error. Configured LS Address: '{effectiveLsAddress}'. Set ANTIGRAVITY_LS_ADDRESS in environment or .env file.";
                    }
                    else if (stderr.Contains("CSRF", StringComparison.OrdinalIgnoreCase) || stderr.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic = "Authentication failed: Missing or invalid CSRF token. Ensure Antigravity Hub is running and ANTIGRAVITY_CSRF_TOKEN is configured or discoverable.";
                    }

                    finalJson = JsonSerializer.Serialize(new
                    {
                        type = "result",
                        is_error = true,
                        result = finalJson,
                        stderr = stderr.Trim(),
                        diagnostic = string.IsNullOrEmpty(diagnostic) ? null : diagnostic
                    });
                }
            }
            finally
            {
                TryKillProcessTree(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CliStreamJsonRunner worker {WorkerId} failed on {ProjectName}", workerId, projectName);
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
            CascadeId: null,
            ProjectName: projectName,
            TaskDescription: prompt,
            Model: model,
            Success: success,
            JsonResult: finalJson,
            Duration: sw.Elapsed,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await _eventBus.PublishAsync(completedEvent, CancellationToken.None);
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
            // Best-effort cleanup
        }
    }
}
