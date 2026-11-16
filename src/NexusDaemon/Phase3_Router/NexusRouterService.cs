using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase1_Gateways;
using NexusDaemon.Phase2_StateAndMemory;
using NexusDaemon.Phase4_ToolsAndEvents;
using NexusDaemon.Phase5_MemoryAndGuardrails;

namespace NexusDaemon.Phase3_Router;

public sealed record StatelessTurnResult(
    string ReplyText,
    string ModelUsed,
    string SystemInstructionSent,
    IReadOnlyList<string> InvokedTools,
    bool AwaitingHitlApproval);

public sealed record ParsedGeminiResponse(
    string? TextReply,
    string? FunctionCallName,
    IReadOnlyDictionary<string, object?>? FunctionCallArgs,
    JsonNode? RawModelContentNode);

/// <summary>
/// Phase 3 Stateless Nexus Router:
/// Holds NO persistent Gemini chat session. On every user message or background event,
/// constructs a brand-new prompt from scratch (System Prompt + StateManager live snapshot +
/// SlidingWindowQueue last 5-10 interactions), sends a single stateless request to the Gemini API,
/// and closes the connection immediately.
/// </summary>
public sealed class NexusRouterService
{
    public const string HardcodedSystemPrompt =
        """
        You are the Nexus Project Manager, a stateless AI orchestrator managing game development repositories (including HordePunkMaidens and Quantum-Quartz) on the user's PC.
        You operate as a stateless router: every turn injects the live PC System State and the recent sliding-window history.
        Rules:
        1. For coding, refactoring, bug-fixing, or multi-file tasks, call `DelegateTask(projectName, prompt, model, isDestructive)` to spin up a headless Antigravity worker in the background. Set `isDestructive: true` if the task deletes files, resets git state, overwrites scenes/prefabs destructively, or modifies critical build settings.
        2. To compile or build a project headlessly, call `RunLocalBuild(projectName, configuration, isDestructive)`.
        3. Because your sliding window only remembers the last 5-10 turns, whenever the user asks about project architecture, decoupling rules, UI/visual constraints, combat formulas, or world lore, call `ReadFile(projectName, relativePath)` to read `Architecture.md`, `Lore.md`, or relevant files from the project root before answering.
        4. When the user asks to switch models, call `SwitchModel(model, target)`. The default model is `hydrogen-4` ("hydrogen 4"), and all models are supported.
        """;

    private readonly NexusOptions _options;
    private readonly SlidingWindowQueue _slidingWindow;
    private readonly StateManager _stateManager;
    private readonly ModelCatalog _modelCatalog;
    private readonly NexusToolDispatcher _toolDispatcher;
    private readonly HitlApprovalInterceptor _hitlInterceptor;
    private readonly IChatGateway _chatGateway;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NexusRouterService> _logger;

    public NexusRouterService(
        IOptions<NexusOptions> options,
        SlidingWindowQueue slidingWindow,
        StateManager stateManager,
        ModelCatalog modelCatalog,
        NexusToolDispatcher toolDispatcher,
        HitlApprovalInterceptor hitlInterceptor,
        IChatGateway chatGateway,
        IHttpClientFactory httpClientFactory,
        ILogger<NexusRouterService> logger)
    {
        _options = options.Value;
        _slidingWindow = slidingWindow;
        _stateManager = stateManager;
        _modelCatalog = modelCatalog;
        _toolDispatcher = toolDispatcher;
        _hitlInterceptor = hitlInterceptor;
        _chatGateway = chatGateway;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<StatelessTurnResult> HandleUserMessageAsync(
        string userMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        // 1. Check if this message is a Y/N response to a pending HITL approval request
        var (decision, pendingRequest) = _hitlInterceptor.TryEvaluateUserReply(userMessage);
        if (decision != HitlDecision.NotApplicable && pendingRequest is not null)
        {
            return await CompleteHitlDecisionAsync(userMessage, decision, pendingRequest, ct);
        }

        // If a pending approval existed and the user sent a new unrelated command instead of Y/N,
        // cancel the superseded approval so a future "yes" on a later turn cannot trigger a stale destructive action.
        var supersededRequest = _stateManager.ConsumePendingApproval();
        if (supersededRequest is not null)
        {
            _logger.LogInformation(
                "Cancelled superseded HITL request #{RequestId} ({ToolName}) because user sent a new message.",
                supersededRequest.RequestId,
                supersededRequest.ToolName);
        }

        // 2. Assemble stateless prompt from scratch
        var liveStateSummary = await _stateManager.BuildSystemStateSummaryAsync(ct);
        var fullSystemInstruction = $"{HardcodedSystemPrompt}\n\n{liveStateSummary}";
        var historySnapshot = _slidingWindow.GetSnapshot();
        var activeModel = _stateManager.ActiveRouterModel;

        var contents = BuildStatelessContentsArray(historySnapshot, userMessage);
        var invokedTools = new List<string>();

        // Allow up to 3 synchronous ReadFile hops within a single stateless turn;
        // on the final hop (hop == maxSyncToolHops), disable tool declarations so the model synthesizes a text reply.
        const int maxSyncToolHops = 3;
        string finalReplyText = string.Empty;
        string modelUsed = activeModel;

        for (var hop = 0; hop <= maxSyncToolHops; hop++)
        {
            var allowToolsOnHop = hop < maxSyncToolHops;
            var (parsed, resolvedModel) = await SendStatelessGenerateContentAsync(
                activeModel,
                fullSystemInstruction,
                contents,
                includeTools: allowToolsOnHop,
                ct);

            modelUsed = resolvedModel;

            if (string.IsNullOrWhiteSpace(parsed.FunctionCallName))
            {
                finalReplyText = !string.IsNullOrWhiteSpace(parsed.TextReply)
                    ? parsed.TextReply!
                    : "Done.";
                break;
            }

            var toolName = parsed.FunctionCallName!;
            var toolArgs = parsed.FunctionCallArgs ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            invokedTools.Add(toolName);

            // Phase 5 Guardrail: Check HITL approval requirement (`isDestructive: true`)
            if (_hitlInterceptor.RequiresApproval(toolName, toolArgs))
            {
                var projectName = toolArgs.TryGetValue("projectName", out var p) && p is not null
                    ? p.ToString() ?? "HordePunkMaidens"
                    : "HordePunkMaidens";
                var summary = toolArgs.TryGetValue("prompt", out var pr) && pr is not null
                    ? pr.ToString() ?? toolName
                    : $"{toolName} ({string.Join(", ", toolArgs.Select(kv => $"{kv.Key}={kv.Value}"))})";

                var held = _hitlInterceptor.HoldForApproval(toolName, projectName, summary, toolArgs);
                var hitlPrompt = _hitlInterceptor.FormatConfirmationPrompt(held);

                _slidingWindow.EnqueueInteraction(userMessage, hitlPrompt);
                await _chatGateway.SendTextAsync(hitlPrompt, ct);

                return new StatelessTurnResult(
                    ReplyText: hitlPrompt,
                    ModelUsed: modelUsed,
                    SystemInstructionSent: fullSystemInstruction,
                    InvokedTools: invokedTools,
                    AwaitingHitlApproval: true);
            }

            var outcome = await _toolDispatcher.DispatchAsync(toolName, toolArgs, ct);

            // If this is ReadFile, feed the file content back into a stateless follow-up call
            if (string.Equals(toolName, "ReadFile", StringComparison.OrdinalIgnoreCase) && hop < maxSyncToolHops)
            {
                if (parsed.RawModelContentNode is not null)
                {
                    contents.Add(parsed.RawModelContentNode.DeepClone());
                }
                else
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["functionCall"] = new JsonObject
                                {
                                    ["name"] = toolName,
                                    ["args"] = SerializeArgsToJsonObject(toolArgs)
                                }
                            }
                        }
                    });
                }

                contents.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = toolName,
                                ["response"] = new JsonObject
                                {
                                    ["success"] = outcome.Success,
                                    ["content"] = outcome.OutputMessage
                                }
                            }
                        }
                    }
                });

                continue;
            }

            // For DelegateTask, RunLocalBuild, or SwitchModel, we immediately return and let Nexus sleep!
            finalReplyText = outcome.OutputMessage;
            break;
        }

        _slidingWindow.EnqueueInteraction(userMessage, finalReplyText);
        await _chatGateway.SendTextAsync(finalReplyText, ct);

        return new StatelessTurnResult(
            ReplyText: finalReplyText,
            ModelUsed: modelUsed,
            SystemInstructionSent: fullSystemInstruction,
            InvokedTools: invokedTools,
            AwaitingHitlApproval: false);
    }

    /// <summary>
    /// Phase 4 Event Handler: Triggered when an AgentTaskCompletedEvent or BuildCompletedEvent arrives.
    /// Sends a new stateless request to the Nexus injecting the worker/build result and pushes the summary to Telegram.
    /// </summary>
    public async Task<StatelessTurnResult> HandleSystemEventAsync(
        INexusSystemEvent systemEvent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);

        var liveStateSummary = await _stateManager.BuildSystemStateSummaryAsync(ct);
        var fullSystemInstruction = $"{HardcodedSystemPrompt}\n\n{liveStateSummary}";
        var historySnapshot = _slidingWindow.GetSnapshot();
        var eventPrompt = systemEvent.FormatForStatelessRouterPrompt();
        var contents = BuildStatelessContentsArray(historySnapshot, eventPrompt);

        var activeModel = _stateManager.ActiveRouterModel;
        string summaryReply;
        string modelUsed = activeModel;

        try
        {
            var (parsed, resolvedModel) = await SendStatelessGenerateContentAsync(
                activeModel,
                fullSystemInstruction,
                contents,
                includeTools: false,
                ct);

            modelUsed = resolvedModel;
            summaryReply = !string.IsNullOrWhiteSpace(parsed.TextReply)
                ? parsed.TextReply!
                : FormatFallbackEventSummary(systemEvent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Stateless Gemini formatting failed for event {EventType}; using fallback summary.", systemEvent.EventType);
            summaryReply = FormatFallbackEventSummary(systemEvent);
        }

        _slidingWindow.EnqueueInteraction(eventPrompt, summaryReply);

        await _chatGateway.SendTextAsync(summaryReply, ct);

        if (systemEvent is BuildCompletedEvent { Success: false, LogFilePath: { Length: > 0 } logPath } &&
            File.Exists(logPath))
        {
            await _chatGateway.SendFileAsync(logPath, $"Build log for {systemEvent.ProjectName}", ct);
        }

        return new StatelessTurnResult(
            ReplyText: summaryReply,
            ModelUsed: modelUsed,
            SystemInstructionSent: fullSystemInstruction,
            InvokedTools: [],
            AwaitingHitlApproval: false);
    }

    private async Task<StatelessTurnResult> CompleteHitlDecisionAsync(
        string userMessage,
        HitlDecision decision,
        PendingApprovalRequest request,
        CancellationToken ct)
    {
        var liveStateSummary = await _stateManager.BuildSystemStateSummaryAsync(ct);
        var fullSystemInstruction = $"{HardcodedSystemPrompt}\n\n{liveStateSummary}";

        if (decision == HitlDecision.Rejected)
        {
            var cancelMsg = $"🛑 Cancelled destructive action `{request.ToolName}` on **{request.ProjectName}** (Request #{request.RequestId}).";
            _slidingWindow.EnqueueInteraction(userMessage, cancelMsg);
            await _chatGateway.SendTextAsync(cancelMsg, ct);

            return new StatelessTurnResult(
                ReplyText: cancelMsg,
                ModelUsed: _stateManager.ActiveRouterModel,
                SystemInstructionSent: fullSystemInstruction,
                InvokedTools: [],
                AwaitingHitlApproval: false);
        }

        var outcome = await _toolDispatcher.DispatchAsync(request.ToolName, request.Arguments, ct);
        var approvedMsg = $"✅ Confirmed Request #{request.RequestId}.\n{outcome.OutputMessage}";
        _slidingWindow.EnqueueInteraction(userMessage, approvedMsg);
        await _chatGateway.SendTextAsync(approvedMsg, ct);

        return new StatelessTurnResult(
            ReplyText: approvedMsg,
            ModelUsed: _stateManager.ActiveRouterModel,
            SystemInstructionSent: fullSystemInstruction,
            InvokedTools: [request.ToolName],
            AwaitingHitlApproval: false);
    }

    private async Task<(ParsedGeminiResponse Response, string ModelUsed)> SendStatelessGenerateContentAsync(
        string requestedModel,
        string systemInstruction,
        JsonArray contents,
        bool includeTools,
        CancellationToken ct)
    {
        var apiKey = _options.Gemini.ResolveApiKey();
        var normalizedModel = _modelCatalog.NormalizeModelId(requestedModel, _options.Gemini.DefaultModel);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ( new ParsedGeminiResponse(
                $"⚠️ `GEMINI_API_KEY` is not configured. Set `Nexus:Gemini:ApiKey` in `appsettings.json` or the `GEMINI_API_KEY` environment variable.\n\nCurrent {await _stateManager.BuildSystemStateSummaryAsync(ct)}",
                null,
                null,
                null), normalizedModel );
        }

        var payload = BuildGenerateContentPayload(systemInstruction, contents, includeTools);
        using var client = _httpClientFactory.CreateClient("GeminiApi");

        var (statusCode, responseBody) = await PostToGeminiModelEndpointAsync(client, normalizedModel, apiKey, payload, ct);

        // If the primary model is a local/codename/Hub identifier (such as "hydrogen-4" or "gemini-4-hydrogen-high")
        // that returns 404 or 400 on the public REST endpoint, automatically retry with FallbackModel if configured.
        var fallbackModel = string.IsNullOrWhiteSpace(_options.Gemini.FallbackModel)
            ? "gemini-3.1-pro-preview"
            : _options.Gemini.FallbackModel;
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            var normalizedFallback = _modelCatalog.NormalizeModelId(fallbackModel);
            if (!string.Equals(normalizedModel, normalizedFallback, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Model '{PrimaryModel}' returned {StatusCode} on REST endpoint; falling back to '{FallbackModel}'.",
                    normalizedModel,
                    (int)statusCode,
                    normalizedFallback);

                (statusCode, responseBody) = await PostToGeminiModelEndpointAsync(client, normalizedFallback, apiKey, payload, ct);
                normalizedModel = normalizedFallback;
            }
        }

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new HttpRequestException(
                $"Gemini API request for model '{normalizedModel}' failed with HTTP {(int)statusCode} ({statusCode}): {responseBody}",
                null,
                statusCode);
        }

        return (ParseGeminiResponseJson(responseBody), normalizedModel);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostToGeminiModelEndpointAsync(
        HttpClient client,
        string modelId,
        string apiKey,
        string jsonPayload,
        CancellationToken ct)
    {
        var baseUrl = _options.Gemini.BaseUrl.TrimEnd('/');
        var cleanModel = modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;

        var url = $"{baseUrl}/models/{Uri.EscapeDataString(cleanModel)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, requestContent, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    public static JsonArray BuildStatelessContentsArray(
        IReadOnlyList<ChatMessageEntry> slidingWindowHistory,
        string currentMessage)
    {
        var contents = new JsonArray();

        foreach (var entry in slidingWindowHistory)
        {
            AppendOrCoalesceContentTurn(contents, entry.Role, entry.Content);
        }

        AppendOrCoalesceContentTurn(contents, "user", currentMessage);

        return contents;
    }

    private static void AppendOrCoalesceContentTurn(JsonArray contents, string role, string text)
    {
        if (contents.Count > 0 &&
            contents[^1] is JsonObject lastObj &&
            string.Equals(lastObj["role"]?.GetValue<string>(), role, StringComparison.OrdinalIgnoreCase) &&
            lastObj["parts"] is JsonArray existingParts)
        {
            existingParts.Add(new JsonObject { ["text"] = text });
            return;
        }

        contents.Add(new JsonObject
        {
            ["role"] = role,
            ["parts"] = new JsonArray
            {
                new JsonObject { ["text"] = text }
            }
        });
    }

    public static string BuildGenerateContentPayload(
        string systemInstruction,
        JsonArray contents,
        bool includeTools)
    {
        var root = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = systemInstruction }
                }
            },
            ["contents"] = contents.DeepClone()
        };

        if (includeTools)
        {
            root["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["functionDeclarations"] = BuildToolDeclarations()
                }
            };
        }

        return root.ToJsonString();
    }

    public static ParsedGeminiResponse ParseGeminiResponseJson(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return new ParsedGeminiResponse(null, null, null, null);
        }

        var firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out var contentElem) ||
            contentElem.ValueKind != JsonValueKind.Object)
        {
            return new ParsedGeminiResponse(null, null, null, null);
        }

        var rawContentNode = JsonNode.Parse(contentElem.GetRawText());
        if (!contentElem.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return new ParsedGeminiResponse(null, null, null, rawContentNode);
        }

        var textBuilder = new StringBuilder();
        string? functionName = null;
        Dictionary<string, object?>? functionArgs = null;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fnCall) &&
                fnCall.ValueKind == JsonValueKind.Object)
            {
                if (fnCall.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    functionName = nameProp.GetString();
                }

                functionArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (fnCall.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsProp.EnumerateObject())
                    {
                        functionArgs[prop.Name] = ConvertJsonElementToPrimitive(prop.Value);
                    }
                }

                break;
            }

            if (part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.AppendLine();
                }

                textBuilder.Append(textProp.GetString());
            }
        }

        var textReply = textBuilder.Length > 0 ? textBuilder.ToString() : null;
        return new ParsedGeminiResponse(textReply, functionName, functionArgs, rawContentNode);
    }

    private static JsonArray BuildToolDeclarations()
    {
        return
        [
            new JsonObject
            {
                ["name"] = "DelegateTask",
                ["description"] = "Updates the StateManager and spins up a headless Antigravity worker in the background on the specified project.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Target project name (e.g., HordePunkMaidens or Quantum-Quartz)."
                        },
                        ["prompt"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Detailed task instructions for the headless Antigravity worker."
                        },
                        ["model"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Optional model ID for the worker (defaults to hydrogen-4; any model is supported)."
                        },
                        ["isDestructive"] = new JsonObject
                        {
                            ["type"] = "BOOLEAN",
                            ["description"] = "Set true if the task performs destructive file/git/scene modifications requiring HITL Y/N confirmation."
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("projectName"), JsonValue.Create("prompt"))
                }
            },
            new JsonObject
            {
                ["name"] = "RunLocalBuild",
                ["description"] = "Updates the StateManager and triggers a headless Unity or .NET build for the target project in the background.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Target project name (e.g., HordePunkMaidens or Quantum-Quartz)."
                        },
                        ["configuration"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Build configuration (Release, Debug, or CompileCheck)."
                        },
                        ["isDestructive"] = new JsonObject
                        {
                            ["type"] = "BOOLEAN",
                            ["description"] = "Set true if this build requires HITL Y/N confirmation."
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("projectName"))
                }
            },
            new JsonObject
            {
                ["name"] = "ReadFile",
                ["description"] = "Reads a markdown or source file (such as Architecture.md or Lore.md) from the project root into the current stateless prompt context.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Target project name (e.g., HordePunkMaidens)."
                        },
                        ["relativePath"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Relative path inside the project (e.g., Architecture.md, Lore.md, or Assets/Scripts/...)."
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("projectName"), JsonValue.Create("relativePath"))
                }
            },
            new JsonObject
            {
                ["name"] = "SwitchModel",
                ["description"] = "Switches the active model for the Nexus router, worker, or both. Any model ID is supported (default: hydrogen-4).",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["model"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "The model ID to switch to (e.g., hydrogen-4, gemini-3.1-pro-preview, gemini-2.5-flash, or any model ID)."
                        },
                        ["target"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Which target to update: 'router', 'worker', or 'both'."
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("model"))
                }
            }
        ];
    }

    private static JsonObject SerializeArgsToJsonObject(IReadOnlyDictionary<string, object?> args)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in args)
        {
            obj[k] = v switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(v.ToString())
            };
        }

        return obj;
    }

    private static object? ConvertJsonElementToPrimitive(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static string FormatFallbackEventSummary(INexusSystemEvent systemEvent)
    {
        return systemEvent switch
        {
            AgentTaskCompletedEvent agentEvt =>
                $"📬 **Worker Task {(agentEvt.Success ? "Completed" : "Failed")}**\n" +
                $"• Worker: `{agentEvt.WorkerId}` (Model: `{agentEvt.Model}`)\n" +
                $"• Project: **{agentEvt.ProjectName}**\n" +
                $"• Duration: {agentEvt.Duration.TotalSeconds:F1}s\n" +
                $"• Output:\n```json\n{agentEvt.JsonResult}\n```",

            BuildCompletedEvent buildEvt =>
                $"📦 **Build {(buildEvt.Success ? "Succeeded" : "Failed")}**\n" +
                $"• Build: `{buildEvt.BuildId}` ({buildEvt.Configuration})\n" +
                $"• Project: **{buildEvt.ProjectName}**\n" +
                $"• Exit Code: `{buildEvt.ExitCode}` ({buildEvt.Duration.TotalSeconds:F1}s)\n" +
                $"• Summary:\n```\n{buildEvt.LogSummary}\n```",

            _ => $"System event `{systemEvent.EventType}` on `{systemEvent.ProjectName}` completed."
        };
    }
}
