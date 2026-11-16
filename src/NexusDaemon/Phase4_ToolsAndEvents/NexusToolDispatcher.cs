using NexusDaemon.Phase2_StateAndMemory;
using NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;
using NexusDaemon.Phase5_MemoryAndGuardrails;

namespace NexusDaemon.Phase4_ToolsAndEvents;

public sealed record ToolDispatchOutcome(
    string ToolName,
    bool IsAsyncBackgroundTask,
    bool Success,
    string OutputMessage,
    object? StructuredPayload = null);

/// <summary>
/// Phase 4 Tool Dispatcher:
/// Executes DelegateTask, RunLocalBuild, ReadFile, and SwitchModel.
/// DelegateTask and RunLocalBuild update StateManager immediately and run asynchronously
/// in the background so the stateless Nexus router can go to sleep right away.
/// ReadFile executes synchronously so the stateless router can read Architecture.md / Lore.md
/// into its current prompt context before answering.
/// </summary>
public sealed class NexusToolDispatcher
{
    private readonly IAgentRunner _agentRunner;
    private readonly IBuildRunner _buildRunner;
    private readonly ProjectFileReader _fileReader;
    private readonly StateManager _stateManager;

    public NexusToolDispatcher(
        IAgentRunner agentRunner,
        IBuildRunner buildRunner,
        ProjectFileReader fileReader,
        StateManager stateManager)
    {
        _agentRunner = agentRunner;
        _buildRunner = buildRunner;
        _fileReader = fileReader;
        _stateManager = stateManager;
    }

    public async Task<ToolDispatchOutcome> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        return toolName.Trim() switch
        {
            "DelegateTask" => DispatchDelegateTask(arguments, ct),
            "RunLocalBuild" => DispatchRunLocalBuild(arguments, ct),
            "ReadFile" => await DispatchReadFileAsync(arguments, ct),
            "SwitchModel" => DispatchSwitchModel(arguments),
            _ => new ToolDispatchOutcome(
                toolName,
                IsAsyncBackgroundTask: false,
                Success: false,
                OutputMessage: $"Unknown tool '{toolName}'. Supported tools: DelegateTask, RunLocalBuild, ReadFile, SwitchModel.")
        };
    }

    private ToolDispatchOutcome DispatchDelegateTask(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var projectName = GetStringArg(arguments, "projectName", "HordePunkMaidens");
        var prompt = GetStringArg(arguments, "prompt", string.Empty);
        var model = GetOptionalStringArg(arguments, "model");

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ToolDispatchOutcome(
                "DelegateTask",
                IsAsyncBackgroundTask: false,
                Success: false,
                OutputMessage: "DelegateTask requires a non-empty 'prompt' argument.");
        }

        var status = _agentRunner.StartWorkerInBackground(projectName, prompt, model, ct);
        var msg = $"🚀 Delegated background task to Antigravity worker `{status.WorkerId}` on project **{status.ProjectName}** " +
                  $"(Model: `{status.Model}`).\n" +
                  $"Task: _{status.TaskDescription}_\n" +
                  "Nexus is going back to sleep and will send you a summary when `AgentTaskCompletedEvent` fires.";

        return new ToolDispatchOutcome(
            "DelegateTask",
            IsAsyncBackgroundTask: true,
            Success: true,
            OutputMessage: msg,
            StructuredPayload: status);
    }

    private ToolDispatchOutcome DispatchRunLocalBuild(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var projectName = GetStringArg(arguments, "projectName", "HordePunkMaidens");
        var configuration = GetStringArg(arguments, "configuration", "Release");

        var status = _buildRunner.StartBuildInBackground(projectName, configuration, ct);
        var msg = $"🔨 Started headless build `{status.BuildId}` for project **{status.ProjectName}** ({status.Configuration}).\n" +
                  "Nexus is going back to sleep and will report the build result when `BuildCompletedEvent` fires.";

        return new ToolDispatchOutcome(
            "RunLocalBuild",
            IsAsyncBackgroundTask: true,
            Success: true,
            OutputMessage: msg,
            StructuredPayload: status);
    }

    private async Task<ToolDispatchOutcome> DispatchReadFileAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var projectName = GetStringArg(arguments, "projectName", "HordePunkMaidens");
        var relativePath = GetStringArg(arguments, "relativePath", "Architecture.md");

        var readResult = await _fileReader.ReadFileAsync(projectName, relativePath, ct);
        return new ToolDispatchOutcome(
            "ReadFile",
            IsAsyncBackgroundTask: false,
            Success: readResult.Success,
            OutputMessage: readResult.Content,
            StructuredPayload: readResult);
    }

    private ToolDispatchOutcome DispatchSwitchModel(IReadOnlyDictionary<string, object?> arguments)
    {
        var modelId = GetStringArg(arguments, "model", string.Empty);
        var target = GetStringArg(arguments, "target", "both").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return new ToolDispatchOutcome(
                "SwitchModel",
                IsAsyncBackgroundTask: false,
                Success: false,
                OutputMessage: "SwitchModel requires a non-empty 'model' argument.");
        }

        string routerModel = _stateManager.ActiveRouterModel;
        string workerModel = _stateManager.ActiveWorkerModel;

        if (target is "router" or "both" or "all")
        {
            routerModel = _stateManager.SetRouterModel(modelId);
        }

        if (target is "worker" or "both" or "all")
        {
            workerModel = _stateManager.SetWorkerModel(modelId);
        }

        return new ToolDispatchOutcome(
            "SwitchModel",
            IsAsyncBackgroundTask: false,
            Success: true,
            OutputMessage: $"Active models updated — Router: `{routerModel}`, Worker: `{workerModel}`.");
    }

    private static string GetStringArg(IReadOnlyDictionary<string, object?> args, string key, string fallback)
    {
        if (args.TryGetValue(key, out var raw) && raw is not null)
        {
            var str = raw.ToString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str.Trim();
            }
        }

        return fallback;
    }

    private static string? GetOptionalStringArg(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var raw) && raw is not null)
        {
            var str = raw.ToString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str.Trim();
            }
        }

        return null;
    }
}
