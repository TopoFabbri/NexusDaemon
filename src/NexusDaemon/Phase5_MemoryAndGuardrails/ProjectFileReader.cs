using Microsoft.Extensions.Options;
using NexusDaemon.Configuration;

namespace NexusDaemon.Phase5_MemoryAndGuardrails;

public sealed record FileReadResult(
    bool Success,
    string ProjectName,
    string RelativePath,
    string? ResolvedFullPath,
    string Content,
    bool Truncated);

/// <summary>
/// Phase 5: Sandboxed dynamic file reader allowing the stateless Nexus router
/// to read Architecture.md, Lore.md, or specific project source files on demand.
/// </summary>
public sealed class ProjectFileReader
{
    public const int MaxCharactersPerRead = 32_000;

    private readonly NexusOptions _options;

    public ProjectFileReader(IOptions<NexusOptions> options)
    {
        _options = options.Value;
    }

    public async Task<FileReadResult> ReadFileAsync(
        string projectName,
        string relativePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return new FileReadResult(false, string.Empty, relativePath ?? string.Empty, null, "Error: projectName is required.", false);
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new FileReadResult(false, projectName, string.Empty, null, "Error: relativePath is required.", false);
        }

        if (!_options.Projects.TryGetValue(projectName.Trim(), out var projectConfig) ||
            string.IsNullOrWhiteSpace(projectConfig.RootPath))
        {
            return new FileReadResult(
                false,
                projectName,
                relativePath,
                null,
                $"Error: Unknown project '{projectName}'. Registered projects: {string.Join(", ", _options.Projects.Keys)}",
                false);
        }

        var rootFullPath = Path.GetFullPath(projectConfig.RootPath);
        var rootWithSep = EnsureTrailingSeparator(rootFullPath);

        // Normalize slashes
        var sanitizedRelative = relativePath.Trim().TrimStart('/', '\\');
        var candidatePrimary = Path.GetFullPath(Path.Combine(rootFullPath, sanitizedRelative));

        // Security check: prevent path traversal outside the project root
        if (!candidatePrimary.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePrimary, rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return new FileReadResult(
                false,
                projectName,
                relativePath,
                null,
                $"Error: Path '{relativePath}' escapes the sandboxed project root '{rootFullPath}'.",
                false);
        }

        string? targetFile = null;
        if (File.Exists(candidatePrimary))
        {
            targetFile = candidatePrimary;
        }
        else
        {
            // Also check nested UnityProjectPath if configured (e.g., D:\Files\Repos\HordePunkMaidens\HordePunkMaidens)
            var unityRoot = projectConfig.GetEffectiveUnityProjectPath();
            if (!string.Equals(unityRoot, rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                var candidateUnity = Path.GetFullPath(Path.Combine(unityRoot, sanitizedRelative));
                if (candidateUnity.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidateUnity))
                {
                    targetFile = candidateUnity;
                }
            }
        }

        if (targetFile is null)
        {
            return new FileReadResult(
                false,
                projectName,
                relativePath,
                null,
                $"Error: File '{relativePath}' was not found in project '{projectName}' (root: '{rootFullPath}').",
                false);
        }

        var content = await File.ReadAllTextAsync(targetFile, ct);
        var truncated = false;
        if (content.Length > MaxCharactersPerRead)
        {
            content = content[..MaxCharactersPerRead] + "\n...[TRUNCATED BY NEXUS FILE READER]...";
            truncated = true;
        }

        return new FileReadResult(true, projectName, relativePath, targetFile, content, truncated);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
