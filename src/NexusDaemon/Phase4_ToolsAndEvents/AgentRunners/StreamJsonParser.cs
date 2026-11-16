using System.Text;
using System.Text.Json;

namespace NexusDaemon.Phase4_ToolsAndEvents.AgentRunners;

public sealed record StreamJsonParseOutcome(
    bool IsTerminalFound,
    bool IsSuccess,
    string FinalResultJson,
    int LinesProcessed);

/// <summary>
/// Phase 4 Stream Parser: Reads stdout from a headless `agy --input-format stream-json` process,
/// parses NDJSON frames, and detects when the terminal completion state arrives.
/// </summary>
public static class StreamJsonParser
{
    public static async Task<StreamJsonParseOutcome> ParseStreamToCompletionAsync(
        TextReader reader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var linesProcessed = 0;
        string? lastValidJsonLine = null;
        var collectedMessages = new List<string>();
        var rawFallback = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            linesProcessed++;
            var trimmed = line.Trim();
            rawFallback.AppendLine(trimmed);

            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                lastValidJsonLine = trimmed;

                if (TryExtractTextDelta(root, out var textChunk) && !string.IsNullOrWhiteSpace(textChunk))
                {
                    collectedMessages.Add(textChunk);
                }

                if (IsTerminalEvent(root, out var isSuccess))
                {
                    return new StreamJsonParseOutcome(
                        IsTerminalFound: true,
                        IsSuccess: isSuccess,
                        FinalResultJson: trimmed,
                        LinesProcessed: linesProcessed);
                }
            }
            catch (JsonException)
            {
                // Ignore malformed non-JSON log lines mixed into stdout
            }
        }

        if (lastValidJsonLine is not null)
        {
            return new StreamJsonParseOutcome(
                IsTerminalFound: false,
                IsSuccess: true,
                FinalResultJson: lastValidJsonLine,
                LinesProcessed: linesProcessed);
        }

        var synthesized = JsonSerializer.Serialize(new
        {
            type = "result",
            status = "eof",
            output = collectedMessages.Count > 0
                ? string.Join("\n", collectedMessages)
                : rawFallback.ToString().Trim()
        });

        return new StreamJsonParseOutcome(
            IsTerminalFound: false,
            IsSuccess: linesProcessed > 0,
            FinalResultJson: synthesized,
            LinesProcessed: linesProcessed);
    }

    public static bool IsTerminalEvent(JsonElement root, out bool isSuccess)
    {
        isSuccess = true;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("is_error", out var isErrProp) &&
            isErrProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            isSuccess = !isErrProp.GetBoolean();
        }
        else if (root.TryGetProperty("success", out var successProp) &&
                 successProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            isSuccess = successProp.GetBoolean();
        }

        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            var typeVal = typeProp.GetString()?.ToLowerInvariant();
            if (typeVal is "result" or "final" or "complete" or "completed" or "turn_complete" or "done")
            {
                if (root.TryGetProperty("subtype", out var subProp) &&
                    subProp.ValueKind == JsonValueKind.String &&
                    string.Equals(subProp.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    isSuccess = false;
                }

                return true;
            }

            if (typeVal is "error" or "fatal")
            {
                isSuccess = false;
                return true;
            }
        }

        if (root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
        {
            var statusVal = statusProp.GetString()?.ToLowerInvariant();
            if (statusVal is "completed" or "complete" or "idle" or "succeeded" or "done")
            {
                return true;
            }

            if (statusVal is "failed" or "error" or "cancelled" or "canceled")
            {
                isSuccess = false;
                return true;
            }
        }

        if (root.TryGetProperty("done", out var doneProp) && doneProp.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractTextDelta(JsonElement root, out string? text)
    {
        text = null;
        if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.String)
        {
            text = resultProp.GetString();
            return true;
        }

        if (root.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
        {
            text = contentProp.GetString();
            return true;
        }

        return false;
    }
}
