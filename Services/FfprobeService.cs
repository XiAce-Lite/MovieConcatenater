using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MovieConcatenater.Models;

namespace MovieConcatenater.Services;

public static class FfprobeService
{
    public static async Task AnalyzeAsync(
        VideoFileItem item,
        string ffprobeExe,
        CancellationToken cancellationToken = default)
    {
        item.IsAnalyzing = true;
        item.Error = "";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobeExe,
                Arguments =
                    "-v error -print_format json -show_format -show_streams " +
                    Quote(item.FullPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("ffprobe を起動できませんでした。");
            }

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr) ? $"ffprobe 終了コード {process.ExitCode}" : stderr.Trim());
            }

            ApplyProbeJson(item, stdout);
        }
        catch (OperationCanceledException)
        {
            item.Error = "解析キャンセル";
            throw;
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.Resolution = "";
            item.VideoCodec = "";
            item.AudioCodec = "";
            item.FrameRate = "";
            item.DurationText = "";
            item.HasAudio = false;
            item.AudioSampleRate = 0;
            item.AudioChannels = 0;
            item.PixelFormat = "";
        }
        finally
        {
            item.IsAnalyzing = false;
        }
    }

    private static void ApplyProbeJson(VideoFileItem item, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double durationSec = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationEl) &&
            double.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            durationSec = parsed;
        }

        JsonElement? video = null;
        JsonElement? audio = null;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var typeEl))
                {
                    continue;
                }

                string? type = typeEl.GetString();
                if (type == "video" && video is null)
                {
                    video = stream;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = stream;
                }
            }
        }

        if (video is null)
        {
            throw new InvalidOperationException("映像ストリームが見つかりません。");
        }

        var v = video.Value;
        int width = v.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
        int height = v.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
        string videoCodec = v.TryGetProperty("codec_name", out var vcEl) ? (vcEl.GetString() ?? "") : "";
        string frameRate = NormalizeFrameRate(
            v.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null,
            v.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() : null);

        string audioCodec = "";
        int sampleRate = 0;
        int channels = 0;
        if (audio is not null)
        {
            var a = audio.Value;
            if (a.TryGetProperty("codec_name", out var acEl))
            {
                audioCodec = acEl.GetString() ?? "";
            }

            if (a.TryGetProperty("sample_rate", out var srEl) &&
                int.TryParse(srEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sr))
            {
                sampleRate = sr;
            }

            if (a.TryGetProperty("channels", out var chEl))
            {
                channels = chEl.GetInt32();
            }
        }

        string pixelFormat = v.TryGetProperty("pix_fmt", out var pfEl) ? (pfEl.GetString() ?? "") : "";

        item.Width = width;
        item.Height = height;
        item.Resolution = width > 0 && height > 0 ? $"{width}x{height}" : "";
        item.VideoCodec = videoCodec;
        item.HasAudio = !string.IsNullOrEmpty(audioCodec);
        item.AudioCodec = item.HasAudio ? audioCodec : "(なし)";
        item.AudioSampleRate = sampleRate;
        item.AudioChannels = channels;
        item.PixelFormat = pixelFormat;
        item.FrameRate = frameRate;
        item.Duration = TimeSpan.FromSeconds(durationSec);
        item.DurationText = durationSec > 0
            ? TimeSpan.FromSeconds(durationSec).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : "";
    }

    private static string NormalizeFrameRate(string? avg, string? r)
    {
        string? raw = string.IsNullOrWhiteSpace(avg) || avg == "0/0" ? r : avg;
        if (string.IsNullOrWhiteSpace(raw) || raw == "0/0")
        {
            return "";
        }

        var parts = raw.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return (num / den).ToString("0.###", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
