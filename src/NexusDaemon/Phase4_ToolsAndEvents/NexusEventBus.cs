using System.Threading.Channels;

namespace NexusDaemon.Phase4_ToolsAndEvents;

public interface INexusSystemEvent
{
    string EventType { get; }
    string ProjectName { get; }
    DateTimeOffset OccurredAtUtc { get; }
    string FormatForStatelessRouterPrompt();
}

public sealed record AgentTaskCompletedEvent(
    string WorkerId,
    int? ProcessId,
    string? CascadeId,
    string ProjectName,
    string TaskDescription,
    string Model,
    bool Success,
    string JsonResult,
    TimeSpan Duration,
    DateTimeOffset OccurredAtUtc) : INexusSystemEvent
{
    public string EventType => nameof(AgentTaskCompletedEvent);

    public string FormatForStatelessRouterPrompt()
    {
        var pidTag = ProcessId.HasValue ? $"PID {ProcessId.Value}" : CascadeId ?? WorkerId;
        var status = Success ? "COMPLETED SUCCESSFULLY" : "FAILED";
        return $"[SYSTEM EVENT: AgentTaskCompletedEvent]\n" +
               $"Worker: {WorkerId} ({pidTag})\n" +
               $"Project: {ProjectName}\n" +
               $"Model: {Model}\n" +
               $"Task: {TaskDescription}\n" +
               $"Status: {status} (Duration: {Duration.TotalSeconds:F1}s)\n" +
               $"Worker JSON Result:\n{JsonResult}\n\n" +
               "Please format a clear, concise summary of this completed worker task for the user on Telegram.";
    }
}

public sealed record BuildCompletedEvent(
    string BuildId,
    int? ProcessId,
    string ProjectName,
    string Configuration,
    bool Success,
    int ExitCode,
    string LogSummary,
    string? LogFilePath,
    TimeSpan Duration,
    DateTimeOffset OccurredAtUtc) : INexusSystemEvent
{
    public string EventType => nameof(BuildCompletedEvent);

    public string FormatForStatelessRouterPrompt()
    {
        var status = Success ? "BUILD SUCCEEDED" : $"BUILD FAILED (Exit Code {ExitCode})";
        return $"[SYSTEM EVENT: BuildCompletedEvent]\n" +
               $"BuildId: {BuildId} (PID: {ProcessId?.ToString() ?? "n/a"})\n" +
               $"Project: {ProjectName} ({Configuration})\n" +
               $"Status: {status} (Duration: {Duration.TotalSeconds:F1}s)\n" +
               $"Build Output Summary:\n{LogSummary}\n\n" +
               "Please format a clear, concise summary of this build result for the user on Telegram.";
    }
}

/// <summary>
/// Phase 4 Event Bus: Publishes background worker and build completion events
/// to trigger new stateless requests to the Nexus router.
/// </summary>
public sealed class NexusEventBus
{
    private readonly Channel<INexusSystemEvent> _channel = Channel.CreateUnbounded<INexusSystemEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    public ChannelReader<INexusSystemEvent> Reader => _channel.Reader;

    public ValueTask PublishAsync(INexusSystemEvent systemEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        return _channel.Writer.WriteAsync(systemEvent, ct);
    }

    public bool TryPublish(INexusSystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        return _channel.Writer.TryWrite(systemEvent);
    }
}
