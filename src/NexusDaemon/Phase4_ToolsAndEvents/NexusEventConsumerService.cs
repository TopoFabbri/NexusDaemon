using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusDaemon.Phase3_Router;

namespace NexusDaemon.Phase4_ToolsAndEvents;

/// <summary>
/// Phase 4 Background Event Consumer:
/// Listens for AgentTaskCompletedEvent and BuildCompletedEvent on NexusEventBus
/// and triggers a new stateless request on NexusRouterService.
/// </summary>
public sealed class NexusEventConsumerService : BackgroundService
{
    private readonly NexusEventBus _eventBus;
    private readonly NexusRouterService _routerService;
    private readonly ILogger<NexusEventConsumerService> _logger;

    public NexusEventConsumerService(
        NexusEventBus eventBus,
        NexusRouterService routerService,
        ILogger<NexusEventConsumerService> logger)
    {
        _eventBus = eventBus;
        _routerService = routerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var systemEvent in _eventBus.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _routerService.HandleSystemEventAsync(systemEvent, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Error handling system event {EventType} for project {ProjectName}",
                    systemEvent.EventType,
                    systemEvent.ProjectName);
            }
        }
    }
}
