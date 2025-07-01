using Serilog;
using System.Diagnostics;
using TwitchStreamsRecorder.Helpers;

namespace TwitchStreamsRecorder.Telegram_logic
{
    /// <summary>
    /// Опции создания миниатюры для видео‑фрагмента.
    /// </summary>
    public record ThumbnailOptions(
        int Width = 320,
        TimeSpan Seek = default,
        int MaxSizeBytes = 200 * 1024,
        string? OutputExtension = ".jpg");

    internal class ThumbnailGenerator(ILogger logger, string? ffmpegPath = null)
    {
        private readonly ILogger _log = logger.ForContext("Source", "ThumbnailGenerator") ?? throw new ArgumentNullException(nameof(logger));
        private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;

        public async Task<string> GenerateAsync(string videoPath, ThumbnailOptions opts, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(videoPath));
            if (!File.Exists(videoPath)) throw new FileNotFoundException("Video file not found", videoPath);

            var thumbPath = Path.ChangeExtension(videoPath, opts.OutputExtension);
            if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length <= opts.MaxSizeBytes)
            {
                _log.Information($"Thumbnail для фрагмента трансляции {videoPath} уже существует: {thumbPath}. Он и будет использоваться для загрузки.");
                return thumbPath;
            }

            int qscale = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var args = BuildFfmpegArgs(videoPath, thumbPath, opts, qscale);

                var psi = new ProcessStartInfo(_ffmpegPath, args)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi};
                proc.Start();

                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode != 0)
                {
                    var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    _log.Warning("ffmpeg exited with code {Code}. qscale={QScale}. stderr: {Err}", proc.ExitCode, qscale, err);
                    throw new InvalidOperationException($"ffmpeg exit {proc.ExitCode}: {err}");
                }

                var size = new FileInfo(thumbPath).Length;
                if (size <= opts.MaxSizeBytes)
                {
                    _log.Information($"Thumbnail generated: {thumbPath} ({size} bytes, qscale={qscale})");
                    return thumbPath;
                }

                // иначе – увеличиваем qscale (снижаем качество) и повторяем
                qscale++;
                if (qscale > 31)
                {
                    throw new InvalidOperationException($"Cannot get thumbnail below {opts.MaxSizeBytes} bytes for '{videoPath}'.");
                }
            }
        }

        private static string BuildFfmpegArgs(string videoPath, string thumbPath, ThumbnailOptions opts, int qscale)
        {
            // -ss opts.Seek -i "{videoPath}" -frames:v 1 -vf "scale=WIDTH:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0" -c:v mjpeg -qscale:v qs -update 1 -map_metadata -1 "{thumbPath}"
            var seek = opts.Seek == default ? string.Empty : $"-ss {opts.Seek.TotalSeconds}";
            var vf = $"\"scale={opts.Width}:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0\"";
            return $"-y -loglevel error {seek} -i \"{videoPath}\" -frames:v 1 -vf {vf} -c:v mjpeg -qscale:v {qscale} -update 1 -map_metadata -1 \"{thumbPath}\"";
        }
    }
}
