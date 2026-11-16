using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;
using NexusDaemon.Phase2_StateAndMemory;

namespace NexusDaemon.Phase4_ToolsAndEvents;

public interface IBuildRunner
{
    BuildStatus StartBuildInBackground(
        string projectName,
        string configuration = "Release",
        CancellationToken stoppingToken = default);
}

public sealed record ResolvedBuildCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string CommandSummary,
    string? LogFilePath);

/// <summary>
/// Phase 4 Build Runner: Tracks headless Unity / MSBuild / dotnet build executions in StateManager
/// and publishes a BuildCompletedEvent when finished.
/// </summary>
public sealed class BuildRunner : IBuildRunner
{
    private readonly NexusOptions _options;
    private readonly StateManager _stateManager;
    private readonly NexusEventBus _eventBus;
    private readonly ILogger<BuildRunner> _logger;

    public BuildRunner(
        IOptions<NexusOptions> options,
        StateManager stateManager,
        NexusEventBus eventBus,
        ILogger<BuildRunner> logger)
    {
        _options = options.Value;
        _stateManager = stateManager;
        _eventBus = eventBus;
        _logger = logger;
    }

    public BuildStatus StartBuildInBackground(
        string projectName,
        string configuration = "Release",
        CancellationToken stoppingToken = default)
    {
        var buildId = $"build-{Guid.NewGuid().ToString("N")[..8]}";
        var effectiveConfig = string.IsNullOrWhiteSpace(configuration) ? "Release" : configuration.Trim();
        var status = _stateManager.RegisterBuild(
            buildId,
            projectName,
            effectiveConfig,
            $"Headless build ({effectiveConfig})");

        _ = Task.Run(() => ExecuteBuildAsync(buildId, projectName, effectiveConfig, stoppingToken), CancellationToken.None);
        return status;
    }

    public ResolvedBuildCommand? TryResolveBuildCommand(
        string projectName,
        ProjectOptions projectConfig,
        string configuration,
        out string diagnosticMessage)
    {
        diagnosticMessage = string.Empty;
        var rootPath = projectConfig.RootPath;
        var unityProjectPath = projectConfig.GetEffectiveUnityProjectPath();
        var unityVersion = TryReadUnityEditorVersion(unityProjectPath);

        // 1. Custom command override
        if (string.Equals(projectConfig.BuildMode, "CustomCommand", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(projectConfig.CustomBuildCommand))
        {
            var args = string.IsNullOrWhiteSpace(projectConfig.CustomBuildArguments)
                ? Array.Empty<string>()
                : projectConfig.CustomBuildArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new ResolvedBuildCommand(
                projectConfig.CustomBuildCommand,
                args,
                rootPath,
                $"{projectConfig.CustomBuildCommand} {projectConfig.CustomBuildArguments}".Trim(),
                null);
        }

        // 2. If DotnetBuild is explicitly selected or a .sln exists and compile-check is requested
        var slnFile = TryFindSolutionFile(unityProjectPath) ?? TryFindSolutionFile(rootPath);
        if (string.Equals(projectConfig.BuildMode, "DotnetBuild", StringComparison.OrdinalIgnoreCase) ||
            (slnFile is not null && string.Equals(configuration, "CompileCheck", StringComparison.OrdinalIgnoreCase)))
        {
            var targetSln = slnFile ?? rootPath;
            var dotnetExe = File.Exists(@"C:\Program Files\dotnet\dotnet.exe")
                ? @"C:\Program Files\dotnet\dotnet.exe"
                : "dotnet";
            var buildConfig = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            string[] args = ["build", targetSln, "-c", buildConfig, "--nologo"];
            return new ResolvedBuildCommand(
                dotnetExe,
                args,
                Path.GetDirectoryName(targetSln) ?? rootPath,
                $"dotnet build \"{Path.GetFileName(targetSln)}\" -c {buildConfig}",
                null);
        }

        // 3. Official Unity CLI (`unity build <projectPath> --target StandaloneWindows64 --output-path <outputExe>`)
        var outputExe = !string.IsNullOrWhiteSpace(projectConfig.BuildExecutableOutput)
            ? projectConfig.BuildExecutableOutput
            : Path.Combine(unityProjectPath, "Builds", "Windows", $"{projectName}.exe");
        var logFile = Path.Combine(unityProjectPath, "Logs", $"nexus-build-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        var unityCliExe = ResolveUnityCliExecutable();
        if (string.Equals(projectConfig.BuildMode, "UnityCli", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(projectConfig.BuildMode, "Auto", StringComparison.OrdinalIgnoreCase) && unityCliExe is not null))
        {
            var cliBinary = unityCliExe ?? "unity";
            string[] cliArgs =
            [
                "build",
                unityProjectPath,
                "--target",
                "StandaloneWindows64",
                "--output-path",
                outputExe,
                "--log-file",
                logFile,
                "--no-tail",
                "--non-interactive"
            ];

            return new ResolvedBuildCommand(
                cliBinary,
                cliArgs,
                unityProjectPath,
                $"unity build \"{unityProjectPath}\" --target StandaloneWindows64 --output-path \"{outputExe}\"",
                logFile);
        }

        // 4. Headless Unity Batchmode Build (`Unity.exe -batchmode -nographics -quit ...`)
        var unityExe = ResolveUnityEditorExecutable(projectConfig.UnityEditorExecutablePath, unityVersion);
        if (unityExe is not null)
        {
            string[] args =
            [
                "-batchmode",
                "-nographics",
                "-quit",
                "-projectPath",
                unityProjectPath,
                "-buildWindows64Player",
                outputExe,
                "-logFile",
                logFile
            ];

            return new ResolvedBuildCommand(
                unityExe,
                args,
                unityProjectPath,
                $"Unity.exe -batchmode -nographics -quit -projectPath \"{unityProjectPath}\" -buildWindows64Player \"{outputExe}\"",
                logFile);
        }

        // 5. Fallback to .sln via dotnet build if Unity.exe isn't found but a generated .sln exists
        if (slnFile is not null)
        {
            var dotnetExe = File.Exists(@"C:\Program Files\dotnet\dotnet.exe")
                ? @"C:\Program Files\dotnet\dotnet.exe"
                : "dotnet";
            var buildConfig = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            string[] args = ["build", slnFile, "-c", buildConfig, "--nologo"];
            return new ResolvedBuildCommand(
                dotnetExe,
                args,
                Path.GetDirectoryName(slnFile) ?? rootPath,
                $"dotnet build \"{Path.GetFileName(slnFile)}\" -c {buildConfig}",
                null);
        }

        var versionHint = unityVersion ?? "6000.3.8f1";
        diagnosticMessage =
            $"Could not find Unity Editor ({versionHint}), Unity CLI (`unity`), or a generated .sln file for '{projectName}' at '{unityProjectPath}'. " +
            $"To build headlessly without opening Unity GUI, either install Unity {versionHint} (so Nexus can run " +
            $"`Unity.exe -batchmode -nographics -quit -projectPath \"{unityProjectPath}\" -buildWindows64Player \"{outputExe}\"`), " +
            "install the Unity CLI (`irm https://unity.com/install.ps1 | iex`), " +
            "or generate the .sln/.csproj files once so `dotnet build` can compile the assemblies directly.";
        return null;
    }

    public static string? TryReadUnityEditorVersion(string unityProjectPath)
    {
        if (string.IsNullOrWhiteSpace(unityProjectPath))
        {
            return null;
        }

        var versionFile = Path.Combine(unityProjectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        foreach (var line in File.ReadLines(versionFile))
        {
            if (line.StartsWith("m_EditorVersion:", StringComparison.OrdinalIgnoreCase))
            {
                return line["m_EditorVersion:".Length..].Trim();
            }
        }

        return null;
    }

    private async Task ExecuteBuildAsync(
        string buildId,
        string projectName,
        string configuration,
        CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        int? processId = null;
        var exitCode = -1;
        var success = false;
        string summary;
        string? logFilePath = null;

        try
        {
            if (!_options.Projects.TryGetValue(projectName, out var projectConfig))
            {
                summary = $"Unknown project '{projectName}'. Registered projects: {string.Join(", ", _options.Projects.Keys)}";
            }
            else
            {
                var resolved = TryResolveBuildCommand(projectName, projectConfig, configuration, out var diagnostic);
                if (resolved is null)
                {
                    summary = diagnostic;
                }
                else
                {
                    logFilePath = resolved.LogFilePath;
                    if (!string.IsNullOrWhiteSpace(logFilePath))
                    {
                        var logDir = Path.GetDirectoryName(logFilePath);
                        if (!string.IsNullOrWhiteSpace(logDir))
                        {
                            Directory.CreateDirectory(logDir);
                        }
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = resolved.FileName,
                        WorkingDirectory = Directory.Exists(resolved.WorkingDirectory)
                            ? resolved.WorkingDirectory
                            : Directory.GetCurrentDirectory(),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    foreach (var arg in resolved.Arguments)
                    {
                        psi.ArgumentList.Add(arg);
                    }

                    using var process = Process.Start(psi)
                        ?? throw new InvalidOperationException($"Failed to start build process '{resolved.FileName}'.");

                    try
                    {
                        processId = process.Id;
                        _stateManager.UpdateBuildProcessId(buildId, process.Id, resolved.CommandSummary);

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _options.Runner.BuildTimeoutSeconds)));

                        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                        await process.WaitForExitAsync(timeoutCts.Token);

                        exitCode = process.ExitCode;
                        var stdout = await stdoutTask;
                        var stderr = await stderrTask;

                        var combined = new StringBuilder();
                        combined.AppendLine($"Command: {resolved.CommandSummary}");
                        if (!string.IsNullOrWhiteSpace(stdout))
                        {
                            combined.AppendLine(stdout.Trim());
                        }

                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            combined.AppendLine(stderr.Trim());
                        }

                        if (!string.IsNullOrWhiteSpace(logFilePath) && File.Exists(logFilePath))
                        {
                            var logTail = await ReadLogTailAsync(logFilePath, 4000, timeoutCts.Token);
                            if (!string.IsNullOrWhiteSpace(logTail))
                            {
                                combined.AppendLine("--- Unity Build Log Tail ---");
                                combined.AppendLine(logTail);
                            }
                        }

                        success = exitCode == 0;
                        summary = TruncateSummary(combined.ToString(), 6000);
                    }
                    finally
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                            }
                        }
                        catch
                        {
                            // Best-effort cleanup of timed-out build process
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build {BuildId} failed for {ProjectName}", buildId, projectName);
            success = false;
            summary = $"Build execution failed with exception: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            _stateManager.CompleteBuild(buildId, out _);
        }

        var buildEvent = new BuildCompletedEvent(
            BuildId: buildId,
            ProcessId: processId,
            ProjectName: projectName,
            Configuration: configuration,
            Success: success,
            ExitCode: exitCode,
            LogSummary: summary,
            LogFilePath: logFilePath,
            Duration: sw.Elapsed,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await _eventBus.PublishAsync(buildEvent, CancellationToken.None);
    }

    private static string? TryFindSolutionFile(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }

    private static string? ResolveUnityCliExecutable()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(userProfile, ".unity", "bin", "unity.exe"),
            Path.Combine(localAppData, "Unity", "bin", "unity.exe"),
            Path.Combine(localAppData, "Programs", "Unity", "bin", "unity.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveUnityEditorExecutable(string? configuredPath, string? editorVersion)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var envEditorPath = Environment.GetEnvironmentVariable("UNITY_EDITOR_PATH");
        if (!string.IsNullOrWhiteSpace(envEditorPath) && File.Exists(envEditorPath))
        {
            return envEditorPath;
        }

        var searchRoots = new List<string>
        {
            @"C:\Program Files\Unity\Hub\Editor",
            @"D:\Programs\Unity\Hub\Editor",
            @"E:\Programs\Unity\Hub\Editor"
        };

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var secondaryPathFile = Path.Combine(appData, "UnityHub", "secondaryInstallPath.json");
            if (File.Exists(secondaryPathFile))
            {
                var customRoot = File.ReadAllText(secondaryPathFile).Trim().Trim('"').Replace(@"\\", @"\");
                if (!string.IsNullOrWhiteSpace(customRoot))
                {
                    searchRoots.Insert(0, customRoot);
                }
            }
        }
        catch
        {
            // Ignore unreadable UnityHub config
        }

        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            foreach (var root in searchRoots)
            {
                var candidate = Path.Combine(root, editorVersion, "Editor", "Unity.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var anyEditor = Directory.GetDirectories(root)
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "Editor", "Unity.exe"))
                .FirstOrDefault(File.Exists);

            if (anyEditor is not null)
            {
                return anyEditor;
            }
        }

        return null;
    }

    private static async Task<string> ReadLogTailAsync(string filePath, int maxChars, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private static string TruncateSummary(string text, int maxChars)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maxChars
            ? trimmed
            : trimmed[..maxChars] + "\n...[TRUNCATED]...";
    }
}
