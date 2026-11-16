using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;

public interface IAgentRunner
{
    /// <summary>
    /// Registers the worker in StateManager and launches the background task without awaiting completion,
    /// allowing the stateless Nexus router to go back to sleep immediately.
    /// </summary>
    WorkerStatus StartWorkerInBackground(
        string projectName,
        string prompt,
        string? model = null,
        CancellationToken stoppingToken = default);
}
