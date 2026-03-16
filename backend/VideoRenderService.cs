using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace backend;

public sealed class VideoRenderService(IHostEnvironment environment, ILogger<VideoRenderService> logger)
{
    private const int Width = 1280;
    private const int Height = 720;
    private const string BackgroundColor = "#0c7b93";
    private const string AccentColor = "#d97706";

    public async Task<(string RelativePath, string AbsolutePath, double DurationSeconds)> RenderAsync(
        string title,
        string voiceoverText,
        IReadOnlyList<StudioSceneDto> scenes,
        string captionsSrt,
        CancellationToken cancellationToken)
    {
        var studioRoot = Path.Combine(environment.ContentRootPath, "backups", "studio", DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(studioRoot);

        var slug = Slugify(title);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var voicePath = Path.Combine(studioRoot, $"{slug}_{stamp}_voice.txt");
        var audioPath = Path.Combine(studioRoot, $"{slug}_{stamp}_voice.wav");
        var captionsPath = Path.Combine(studioRoot, $"{slug}_{stamp}.srt");
        var outputPath = Path.Combine(studioRoot, $"{slug}_{stamp}.mp4");

        await File.WriteAllTextAsync(voicePath, voiceoverText, Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(captionsPath, captionsSrt, Encoding.UTF8, cancellationToken);

        var audioGenerated = await TryGenerateVoiceoverAsync(voicePath, audioPath, cancellationToken);
        var durationSeconds = audioGenerated
            ? Math.Max(18, await ProbeDurationAsync(audioPath, cancellationToken))
            : Math.Max(18, scenes.Count * 8);

        var filter = BuildVideoFilter(scenes, durationSeconds);

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add($"color=c={BackgroundColor}:s={Width}x{Height}:d={durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");

        if (audioGenerated)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPath);
        }
        else
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("lavfi");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("anullsrc=channel_layout=stereo:sample_rate=44100");
        }

        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(filter);
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-shortest");
        startInfo.ArgumentList.Add(outputPath);

        await RunProcessAsync(startInfo, cancellationToken);

        var relativePath = Path.GetRelativePath(environment.ContentRootPath, outputPath).Replace('\\', '/');
        return (relativePath, outputPath, durationSeconds);
    }

    private async Task<bool> TryGenerateVoiceoverAsync(string voicePath, string audioPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add($"flite=textfile='{EscapeFilterPath(voicePath)}':voice=slt");
        startInfo.ArgumentList.Add(audioPath);

        try
        {
            await RunProcessAsync(startInfo, cancellationToken);
            return File.Exists(audioPath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "No se pudo sintetizar la voz del video con ffmpeg/flite.");
            return false;
        }
    }

    private static string BuildVideoFilter(IReadOnlyList<StudioSceneDto> scenes, double totalDuration)
    {
        var fontPath = EscapeFilterPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf"));
        var sceneDuration = scenes.Count == 0 ? totalDuration : totalDuration / scenes.Count;
        var filters = new List<string>
        {
            $"drawbox=x=0:y=0:w={Width}:h={Height}:color=black@0.16:t=fill",
            $"drawbox=x=60:y=64:w={Width - 120}:h=6:color={AccentColor}@0.95:t=fill",
            $"drawtext=fontfile='{fontPath}':text='HDM PROCUREMENT STUDIO':fontcolor=white@0.78:fontsize=24:x=60:y={Height - 76}"
        };

        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var start = index * sceneDuration;
            var end = Math.Min(totalDuration, start + sceneDuration);
            filters.Add(
                $"drawtext=fontfile='{fontPath}':text='{EscapeDrawText(scene.Title)}':fontcolor=white:fontsize=50:x=(w-text_w)/2:y=160:enable='between(t,{start.ToString("0.###", CultureInfo.InvariantCulture)},{end.ToString("0.###", CultureInfo.InvariantCulture)})'");
            filters.Add(
                $"drawtext=fontfile='{fontPath}':text='{EscapeDrawText(scene.OverlayText)}':fontcolor=white@0.96:fontsize=28:x=(w-text_w)/2:y=280:enable='between(t,{start.ToString("0.###", CultureInfo.InvariantCulture)},{end.ToString("0.###", CultureInfo.InvariantCulture)})'");
            filters.Add(
                $"drawtext=fontfile='{fontPath}':text='Escena {index + 1}/{scenes.Count}':fontcolor=white@0.7:fontsize=24:x=60:y=118:enable='between(t,{start.ToString("0.###", CultureInfo.InvariantCulture)},{end.ToString("0.###", CultureInfo.InvariantCulture)})'");
        }

        return string.Join(",", filters);
    }

    private async Task<double> ProbeDurationAsync(string audioPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(audioPath);

        var output = await RunProcessAsync(startInfo, cancellationToken);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0;
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var output = await stdOutTask;
        var error = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"El proceso {startInfo.FileName} fallo con codigo {process.ExitCode}."
                : error.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static string EscapeDrawText(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeFilterPath(string path)
        => path.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);

    private static string Slugify(string value)
    {
        var normalized = value.ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }
}
