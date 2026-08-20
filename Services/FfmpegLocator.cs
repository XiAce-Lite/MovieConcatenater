using System.IO;

namespace MovieConcatenater.Services;

public static class FfmpegLocator
{
    public static bool TryResolveFfmpeg(string? configuredPath, out string exePath, out string error)
    {
        return TryResolveTool("ffmpeg", configuredPath, out exePath, out error);
    }

    public static bool TryResolveFfprobe(string? configuredFfmpegPath, out string exePath, out string error)
    {
        // FFmpeg と同じフォルダの ffprobe を優先
        if (!string.IsNullOrWhiteSpace(configuredFfmpegPath))
        {
            string ffmpeg = configuredFfmpegPath.Trim().Trim('"');
            if (File.Exists(ffmpeg))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(ffmpeg));
                if (!string.IsNullOrEmpty(dir))
                {
                    string sibling = Path.Combine(dir, "ffprobe.exe");
                    if (File.Exists(sibling))
                    {
                        exePath = sibling;
                        error = "";
                        return true;
                    }
                }
            }
        }

        return TryResolveTool("ffprobe", null, out exePath, out error);
    }

    private static bool TryResolveTool(string toolName, string? configuredPath, out string exePath, out string error)
    {
        exePath = "";
        error = "";

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string normalized = configuredPath.Trim().Trim('"');
            if (File.Exists(normalized))
            {
                exePath = Path.GetFullPath(normalized);
                return true;
            }

            error = $"指定されたパスに {toolName} が見つかりません: {normalized}";
            return false;
        }

        string? fromPath = FindOnPath(toolName + ".exe") ?? FindOnPath(toolName);
        if (!string.IsNullOrEmpty(fromPath))
        {
            exePath = fromPath;
            return true;
        }

        error = $"{toolName} が PATH 上に見つかりません。オプションでパスを指定してください。";
        return false;
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // ignore invalid PATH entries
            }
        }

        return null;
    }
}
