namespace NexusDaemon.Phase1_Gateways;

public interface IChatGateway
{
    Task SendTextAsync(string message, CancellationToken ct = default);
    Task SendFileAsync(string filePath, string? caption = null, CancellationToken ct = default);
}
