using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MovieConcatenater.Models;

namespace MovieConcatenater.Services;

public sealed class ConcatProgress
{
    public double Percent { get; init; }
    public string Message { get; init; } = "";
}

public static class ConcatService
{
    // FFmpeg concat demuxer は BOM 付き UTF-8 を解釈できず "unknown keyword 'file'" になる
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly Regex TimeRegex = new(
        @"time=(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void ValidateBasic(IReadOnlyList<VideoFileItem> items)
    {
        if (items.Count < 2)
        {
            throw new InvalidOperationException("連結には2つ以上のファイルが必要です。");
        }

        if (items.Any(i => i.IsAnalyzing))
        {
            throw new InvalidOperationException("メディア情報の解析が完了していません。しばらく待ってから再実行してください。");
        }

        var failed = items.Where(i => !string.IsNullOrEmpty(i.Error) || string.IsNullOrEmpty(i.VideoCodec)).ToList();
        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                "解析に失敗したファイルがあります:\n" +
                string.Join("\n", failed.Select(f => $"・{f.FileName}: {f.Error}")));
        }
    }

    public static CompatibilityResult CheckCompatibility(IReadOnlyList<VideoFileItem> items)
    {
        ValidateBasic(items);

        var reference = items[0];
        string key = reference.CompatibilityKey;
        var mismatches = items.Where(i => i.CompatibilityKey != key).ToList();

        return new CompatibilityResult
        {
            Items = items,
            Reference = reference,
            Mismatches = mismatches,
        };
    }

    public static string BuildReencodeConfirmMessage(CompatibilityResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("解像度またはエンコード形式が一致しません。");
        sb.AppendLine("先頭ファイルの形式に合わせて再エンコードして連結します。");
        sb.AppendLine("（時間がかかり、画質が一度劣化する可能性があります）");
        sb.AppendLine();
        sb.AppendLine("【出力基準（先頭ファイル）】");
        sb.AppendLine($"  {result.Reference.FileName}");
        sb.AppendLine($"  {FormatSpec(result.Reference)}");
        sb.AppendLine();
        sb.AppendLine("【不一致ファイル】");
        foreach (var m in result.Mismatches)
        {
            sb.AppendLine($"  {m.FileName} → {FormatSpec(m)}");
        }

        sb.AppendLine();
        sb.AppendLine("続行しますか？");
        return sb.ToString().TrimEnd();
    }

    public static async Task ConcatenateAsync(
        IReadOnlyList<VideoFileItem> items,
        string ffmpegExe,
        string outputPath,
        ConcatMode mode,
        IProgress<ConcatProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateBasic(items);

        if (mode == ConcatMode.Copy)
        {
            var check = CheckCompatibility(items);
            if (!check.IsCompatible)
            {
                throw new InvalidOperationException("形式不一致のためコピー連結はできません。");
            }

            await ConcatenateCopyAsync(items, ffmpegExe, outputPath, progress, cancellationToken);
        }
        else
        {
            await ConcatenateReencodeAsync(items, ffmpegExe, outputPath, progress, cancellationToken);
        }
    }

    private static async Task ConcatenateCopyAsync(
        IReadOnlyList<VideoFileItem> items,
        string ffmpegExe,
        string outputPath,
        IProgress<ConcatProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOutputReady(outputPath);

        TimeSpan totalDuration = TimeSpan.FromTicks(items.Sum(i => i.Duration.Ticks));
        string listPath = Path.Combine(Path.GetTempPath(), "MovieConcatenater_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            await File.WriteAllLinesAsync(
                listPath,
                items.Select(i => "file " + EscapeConcatPath(i.FullPath)),
                Utf8NoBom,
                cancellationToken);

            progress?.Report(new ConcatProgress { Percent = 0, Message = "連結を開始しています…（コピー）" });

            string args =
                $"-hide_banner -y -f concat -safe 0 -i {Quote(listPath)} -c copy {Quote(outputPath)}";

            await RunFfmpegAsync(ffmpegExe, args, totalDuration, "連結中…", progress, outputPath, cancellationToken);
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    private static async Task ConcatenateReencodeAsync(
        IReadOnlyList<VideoFileItem> items,
        string ffmpegExe,
        string outputPath,
        IProgress<ConcatProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOutputReady(outputPath);

        var reference = items[0];
        string filter = BuildReencodeFilterComplex(items, reference);
        string videoEncoder = MapVideoEncoder(reference.VideoCodec);
        string audioEncoder = reference.HasAudio ? MapAudioEncoder(reference.AudioCodec) : "";

        var inputArgs = new StringBuilder();
        foreach (var item in items)
        {
            inputArgs.Append("-i ").Append(Quote(item.FullPath)).Append(' ');
        }

        var outputArgs = new StringBuilder();
        outputArgs.Append("-map \"[vout]\" ");
        if (reference.HasAudio)
        {
            outputArgs.Append("-map \"[aout]\" ");
            outputArgs.Append("-c:a ").Append(audioEncoder).Append(' ');
        }

        outputArgs.Append("-c:v ").Append(videoEncoder).Append(' ');
        outputArgs.Append(Quote(outputPath));

        string args =
            $"-hide_banner -y {inputArgs}-filter_complex {Quote(filter)} {outputArgs}";

        TimeSpan totalDuration = TimeSpan.FromTicks(items.Sum(i => i.Duration.Ticks));
        progress?.Report(new ConcatProgress { Percent = 0, Message = "再エンコード連結を開始しています…" });

        await RunFfmpegAsync(
            ffmpegExe,
            args,
            totalDuration,
            "再エンコード中…",
            progress,
            outputPath,
            cancellationToken);
    }

    internal static string BuildReencodeFilterComplex(IReadOnlyList<VideoFileItem> items, VideoFileItem reference)
    {
        int w = reference.Width;
        int h = reference.Height;
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("先頭ファイルの解像度を取得できません。");
        }

        string fps = string.IsNullOrWhiteSpace(reference.FrameRate) ? "30" : reference.FrameRate;
        string pixFmt = string.IsNullOrWhiteSpace(reference.PixelFormat) ? "yuv420p" : reference.PixelFormat;
        int sampleRate = reference.AudioSampleRate > 0 ? reference.AudioSampleRate : 48000;
        int channels = reference.AudioChannels > 0 ? reference.AudioChannels : 2;
        string channelLayout = channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => $"{channels}c",
        };

        var sb = new StringBuilder();
        var concatInputs = new StringBuilder();

        for (int i = 0; i < items.Count; i++)
        {
            sb.Append(
                $"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease," +
                $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format={pixFmt},setpts=PTS-STARTPTS[v{i}];");

            if (reference.HasAudio)
            {
                if (items[i].HasAudio)
                {
                    sb.Append(
                        $"[{i}:a]aresample={sampleRate},aformat=sample_rates={sampleRate}:channel_layouts={channelLayout}," +
                        $"asetpts=PTS-STARTPTS[a{i}];");
                }
                else
                {
                    double duration = Math.Max(items[i].Duration.TotalSeconds, 0.001);
                    string durationText = duration.ToString("0.###", CultureInfo.InvariantCulture);
                    sb.Append(
                        $"anullsrc=r={sampleRate}:cl={channelLayout},atrim=duration={durationText}," +
                        $"asetpts=PTS-STARTPTS[a{i}];");
                }
            }

            concatInputs.Append(reference.HasAudio ? $"[v{i}][a{i}]" : $"[v{i}]");
        }

        if (reference.HasAudio)
        {
            sb.Append($"{concatInputs}concat=n={items.Count}:v=1:a=1[vout][aout]");
        }
        else
        {
            sb.Append($"{concatInputs}concat=n={items.Count}:v=1:a=0[vout]");
        }

        return sb.ToString();
    }

    internal static async Task RunFfmpegAsync(
        string ffmpegExe,
        string arguments,
        TimeSpan totalDuration,
        string progressPrefix,
        IProgress<ConcatProgress>? progress,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("ffmpeg を起動できませんでした。");
        }

        var stderrBuilder = new StringBuilder();
        var readTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                stderrBuilder.AppendLine(line);
                TryReportProgress(line, totalDuration, progressPrefix, progress);
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(outputPath);
            throw;
        }

        await readTask;

        if (cancellationToken.IsCancellationRequested)
        {
            TryDelete(outputPath);
            throw new OperationCanceledException(cancellationToken);
        }

        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            string detail = stderrBuilder.ToString().Trim();
            if (detail.Length > 1200)
            {
                detail = detail[^1200..];
            }

            throw new InvalidOperationException(
                $"ffmpeg が失敗しました (code {process.ExitCode}).\n{detail}");
        }

        progress?.Report(new ConcatProgress { Percent = 100, Message = "完了: " + outputPath });
    }

    internal static void EnsureOutputReady(string outputPath)
    {
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new InvalidOperationException("出力パスが不正です。");
        }

        Directory.CreateDirectory(outputDir);

        if (File.Exists(outputPath))
        {
            throw new InvalidOperationException($"出力ファイルが既に存在します:\n{outputPath}");
        }
    }

    internal static string MapVideoEncoder(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "libx264",
        "hevc" or "h265" => "libx265",
        "vp9" => "libvpx-vp9",
        "vp8" => "libvpx",
        "av1" => "libsvtav1",
        "mpeg4" or "msmpeg4v3" => "libx264",
        "mjpeg" => "libx264",
        _ => "libx264",
    };

    private static string MapAudioEncoder(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" => "aac",
        "mp3" => "libmp3lame",
        "opus" => "libopus",
        "vorbis" => "libvorbis",
        "ac3" => "ac3",
        "flac" => "flac",
        _ => "aac",
    };

    private static void TryReportProgress(
        string line,
        TimeSpan totalDuration,
        string prefix,
        IProgress<ConcatProgress>? progress)
    {
        if (progress is null || totalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var m = TimeRegex.Match(line);
        if (!m.Success)
        {
            return;
        }

        int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        int min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        double sec = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var current = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
        double percent = Math.Clamp(current.TotalSeconds / totalDuration.TotalSeconds * 100.0, 0, 99.5);

        progress.Report(new ConcatProgress
        {
            Percent = percent,
            Message = $"{prefix} {percent:0.0}% ({FormatTs(current)} / {FormatTs(totalDuration)})",
        });
    }

    private static string FormatTs(TimeSpan ts) =>
        ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatSpec(VideoFileItem item) =>
        $"{item.Resolution}, video={item.VideoCodec}, audio={item.AudioCodec}, fps={item.FrameRate}";

    private static string EscapeConcatPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return "'" + normalized.Replace("'", @"'\''") + "'";
    }

    internal static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static void TryKill(Process process)
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
            // ignore
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
