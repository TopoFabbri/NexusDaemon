using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase2_StateAndMemory;
using NexusDaemon.Phase3_Router;

namespace NexusDaemon.Phase1_Gateways;

/// <summary>
/// Phase 1 Chat Interface (The Director):
/// Telegram Bot poller and sender that receives raw text commands from the authorized user,
/// routes them to the stateless NexusRouterService (or handles quick /model, /models, /status commands),
/// and sends text/file responses back to Telegram.
/// </summary>
public sealed class TelegramChatGateway : BackgroundService, IChatGateway
{
    public const int MaxTelegramMessageLength = 4000;

    private readonly NexusOptions _options;
    private readonly StateManager _stateManager;
    private readonly SlidingWindowQueue _slidingWindow;
    private readonly ModelCatalog _modelCatalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramChatGateway> _logger;

    private long _lastUpdateOffset;

    public TelegramChatGateway(
        IOptions<NexusOptions> options,
        StateManager stateManager,
        SlidingWindowQueue slidingWindow,
        ModelCatalog modelCatalog,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        ILogger<TelegramChatGateway> logger)
    {
        _options = options.Value;
        _stateManager = stateManager;
        _slidingWindow = slidingWindow;
        _modelCatalog = modelCatalog;
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task SendTextAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var token = _options.Telegram.ResolveBotToken();
        var chatId = _options.Telegram.AllowedChatId;
        if (string.IsNullOrWhiteSpace(token) || chatId == 0)
        {
            _logger.LogInformation("[Telegram Output Preview] {Message}", message);
            return;
        }

        using var client = _httpClientFactory.CreateClient("TelegramApi");
        foreach (var chunk in ChunkMessage(message, MaxTelegramMessageLength))
        {
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text = chunk
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            using var response = await client.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Telegram sendMessage returned {StatusCode}: {Error}", response.StatusCode, err);
            }
        }
    }

    public async Task SendFileAsync(string filePath, string? caption = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var token = _options.Telegram.ResolveBotToken();
        var chatId = _options.Telegram.AllowedChatId;
        if (string.IsNullOrWhiteSpace(token) || chatId == 0)
        {
            _logger.LogInformation("[Telegram File Preview] {FilePath} ({Caption})", filePath, caption);
            return;
        }

        using var client = _httpClientFactory.CreateClient("TelegramApi");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString()), "chat_id");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }

        await using var fileStream = File.OpenRead(filePath);
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(streamContent, "document", Path.GetFileName(filePath));

        var url = $"https://api.telegram.org/bot{token}/sendDocument";
        using var response = await client.PostAsync(url, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram sendDocument returned {StatusCode}: {Error}", response.StatusCode, err);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Telegram.Enabled)
        {
            _logger.LogInformation("TelegramChatGateway is disabled in configuration.");
            return;
        }

        var token = _options.Telegram.ResolveBotToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning(
                "Telegram BotToken is not configured yet (set Nexus:Telegram:BotToken or TELEGRAM_BOT_TOKEN). " +
                "Telegram polling is idle until a token is provided.");
            return;
        }

        _logger.LogInformation(
            "TelegramChatGateway started polling (AllowedChatId={AllowedChatId}, DefaultModel={DefaultModel}).",
            _options.Telegram.AllowedChatId,
            _stateManager.ActiveRouterModel);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(token, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Telegram getUpdates polling.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            if (_options.Telegram.PollingIntervalMs > 0)
            {
                await Task.Delay(_options.Telegram.PollingIntervalMs, stoppingToken);
            }
        }
    }

    public async Task PollOnceAsync(string botToken, CancellationToken ct = default)
    {
        using var client = _httpClientFactory.CreateClient("TelegramApi");
        var timeoutSec = Math.Clamp(_options.Telegram.LongPollTimeoutSeconds, 0, 50);
        var url = $"https://api.telegram.org/bot{botToken}/getUpdates?offset={_lastUpdateOffset}&timeout={timeoutSec}";

        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram getUpdates returned {StatusCode}: {Body}", response.StatusCode, body);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            return;
        }

        if (!root.TryGetProperty("result", out var resultArray) || resultArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var router = _serviceProvider.GetRequiredService<NexusRouterService>();

        foreach (var update in resultArray.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var idProp) && idProp.TryGetInt64(out var updateId))
            {
                _lastUpdateOffset = Math.Max(_lastUpdateOffset, updateId + 1);
            }

            if (!update.TryGetProperty("message", out var msgElem) || msgElem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!msgElem.TryGetProperty("chat", out var chatElem) ||
                !chatElem.TryGetProperty("id", out var chatIdProp) ||
                !chatIdProp.TryGetInt64(out var chatId))
            {
                continue;
            }

            if (!msgElem.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = textProp.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await ProcessIncomingMessageAsync(chatId, text, router, ct);
            }
        }
    }

    public async Task ProcessIncomingMessageAsync(
        long chatId,
        string text,
        NexusRouterService router,
        CancellationToken ct = default)
    {
        if (_options.Telegram.AllowedChatId != 0 && chatId != _options.Telegram.AllowedChatId)
        {
            _logger.LogWarning("Ignored Telegram message from unauthorized chat_id {ChatId}", chatId);
            return;
        }

        var trimmed = text.Trim();

        // Built-in Director Slash Commands
        if (trimmed.StartsWith('/'))
        {
            var handled = await TryHandleSlashCommandAsync(trimmed, ct);
            if (handled)
            {
                return;
            }
        }

        try
        {
            await router.HandleUserMessageAsync(trimmed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling Telegram user message.");
            await SendTextAsync($"⚠️ Error processing request: {ex.Message}", ct);
        }
    }

    private async Task<bool> TryHandleSlashCommandAsync(string commandText, CancellationToken ct)
    {
        var parts = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (cmd)
        {
            case "/start":
            case "/help":
            {
                var help =
                    "🤖 **Nexus Stateless Daemon (The Director)**\n\n" +
                    $"• **Active Router Model**: `{_stateManager.ActiveRouterModel}`\n" +
                    $"• **Active Worker Model**: `{_stateManager.ActiveWorkerModel}`\n" +
                    $"• **Runner Mode**: `{_options.Runner.Mode}`\n\n" +
                    "**Commands**:\n" +
                    "• `/status` — Show live PC state, active workers, builds, and Git status\n" +
                    "• `/model <model_id>` — Switch both Router & Worker to any model (e.g. `/model hydrogen 4`, `/model gemini-3.1-pro-preview`, `/model gemini-2.5-flash`)\n" +
                    "• `/router-model <model_id>` — Switch only the stateless router model\n" +
                    "• `/worker-model <model_id>` — Switch only the headless worker model\n" +
                    "• `/models` — List active and known models\n" +
                    "• `/projects` — List registered projects\n" +
                    "• `/clear` — Clear the 5-10 turn sliding window queue";
                await SendTextAsync(help, ct);
                return true;
            }

            case "/status":
            {
                var stateSummary = await _stateManager.BuildSystemStateSummaryAsync(ct);
                await SendTextAsync(stateSummary, ct);
                return true;
            }

            case "/model":
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendTextAsync(
                        $"Current models:\n• Router: `{_stateManager.ActiveRouterModel}`\n• Worker: `{_stateManager.ActiveWorkerModel}`\n\n" +
                        "Usage: `/model <any_model_id>` (e.g. `/model hydrogen 4` or `/model gemini-3.1-pro-preview`)",
                        ct);
                    return true;
                }

                var newRouter = _stateManager.SetRouterModel(arg);
                var newWorker = _stateManager.SetWorkerModel(arg);
                await SendTextAsync($"✅ Switched active models to `{newRouter}` (Router: `{newRouter}`, Worker: `{newWorker}`).", ct);
                return true;
            }

            case "/router-model":
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendTextAsync($"Current Router Model: `{_stateManager.ActiveRouterModel}`", ct);
                    return true;
                }

                var updated = _stateManager.SetRouterModel(arg);
                await SendTextAsync($"✅ Switched Router model to `{updated}`.", ct);
                return true;
            }

            case "/worker-model":
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendTextAsync($"Current Worker Model: `{_stateManager.ActiveWorkerModel}`", ct);
                    return true;
                }

                var updated = _stateManager.SetWorkerModel(arg);
                await SendTextAsync($"✅ Switched Worker model to `{updated}`.", ct);
                return true;
            }

            case "/models":
            {
                var known = _modelCatalog.GetKnownModels();
                var msg =
                    $"🧠 **Model Configuration**\n" +
                    $"• Active Router Model: `{_stateManager.ActiveRouterModel}`\n" +
                    $"• Active Worker Model: `{_stateManager.ActiveWorkerModel}`\n" +
                    $"• Default Model: `{_options.Gemini.DefaultModel}`\n\n" +
                    $"**Preset / Alias Models**:\n" +
                    string.Join("\n", known.Select(m => $"• `{m}`")) +
                    "\n\n_Note: You can pass **any** model ID to `/model <model_id>` or in natural language._";
                await SendTextAsync(msg, ct);
                return true;
            }

            case "/projects":
            {
                var lines = _options.Projects.Select(kv =>
                    $"• **{kv.Key}**: `{kv.Value.RootPath}` (Unity: `{kv.Value.GetEffectiveUnityProjectPath()}`)");
                await SendTextAsync("📂 **Registered Projects**:\n" + string.Join("\n", lines), ct);
                return true;
            }

            case "/clear":
            {
                _slidingWindow.Clear();
                _stateManager.ConsumePendingApproval();
                await SendTextAsync("🧹 Cleared the sliding window conversation queue and any pending HITL approval.", ct);
                return true;
            }

            default:
                return false;
        }
    }

    public static IReadOnlyList<string> ChunkMessage(string text, int maxChunkSize = MaxTelegramMessageLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text.Length <= maxChunkSize)
        {
            return [text];
        }

        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var len = Math.Min(maxChunkSize, text.Length - offset);
            chunks.Add(text.Substring(offset, len));
            offset += len;
        }

        return chunks;
    }
}
