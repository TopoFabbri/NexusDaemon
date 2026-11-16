using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;

namespace NexusDaemon.Phase2_StateAndMemory;

/// <summary>
/// Normalizes and resolves model identifiers while allowing any model ID to be used.
/// Defaults to "hydrogen-4" ("hydrogen 4").
/// </summary>
public sealed class ModelCatalog
{
    private readonly GeminiOptions _geminiOptions;

    public ModelCatalog(IOptions<NexusOptions> options)
    {
        _geminiOptions = options.Value.Gemini;
    }

    public string NormalizeModelId(string? requestedModel, string fallbackDefault = "hydrogen-4")
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return ResolveAlias(fallbackDefault);
        }

        var trimmed = requestedModel.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["models/".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ResolveAlias(fallbackDefault);
        }

        return ResolveAlias(trimmed);
    }

    /// <summary>
    /// Resolves a canonical or user-supplied model ID to the exact `--model=` value expected by
    /// Antigravity's `agentapi.bat new-conversation`. In Antigravity v2.32+, "Gemini 4 Hydrogen"
    /// uses modelId `gemini-4-hydrogen-high` (or `-xhigh`, `-medium`, `-low`); passing bare `hydrogen-4`
    /// to `agentapi.bat` would otherwise be unrecognized and downgrade to the `flash` tier.
    /// </summary>
    public string ResolveAntigravityCliModelArgument(string? requestedModel)
    {
        var normalized = NormalizeModelId(requestedModel);
        return normalized.ToLowerInvariant() switch
        {
            "hydrogen-4" or "hydrogen" or "gemini-4-hydrogen" => "gemini-4-hydrogen-high",
            _ => normalized
        };
    }

    private string ResolveAlias(string modelInput)
    {
        if (_geminiOptions.ModelAliases.TryGetValue(modelInput, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim();
        }

        // Normalize spaces to hyphens (e.g., "hydrogen 4" -> "hydrogen-4") while preserving arbitrary model IDs
        if (modelInput.Contains(' ', StringComparison.Ordinal))
        {
            var hyphenated = string.Join('-', modelInput.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (_geminiOptions.ModelAliases.TryGetValue(hyphenated, out var mappedHyphenated) &&
                !string.IsNullOrWhiteSpace(mappedHyphenated))
            {
                return mappedHyphenated.Trim();
            }

            return hyphenated.ToLowerInvariant();
        }

        return modelInput;
    }

    public IReadOnlyList<string> GetKnownModels()
    {
        return _geminiOptions.KnownModels
            .Select(m => NormalizeModelId(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
