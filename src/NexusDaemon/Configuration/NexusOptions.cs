namespace NexusDaemon.Configuration;

public sealed class NexusOptions
{
    public const string SectionName = "Nexus";

    public GeminiOptions Gemini { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public RunnerOptions Runner { get; set; } = new();
    public Dictionary<string, ProjectOptions> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// Default model for the stateless Nexus router. Defaults to "hydrogen-4" ("hydrogen 4"),
    /// while allowing any model ID to be used or switched at runtime.
    /// </summary>
    public string DefaultModel { get; set; } = "hydrogen-4";

    /// <summary>
    /// Optional fallback model if the upstream REST endpoint returns 404 for a local/codename model ID.
    /// </summary>
    public string? FallbackModel { get; set; } = "gemini-3.1-pro-preview";

    /// <summary>
    /// Number of messages retained in the sliding window (constrained to 5-10 interactions / up to 20 messages).
    /// </summary>
    public int SlidingWindowSize { get; set; } = 10;

    /// <summary>
    /// Friendly model aliases mapped to their canonical model identifiers.
    /// Any model not in this map is passed through verbatim so all models can be used.
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hydrogen 4"] = "hydrogen-4",
        ["hydrogen-4"] = "hydrogen-4",
        ["hydrogen4"] = "hydrogen-4",
        ["hydrogen"] = "hydrogen-4",
        ["gemini 4 hydrogen"] = "gemini-4-hydrogen-high",
        ["gemini 4 hydrogen (high)"] = "gemini-4-hydrogen-high",
        ["hydrogen 4 high"] = "gemini-4-hydrogen-high",
        ["hydrogen-4-high"] = "gemini-4-hydrogen-high",
        ["gemini 4 hydrogen (xhigh)"] = "gemini-4-hydrogen-xhigh",
        ["hydrogen 4 xhigh"] = "gemini-4-hydrogen-xhigh",
        ["hydrogen-4-xhigh"] = "gemini-4-hydrogen-xhigh",
        ["gemini 4 hydrogen (medium)"] = "gemini-4-hydrogen-medium",
        ["hydrogen 4 medium"] = "gemini-4-hydrogen-medium",
        ["hydrogen-4-medium"] = "gemini-4-hydrogen-medium",
        ["gemini 4 hydrogen (low)"] = "gemini-4-hydrogen-low",
        ["hydrogen 4 low"] = "gemini-4-hydrogen-low",
        ["hydrogen-4-low"] = "gemini-4-hydrogen-low",
        ["argon 4"] = "gemini-4-argon-high",
        ["argon-4"] = "gemini-4-argon-high",
        ["gemini 4 argon"] = "gemini-4-argon-high",
        ["gemini 4 argon (high)"] = "gemini-4-argon-high",
        ["gemini 4 argon (medium)"] = "gemini-4-argon-medium",
        ["gemini 4 argon (low)"] = "gemini-4-argon-low",
        ["orchestrator"] = "gemini-pro-orchestrator-g1-pro",
        ["gemini 3.8 flash (high)"] = "gemini-3.8-flash-high",
        ["gemini 3.8 flash (medium)"] = "gemini-3.8-flash-medium",
        ["gemini 3.8 flash (low)"] = "gemini-3.8-flash-low",
        ["claude opus 5.5"] = "claude-opus-5-5-high",
        ["claude opus 5.5 (high)"] = "claude-opus-5-5-high",
        ["claude opus 5.5 (medium)"] = "claude-opus-5-5-medium",
        ["claude opus 5.5 (low)"] = "claude-opus-5-5-low",
        ["claude sonnet 5.5"] = "claude-sonnet-5-5-high",
        ["claude sonnet 5.5 (high)"] = "claude-sonnet-5-5-high",
        ["claude sonnet 5.5 (medium)"] = "claude-sonnet-5-5-medium",
        ["claude sonnet 5.5 (low)"] = "claude-sonnet-5-5-low",
        ["gpt-oss"] = "gpt-oss-120b-medium",
        ["gpt-oss 120b"] = "gpt-oss-120b-medium",
        ["pro"] = "gemini-3.1-pro-preview",
        ["gemini-pro"] = "gemini-3.1-pro-preview",
        ["gemini pro"] = "gemini-3.1-pro-preview",
        ["gemini-2.5-pro"] = "gemini-3.1-pro-preview",
        ["gemini 2.5 pro"] = "gemini-3.1-pro-preview",
        ["2.5-pro"] = "gemini-3.1-pro-preview",
        ["2.5 pro"] = "gemini-3.1-pro-preview",
        ["gemini-3.1-pro"] = "gemini-3.1-pro-preview",
        ["gemini 3.1 pro"] = "gemini-3.1-pro-preview",
        ["flash"] = "gemini-2.5-flash",
        ["flash_lite"] = "gemini-2.5-flash-lite",
        ["flash-lite"] = "gemini-2.5-flash-lite",
        ["gemma 4"] = "gemma-4",
        ["gemma-4"] = "gemma-4"
    };

    /// <summary>
    /// Known/suggested models shown in /models (any other model ID is also accepted).
    /// Includes both Antigravity Hub models (GetCascadeModelConfigData) and Gemini REST API models.
    /// </summary>
    public List<string> KnownModels { get; set; } =
    [
        "hydrogen-4",
        "gemini-4-hydrogen-high",
        "gemini-4-hydrogen-xhigh",
        "gemini-4-hydrogen-medium",
        "gemini-4-hydrogen-low",
        "gemini-4-argon-high",
        "gemini-4-argon-medium",
        "gemini-4-argon-low",
        "gemini-pro-orchestrator-g1-pro",
        "gemini-3.8-flash-high",
        "gemini-3.8-flash-medium",
        "gemini-3.8-flash-low",
        "claude-opus-5-5-high",
        "claude-opus-5-5-medium",
        "claude-opus-5-5-low",
        "claude-sonnet-5-5-high",
        "claude-sonnet-5-5-medium",
        "claude-sonnet-5-5-low",
        "gpt-oss-120b-medium",
        "gemini-4-hydrogen",
        "gemini-3.1-pro-preview",
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-2.0-flash",
        "gemma-4",
        "pro",
        "flash",
        "flash_lite"
    ];

    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey.Trim();
        }

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        return envKey?.Trim() ?? string.Empty;
    }
}

public sealed class TelegramOptions
{
    public bool Enabled { get; set; } = true;
    public string BotToken { get; set; } = string.Empty;
    public long AllowedChatId { get; set; } = 6622366604;
    public int PollingIntervalMs { get; set; } = 1000;
    public int LongPollTimeoutSeconds { get; set; } = 20;

    public string ResolveBotToken()
    {
        if (!string.IsNullOrWhiteSpace(BotToken))
        {
            return BotToken.Trim();
        }

        return Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? string.Empty;
    }
}

public sealed class RunnerOptions
{
    /// <summary>
    /// Execution mode: "AntigravityDaemon" (default, uses agentapi.bat + local Hub RPC)
    /// or "CliStreamJson" (uses agy --input-format stream-json).
    /// </summary>
    public string Mode { get; set; } = "AntigravityDaemon";

    /// <summary>
    /// Default model passed to headless worker conversations ("hydrogen-4" by default, supports any model).
    /// </summary>
    public string DefaultWorkerModel { get; set; } = "hydrogen-4";

    public string AgentApiBatchPath { get; set; } = @"C:\Users\Administrator\.gemini\antigravity\bin\agentapi.bat";

    private string _daemonDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "daemon");
    public string DaemonDirectory
    {
        get => string.IsNullOrWhiteSpace(_daemonDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "daemon")
            : _daemonDirectory;
        set => _daemonDirectory = value;
    }

    public string? LsAddress { get; set; }
    public string? CsrfToken { get; set; }

    public string AgyExecutablePath { get; set; } = "agy";
    public string AgyArgumentsTemplate { get; set; } = "--input-format stream-json";
    public string GitExecutablePath { get; set; } = @"C:\Users\Administrator\AppData\Local\Fork\gitInstance\2.50.1\bin\git.exe";
    public int WorkerTimeoutSeconds { get; set; } = 1800;
    public int BuildTimeoutSeconds { get; set; } = 1800;

    public string ResolveLsAddress(string? discoveredAddress = null)
    {
        if (!string.IsNullOrWhiteSpace(LsAddress))
        {
            return NormalizeAddress(LsAddress);
        }

        var env = Environment.GetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return NormalizeAddress(env);
        }

        if (!string.IsNullOrWhiteSpace(discoveredAddress))
        {
            return NormalizeAddress(discoveredAddress);
        }

        return "127.0.0.1:5387";
    }

    public string ResolveCsrfToken(string? discoveredToken = null)
    {
        if (!string.IsNullOrWhiteSpace(CsrfToken))
        {
            return CsrfToken.Trim();
        }

        var env = Environment.GetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN")
                  ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_SERVER_CSRF_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return discoveredToken?.Trim() ?? string.Empty;
    }

    public static string NormalizeAddress(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
        {
            return string.Empty;
        }

        var trimmed = addr.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://".Length..];
        }
        else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["https://".Length..];
        }

        return trimmed.TrimEnd('/');
    }
}

public sealed class ProjectOptions
{
    public string RootPath { get; set; } = string.Empty;
    public string? UnityProjectPath { get; set; }
    public string? BuildExecutableOutput { get; set; }

    /// <summary>
    /// Build mode: "Auto", "UnityCli", "UnityBatch", "DotnetBuild", or "CustomCommand".
    /// </summary>
    public string BuildMode { get; set; } = "Auto";

    public string? CustomBuildCommand { get; set; }
    public string? CustomBuildArguments { get; set; }
    public string? UnityEditorExecutablePath { get; set; }

    public string GetEffectiveUnityProjectPath()
    {
        if (!string.IsNullOrWhiteSpace(UnityProjectPath))
        {
            return Path.IsPathRooted(UnityProjectPath)
                ? UnityProjectPath
                : Path.GetFullPath(Path.Combine(RootPath, UnityProjectPath));
        }

        return RootPath;
    }
}
