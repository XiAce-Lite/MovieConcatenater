using System.Globalization;
using System.IO;
using System.Text;
using MovieConcatenater.Models;

namespace MovieConcatenater.Services;

public sealed record ResolutionPreset(string Label, int Width, int Height, string FileSuffix);

public sealed record FpsOption(string Label, double? Value, string? FilterValue)
{
    public bool KeepOriginal => Value is null;
}

public static class ResizeService
{
    private const double FpsTolerance = 0.05;

    public static IReadOnlyList<ResolutionPreset> ResolutionPresets { get; } =
    [
        new("DVD (720×480)", 720, 480, "480p"),
        new("HD (1280×720)", 1280, 720, "720p"),
        new("FHD (1920×1080)", 1920, 1080, "1080p"),
        new("2K (2560×1440)", 2560, 1440, "1440p"),
    ];

    public static IReadOnlyList<FpsOption> FpsOptions { get; } =
    [
        new("元のまま", null, null),
        new("15", 15, "15"),
        new("29.97", 30000d / 1001d, "30000/1001"),
        new("30", 30, "30"),
        new("59.94", 60000d / 1001d, "60000/1001"),
        new("60", 60, "60"),
    ];

    public static string SuggestOutputFileName(string sourcePath, ResolutionPreset preset)
    {
        string body = Path.GetFileNameWithoutExtension(sourcePath);
        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".mp4";
        }

        return $"{body}_{preset.FileSuffix}{ext}";
    }

    /// <summary>縦長の動画では枠も縦向き（例: FHD → 1080×1920）として扱う。</summary>
    public static (int Width, int Height) GetTargetBox(VideoFileItem item, ResolutionPreset preset) =>
        item.Height > item.Width
            ? (preset.Height, preset.Width)
            : (preset.Width, preset.Height);

    /// <summary>縦横比を保って枠に収めたときの出力解像度（偶数丸め）。</summary>
    public static (int Width, int Height) CalculateOutputSize(VideoFileItem item, ResolutionPreset preset)
    {
        var (boxW, boxH) = GetTargetBox(item, preset);
        double scale = Math.Min((double)boxW / item.Width, (double)boxH / item.Height);
        int w = Math.Max(2, (int)(item.Width * scale) / 2 * 2);
        int h = Math.Max(2, (int)(item.Height * scale) / 2 * 2);
        return (w, h);
    }

    /// <summary>実行前の一括チェック。問題のある行ごとのメッセージを返す（空なら OK）。</summary>
    public static IReadOnlyList<string> ValidateBatch(IReadOnlyList<ResizeItem> targets)
    {
        var errors = new List<string>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            sourcePaths.Add(Path.GetFullPath(t.Source.FullPath));
        }

        var seenOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            string name = t.Source.FileName;
            try
            {
                Validate(t.Source, t.Preset, t.Fps);
            }
            catch (Exception ex)
            {
                errors.Add($"・{name}: {ex.Message.Replace("\n", " ")}");
            }

            string output = t.OutputFullPath;
            if (string.IsNullOrWhiteSpace(output))
            {
                errors.Add($"・{name}: 出力パスが指定されていません。");
                continue;
            }

            string fullOutput;
            try
            {
                fullOutput = Path.GetFullPath(output);
            }
            catch
            {
                errors.Add($"・{name}: 出力パスが不正です（{output}）。");
                continue;
            }

            if (sourcePaths.Contains(fullOutput))
            {
                errors.Add($"・{name}: 出力先が入力ファイルと同じです（{fullOutput}）。");
            }
            else if (File.Exists(fullOutput))
            {
                errors.Add($"・{name}: 出力ファイルが既に存在します（{fullOutput}）。");
            }

            if (seenOutputs.TryGetValue(fullOutput, out string? other))
            {
                errors.Add($"・{name}: 出力パスが「{other}」と重複しています（{fullOutput}）。");
            }
            else
            {
                seenOutputs[fullOutput] = name;
            }
        }

        return errors;
    }

    public static void Validate(VideoFileItem? item, ResolutionPreset preset, FpsOption fps)
    {
        if (item is null)
        {
            throw new InvalidOperationException("動画ファイルをドロップしてください。");
        }

        if (item.IsAnalyzing)
        {
            throw new InvalidOperationException("メディア情報の解析が完了していません。しばらく待ってから再実行してください。");
        }

        if (!string.IsNullOrEmpty(item.Error) || item.Width <= 0 || item.Height <= 0)
        {
            throw new InvalidOperationException($"メディア情報を取得できませんでした:\n{item.Error}");
        }

        var (boxW, boxH) = GetTargetBox(item, preset);
        double scale = Math.Min((double)boxW / item.Width, (double)boxH / item.Height);
        if (scale > 1.0001)
        {
            throw new InvalidOperationException(
                $"元ファイル（{item.Width}×{item.Height}）より大きい解像度は選べません。\n" +
                "元ファイル以下の解像度を選んでください。");
        }

        if (!fps.KeepOriginal &&
            double.TryParse(item.FrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out double sourceFps) &&
            sourceFps > 0 &&
            fps.Value > sourceFps + FpsTolerance)
        {
            throw new InvalidOperationException(
                $"元ファイル（{item.FrameRate} fps）より高い FPS は選べません。\n" +
                "元ファイル以下の FPS を選んでください。");
        }
    }

    public static async Task ResizeAsync(
        VideoFileItem item,
        ResolutionPreset preset,
        FpsOption fps,
        string ffmpegExe,
        string outputPath,
        IProgress<ConcatProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(item, preset, fps);
        ConcatService.EnsureOutputReady(outputPath);

        string args = BuildArguments(item, preset, fps, outputPath);
        progress?.Report(new ConcatProgress { Percent = 0, Message = "解像度変更を開始しています…" });

        await ConcatService.RunFfmpegAsync(
            ffmpegExe,
            args,
            item.Duration,
            "解像度変更中…",
            progress,
            outputPath,
            cancellationToken);
    }

    internal static string BuildArguments(VideoFileItem item, ResolutionPreset preset, FpsOption fps, string outputPath)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        bool isMp4Family = ext is ".mp4" or ".m4v" or ".mov";

        var (boxW, boxH) = GetTargetBox(item, preset);
        var filter = new StringBuilder();
        filter.Append(CultureInfo.InvariantCulture,
            $"scale={boxW}:{boxH}:force_original_aspect_ratio=decrease:force_divisible_by=2");
        if (!fps.KeepOriginal)
        {
            filter.Append(",fps=").Append(fps.FilterValue);
        }

        string encoder = ConcatService.MapVideoEncoder(item.VideoCodec);

        var sb = new StringBuilder();
        sb.Append("-hide_banner -y ");
        sb.Append("-i ").Append(ConcatService.Quote(item.FullPath)).Append(' ');
        sb.Append("-map 0:v:0 -map 0:a? ");
        sb.Append("-vf ").Append(ConcatService.Quote(filter.ToString())).Append(' ');
        sb.Append("-c:v ").Append(encoder).Append(' ');
        sb.Append(QualityArguments(encoder, isMp4Family));
        sb.Append("-c:a copy ");
        sb.Append("-map_metadata 0 ");
        if (isMp4Family)
        {
            sb.Append("-movflags +faststart ");
        }

        sb.Append(ConcatService.Quote(outputPath));
        return sb.ToString();
    }

    // 画質基準（CRF）。解像度が下がれば出力ビットレートも自然に下がる
    private static string QualityArguments(string encoder, bool isMp4Family) => encoder switch
    {
        "libx264" => "-preset medium -crf 23 ",
        "libx265" => "-preset medium -crf 28 " + (isMp4Family ? "-tag:v hvc1 " : ""),
        "libvpx-vp9" => "-crf 32 -b:v 0 ",
        "libvpx" => "-crf 10 -b:v 2M ",
        "libsvtav1" => "-preset 8 -crf 35 ",
        _ => "-crf 23 ",
    };
}
