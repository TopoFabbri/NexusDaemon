using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;

/// <summary>
/// Selects between AntigravityDaemonRunner (default) and CliStreamJsonRunner based on Runner:Mode.
/// </summary>
public sealed class ConfigurableAgentRunner : IAgentRunner
{
    private readonly NexusOptions _options;
    private readonly AntigravityDaemonRunner _daemonRunner;
    private readonly CliStreamJsonRunner _cliStreamRunner;

    public ConfigurableAgentRunner(
        IOptions<NexusOptions> options,
        AntigravityDaemonRunner daemonRunner,
        CliStreamJsonRunner cliStreamRunner)
    {
        _options = options.Value;
        _daemonRunner = daemonRunner;
        _cliStreamRunner = cliStreamRunner;
    }

    public WorkerStatus StartWorkerInBackground(
        string projectName,
        string prompt,
        string? model = null,
        CancellationToken stoppingToken = default)
    {
        if (string.Equals(_options.Runner.Mode, "CliStreamJson", StringComparison.OrdinalIgnoreCase))
        {
            return _cliStreamRunner.StartWorkerInBackground(projectName, prompt, model, stoppingToken);
        }

        return _daemonRunner.StartWorkerInBackground(projectName, prompt, model, stoppingToken);
    }
}
