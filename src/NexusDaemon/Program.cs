using NexusDaemon;

DotEnv.Load();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNexusDaemon(builder.Configuration);

var host = builder.Build();
host.Run();

namespace NexusDaemon
{
    public static class DotEnv
    {
        public static void Load(string? specificPath = null)
        {
            if (!string.IsNullOrWhiteSpace(specificPath))
            {
                if (File.Exists(specificPath))
                {
                    LoadFile(specificPath);
                }
                return;
            }

            var candidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env")
            };

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate))
                {
                    LoadFile(candidate);
                    break;
                }
            }
        }

        private static void LoadFile(string filePath)
        {
            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }

                var separatorIdx = line.IndexOf('=');
                if (separatorIdx <= 0)
                {
                    continue;
                }

                var key = line[..separatorIdx].Trim();
                var val = line[(separatorIdx + 1)..].Trim();

                if (val.Length >= 2 &&
                    ((val.StartsWith('"') && val.EndsWith('"')) ||
                     (val.StartsWith('\'') && val.EndsWith('\''))))
                {
                    val = val[1..^1];
                }

                if (!string.IsNullOrEmpty(key) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                {
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
        }
    }
}
