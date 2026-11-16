using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;

namespace NexusDaemon.Phase2_StateAndMemory;

public sealed record WorkerStatus(
    string WorkerId,
    int? ProcessId,
    string? CascadeId,
    string ProjectName,
    string TaskDescription,
    string Model,
    DateTimeOffset StartedAtUtc);

public sealed record BuildStatus(
    string BuildId,
    int? ProcessId,
    string ProjectName,
    string Configuration,
    string CommandSummary,
    DateTimeOffset StartedAtUtc);

public sealed record PendingApprovalRequest(
    string RequestId,
    string ToolName,
    string ProjectName,
    string Summary,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset CreatedAtUtc);

public sealed record GitRepoStatus(
    string ProjectName,
    string Branch,
    int ModifiedOrUntrackedCount,
    bool IsAvailable,
    string? ErrorMessage = null);

public interface IGitStatusProvider
{
    Task<GitRepoStatus> GetRepoStatusAsync(string projectName, string repoRootPath, CancellationToken ct = default);
}

public sealed class ProcessGitStatusProvider : IGitStatusProvider
{
    private readonly NexusOptions _options;
    private readonly ILogger<ProcessGitStatusProvider> _logger;

    public ProcessGitStatusProvider(IOptions<NexusOptions> options, ILogger<ProcessGitStatusProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GitRepoStatus> GetRepoStatusAsync(string projectName, string repoRootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath) || !Directory.Exists(repoRootPath))
        {
            return new GitRepoStatus(projectName, "unknown", 0, false, "Directory not found");
        }

        var gitExe = ResolveGitExecutable(_options.Runner.GitExecutablePath);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitExe,
                WorkingDirectory = repoRootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");
            psi.ArgumentList.Add("--branch");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new GitRepoStatus(projectName, "unknown", 0, false, "Failed to start git process");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                return new GitRepoStatus(projectName, "unknown", 0, false, stderr.Trim());
            }

            return ParsePorcelainBranchOutput(projectName, stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not query git status for project {ProjectName}", projectName);
            return new GitRepoStatus(projectName, "unknown", 0, false, ex.Message);
        }
    }

    public static GitRepoStatus ParsePorcelainBranchOutput(string projectName, string porcelainOutput)
    {
        var lines = (porcelainOutput ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var branch = "detached";
        var modifiedCount = 0;

        foreach (var rawLine in lines)
        {
            if (rawLine.StartsWith("## ", StringComparison.Ordinal))
            {
                var branchHeader = rawLine[3..].Trim();
                if (branchHeader.StartsWith("HEAD (no branch)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(branchHeader, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    branch = "detached";
                    continue;
                }

                const string unbornPrefix1 = "No commits yet on ";
                const string unbornPrefix2 = "Initial commit on ";
                if (branchHeader.StartsWith(unbornPrefix1, StringComparison.OrdinalIgnoreCase))
                {
                    branchHeader = branchHeader[unbornPrefix1.Length..].Trim();
                }
                else if (branchHeader.StartsWith(unbornPrefix2, StringComparison.OrdinalIgnoreCase))
                {
                    branchHeader = branchHeader[unbornPrefix2.Length..].Trim();
                }

                var dotIndex = branchHeader.IndexOf("...", StringComparison.Ordinal);
                var spaceIndex = branchHeader.IndexOf(' ', StringComparison.Ordinal);
                if (dotIndex > 0)
                {
                    branch = branchHeader[..dotIndex];
                }
                else if (spaceIndex > 0)
                {
                    branch = branchHeader[..spaceIndex];
                }
                else if (!string.IsNullOrWhiteSpace(branchHeader))
                {
                    branch = branchHeader;
                }
            }
            else if (!string.IsNullOrWhiteSpace(rawLine))
            {
                modifiedCount++;
            }
        }

        return new GitRepoStatus(projectName, branch, modifiedCount, true);
    }

    private static string ResolveGitExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        return "git";
    }
}

/// <summary>
/// Phase 2 Singleton: Tracks live status of the PC including ActiveWorkers (Antigravity PIDs/CascadeIds),
/// ActiveBuilds (MSBuild / Unity CLI execution), GitStatus across projects, active models, and HITL PendingApproval.
/// </summary>
public sealed class StateManager
{
    private readonly NexusOptions _options;
    private readonly ModelCatalog _modelCatalog;
    private readonly IGitStatusProvider _gitStatusProvider;
    private readonly ConcurrentDictionary<string, WorkerStatus> _activeWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BuildStatus> _activeBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateSync = new();

    private PendingApprovalRequest? _pendingApproval;
    private string _activeRouterModel;
    private string _activeWorkerModel;

    public StateManager(
        IOptions<NexusOptions> options,
        ModelCatalog modelCatalog,
        IGitStatusProvider gitStatusProvider)
    {
        _options = options.Value;
        _modelCatalog = modelCatalog;
        _gitStatusProvider = gitStatusProvider;
        _activeRouterModel = _modelCatalog.NormalizeModelId(_options.Gemini.DefaultModel, "hydrogen-4");
        _activeWorkerModel = _modelCatalog.NormalizeModelId(_options.Runner.DefaultWorkerModel, "hydrogen-4");
    }

    public IReadOnlyDictionary<string, WorkerStatus> ActiveWorkers => _activeWorkers;

    public IReadOnlyDictionary<string, BuildStatus> ActiveBuilds => _activeBuilds;

    public string ActiveRouterModel
    {
        get
        {
            lock (_stateSync)
            {
                return _activeRouterModel;
            }
        }
    }

    public string ActiveWorkerModel
    {
        get
        {
            lock (_stateSync)
            {
                return _activeWorkerModel;
            }
        }
    }

    public PendingApprovalRequest? PendingApproval
    {
        get
        {
            lock (_stateSync)
            {
                return _pendingApproval;
            }
        }
    }

    public string SetRouterModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var normalized = _modelCatalog.NormalizeModelId(modelId);
        lock (_stateSync)
        {
            _activeRouterModel = normalized;
            return _activeRouterModel;
        }
    }

    public string SetWorkerModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var normalized = _modelCatalog.NormalizeModelId(modelId);
        lock (_stateSync)
        {
            _activeWorkerModel = normalized;
            return _activeWorkerModel;
        }
    }

    public WorkerStatus RegisterWorker(
        string workerId,
        string projectName,
        string taskDescription,
        string? model = null,
        int? processId = null,
        string? cascadeId = null)
    {
        var effectiveModel = _modelCatalog.NormalizeModelId(model ?? ActiveWorkerModel);
        var status = new WorkerStatus(
            workerId,
            processId,
            cascadeId,
            projectName,
            taskDescription,
            effectiveModel,
            DateTimeOffset.UtcNow);

        _activeWorkers[workerId] = status;
        return status;
    }

    public void UpdateWorkerProcessInfo(string workerId, int? processId, string? cascadeId)
    {
        if (_activeWorkers.TryGetValue(workerId, out var existing))
        {
            _activeWorkers[workerId] = existing with
            {
                ProcessId = processId ?? existing.ProcessId,
                CascadeId = cascadeId ?? existing.CascadeId
            };
        }
    }

    public bool CompleteWorker(string workerId, out WorkerStatus? removed)
    {
        return _activeWorkers.TryRemove(workerId, out removed);
    }

    public BuildStatus RegisterBuild(
        string buildId,
        string projectName,
        string configuration,
        string commandSummary,
        int? processId = null)
    {
        var status = new BuildStatus(
            buildId,
            processId,
            projectName,
            configuration,
            commandSummary,
            DateTimeOffset.UtcNow);

        _activeBuilds[buildId] = status;
        return status;
    }

    public void UpdateBuildProcessId(string buildId, int processId, string? commandSummary = null)
    {
        if (_activeBuilds.TryGetValue(buildId, out var existing))
        {
            _activeBuilds[buildId] = existing with
            {
                ProcessId = processId,
                CommandSummary = !string.IsNullOrWhiteSpace(commandSummary) ? commandSummary : existing.CommandSummary
            };
        }
    }

    public bool CompleteBuild(string buildId, out BuildStatus? removed)
    {
        return _activeBuilds.TryRemove(buildId, out removed);
    }

    public void SetPendingApproval(PendingApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateSync)
        {
            _pendingApproval = request;
        }
    }

    public PendingApprovalRequest? ConsumePendingApproval()
    {
        lock (_stateSync)
        {
            var current = _pendingApproval;
            _pendingApproval = null;
            return current;
        }
    }

    public async Task<string> BuildSystemStateSummaryAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append($"System State [RouterModel={ActiveRouterModel}, WorkerModel={ActiveWorkerModel}]: ");

        if (_options.Projects.Count == 0)
        {
            sb.Append("No registered projects. ");
        }
        else
        {
            var projectSegments = new List<string>();
            foreach (var (projectName, projectConfig) in _options.Projects)
            {
                var gitStatus = await _gitStatusProvider.GetRepoStatusAsync(projectName, projectConfig.RootPath, ct);
                var gitSegment = gitStatus.IsAvailable
                    ? $"branch: {gitStatus.Branch}, {gitStatus.ModifiedOrUntrackedCount} modified/untracked files"
                    : "git status unavailable";

                var projectWorkers = _activeWorkers.Values
                    .Where(w => string.Equals(w.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var projectBuilds = _activeBuilds.Values
                    .Where(b => string.Equals(b.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var workerText = projectWorkers.Count == 0
                    ? "0 active agy workers"
                    : $"{projectWorkers.Count} active agy worker(s) [{string.Join(", ", projectWorkers.Select(FormatWorkerTag))}]";

                var buildText = projectBuilds.Count == 0
                    ? "0 active builds"
                    : $"{projectBuilds.Count} active build(s) [{string.Join(", ", projectBuilds.Select(FormatBuildTag))}]";

                projectSegments.Add($"{projectName} ({gitSegment}) has {workerText}; {buildText}");
            }

            sb.Append(string.Join(" | ", projectSegments));
            sb.Append('.');
        }

        // Include any workers or builds on ad-hoc project names not in _options.Projects
        var unlistedWorkers = _activeWorkers.Values
            .Where(w => !_options.Projects.ContainsKey(w.ProjectName))
            .ToList();
        if (unlistedWorkers.Count > 0)
        {
            sb.Append($" Unlisted project workers: {string.Join(", ", unlistedWorkers.Select(FormatWorkerTag))}.");
        }

        var unlistedBuilds = _activeBuilds.Values
            .Where(b => !_options.Projects.ContainsKey(b.ProjectName))
            .ToList();
        if (unlistedBuilds.Count > 0)
        {
            sb.Append($" Unlisted project builds: {string.Join(", ", unlistedBuilds.Select(FormatBuildTag))}.");
        }

        var pending = PendingApproval;
        if (pending is not null)
        {
            sb.Append($" Pending HITL Approval: {pending.ToolName} on {pending.ProjectName} ({pending.Summary}).");
        }

        return sb.ToString().Trim();
    }

    private static string FormatWorkerTag(WorkerStatus worker)
    {
        var idTag = (worker.ProcessId.HasValue, !string.IsNullOrWhiteSpace(worker.CascadeId)) switch
        {
            (true, true) => $"PID {worker.ProcessId!.Value} / Cascade {worker.CascadeId}",
            (true, false) => $"PID {worker.ProcessId!.Value}",
            (false, true) => $"Cascade {worker.CascadeId}",
            _ => worker.WorkerId
        };
        return $"{idTag} ({worker.Model}): {worker.TaskDescription}";
    }

    private static string FormatBuildTag(BuildStatus build)
    {
        var idTag = build.ProcessId.HasValue ? $"PID {build.ProcessId.Value}" : build.BuildId;
        return $"{idTag} ({build.Configuration})";
    }
}
