using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TwitchStreamsRecorder.Helpers;

namespace TwitchStreamsRecorder
{
    internal class TranscoderService(ILogger logger)
    {
        private int _outputFileIndex = 0;
        private int _bufferSize;
        private BlockingCollection<string>? _bufferFilesQueue;

        private Process? _ffmpegProc; private Process? _ffmpegProc720;
        public Process? FfmpegProc { get => _ffmpegProc; set => _ffmpegProc = value; }
        public Process? FfmpegProc720 { get => _ffmpegProc720; set => _ffmpegProc720 = value; }

        private readonly ILogger _log = logger.ForContext("Source", "Transcoder");
        private enum TranscodeMode { Original, p720, FastStart }

        public void SetBufferQueue(BlockingCollection<string> bufferFilesQueue, int bufferSize)
        {
            _bufferFilesQueue = bufferFilesQueue;
            _bufferSize = bufferSize;
        }
        private string PrepareToTranscoding(string transcodeResultDirectory)
        {
            return Path.Combine(transcodeResultDirectory, $"rec{_outputFileIndex++}_%Y-%m-%d_%H-%M-%S.temp.mp4");
        }
        private static (int, int) GetVideoRes(string mp4)
        {
            using var info = TagLib.File.Create(mp4);
            return (info.Properties.VideoWidth, info.Properties.VideoHeight);
        }
        private string[] BuildArgs(string input, string output, TranscodeMode quality)
        {
            var args = new List<string>
            {
                "-y"
            };

            if (quality == TranscodeMode.Original)
            {
                if (OperatingSystem.IsWindows())
                {
                    args.AddRange
                        (
                        [
                            "-hwaccel",                 "qsv",
                            "-hwaccel_output_format",   "qsv",
                            "-extra_hw_frames",         "64",
                            "-i",                       input,
                            "-c:v",                     "hevc_qsv",
                            "-preset",                  "1",
                            "-global_quality",          "20"
                        ]
                        );
                }
                else
                {
                    args.AddRange
                        (
                        [
                            "-i",       input,
                            "-c:v",     "libx264",
                            "-preset",  "fast",
                            "-crf",     "22"
                        ]
                        );
                }
            }

            if (quality == TranscodeMode.p720)
            {
                args.AddRange
                    (
                    [
                        "-i",       input,
                        "-vf",      "scale=-2:720",
                        "-c:v",     "libx264",
                        "-preset",  "fast",
                        "-crf",     "22"
                    ]
                    );
            }

            if (quality != TranscodeMode.FastStart)
            {
                args.AddRange
                (
                [
                    "-c:a",                 "copy",
                    "-f",                   "segment",
                    "-segment_time",        "4500",
                    "-reset_timestamps",    "1",
                    "-segment_format",      "mp4",
                    "-strftime",            "1"
                ]
                );
            }
            else
            {
                args.AddRange
                (
                [
                    "-i",   input,
                    "-map", "0",
                    "-c",   "copy"
                ]
                );
            }

            if (quality != TranscodeMode.Original)
                args.AddRange(["-movflags", "+faststart"]);

            args.Add(output);

            return args.ToArray();
        }
        public async Task StartTranscoding(string pathForOutputResult, TelegramChannelService tgChannel, Result720CleanerService result720Cleaner, CancellationToken cts)
        {
            if (_bufferFilesQueue == null)
                throw new InvalidOperationException("Buffer queue is not set. Call SetBufferQueue() first.");

            if (FfmpegProc is not null && !FfmpegProc.HasExited)
                return;

            string? currentBufferFile = null;
            long currentBufferPos = 0;

            await using var gateToken = await Globals.FfmpegGate.AcquireAsync(cts);   // ← блокирует, если идёт apt-upgrade

            string resultDir = DirectoriesManager.CreateTranscodeResultDirectory(pathForOutputResult);

            var firstFile = _bufferFilesQueue.Take(cts);

            while (!File.Exists(firstFile))
            {
                await Task.Delay(50, cts);
            }

            currentBufferFile = firstFile;

            int i = 0;

            bool isReadyToUpload = false;

            while (!cts.IsCancellationRequested)
            {
                i++;
                if (i % 20 == 0)
                {
                    _log.Fatal("Множество попыток перезапуска ffmpeg завершились неудачно. Дальнейшее выполнение невозможно. Требуется ручное вмешательство.");
                    break;
                }

                var outFile = PrepareToTranscoding(resultDir);

                _log.Information("Запуск ffmpeg...");

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var args = BuildArgs("pipe:0", outFile, TranscodeMode.Original);

                foreach (var arg in args) ffmpegPsi.ArgumentList.Add(arg);

                try
                {
                    FfmpegProc = Process.Start(ffmpegPsi);
                }
                catch (Exception ex)
                {
                    _log.Fatal(ex, "Запуск ffmpeg не удался. Ошибка:");
                    return;
                }

                if (FfmpegProc is null)
                {
                    _log.Fatal("Запуск ffmpeg не удался.");
                    return;
                }

                var writer = FfmpegProc.StandardInput;
                var stdin = writer.BaseStream;

                var progressRegex = new Regex(@"^frame=\s*\d+", RegexOptions.Compiled);

                int lastProgressLen = 0;
                bool lastWasProgress = false;

                FfmpegProc.ErrorDataReceived += (s, e) => 
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    if (progressRegex.IsMatch(e.Data))
                    {
                        var text = e.Data.TrimEnd();
                        var pad = new string(' ', Math.Max(0, lastProgressLen - text.Length));

                        Console.Write($"\r[ffmpeg]: {text}{pad}");
                        lastProgressLen = text.Length;
                        lastWasProgress = true;
                    }
                    else
                    {
                        if (lastWasProgress)
                        {
                            Console.WriteLine();
                            lastWasProgress = false;
                            lastProgressLen = 0;
                        }

                        Console.WriteLine("[ffmpeg]: " + e.Data);
                    }
                };
                FfmpegProc.BeginErrorReadLine();

                _log.Information("ffmpeg успешно запущен.");

                try
                {
                    (currentBufferFile, currentBufferPos) = await StartStreamFragmentsFromBufferAsync(stdin, writer, currentBufferFile, currentBufferPos, cts);

                    if (currentBufferFile is null && _bufferFilesQueue.IsCompleted)
                    {
                        await FfmpegProc.WaitForExitAsync(cts);

                        Console.WriteLine();

                        isReadyToUpload = true;

                        _log.Information("ffmpeg успешно закончил перекодирование - все буффер-файлы оработаны.");

                        break;
                    }
                }
                catch (IOException io) when (io.InnerException is { HResult: unchecked((int)0x8007006D) } /*ERROR_BROKEN_PIPE*/)
                {
                    _log.Warning(io, "ffmpeg pipe-поток закрылся неожиданно - перезапуск ffmpeg...");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Неожиданное завершение ffmpeg - перезапуск...");
                }
                finally
                {
                    if (FfmpegProc != null)
                    {
                        if (!FfmpegProc!.HasExited)
                        {
                            try
                            {
                                using var to = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                await FfmpegProc.WaitForExitAsync(to.Token);
                            }
                            catch (OperationCanceledException) { }

                            if (!FfmpegProc.HasExited)
                                FfmpegProc.Kill(entireProcessTree: true);
                        }

                        _log.Warning($"ffmpeg exit code = {FfmpegProc!.ExitCode}");
                        FfmpegProc.Dispose();
                        FfmpegProc = null;
                    }
                }
            }

            if (isReadyToUpload)
            {
                try
                {
                    await FastStartPassAsync(resultDir, cts);
                }
                catch (Exception ex) when (ex is ThreadStateException or AggregateException)
                {
                    return;
                }

                string? finalDir = FinalizeOutputFolder(resultDir);

                if (finalDir == null) return;

                var videosList = Directory.GetFiles(finalDir, "*.mp4");
                var testVideo = videosList.FirstOrDefault();

                if (testVideo != null)
                {
                    (int width, int height) = GetVideoRes(testVideo);

                    _ = tgChannel.SendFinalStreamVOD(videosList, width, height, cts);

                    if (height > 720)
                    {
                        await StartTranscoding720(finalDir, tgChannel, result720Cleaner, cts);
                    }
                    else
                    {
                        _log.Error("Качество оригинальной записи <= 720p => не будет запущено перекодирование в более сжатое качество, но это не учтено при формировании заглавного сообщения в канал. Требуется ручное редактирование сообщения.");
                    }
                }
            }
        }
        private async Task<(string? bufferFile, long readPos)> StartStreamFragmentsFromBufferAsync(Stream stdIn, StreamWriter writer, string? bufferFile, long readPos, CancellationToken cts)
        {
            bufferFile ??= _bufferFilesQueue!.Take(cts);

            while (bufferFile is not null)
            {
                using var bufferFileStream = new FileStream
                    (
                        bufferFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, _bufferSize, useAsync: true
                    );
                var buffer = new byte[_bufferSize];

                _log.Information($"Начало чтение буффер файла ({bufferFile}) и перекодирование видео потока.");

                var stableFor = TimeSpan.Zero;

                while (true)
                {
                    if (readPos < bufferFileStream.Length)
                    {
                        bufferFileStream.Seek(readPos, SeekOrigin.Begin);
                        int read = await bufferFileStream.ReadAsync(buffer.AsMemory(), cts);
                        if (read > 0)
                        {
                            await stdIn.WriteAsync(buffer.AsMemory(0, read), cts);
                            readPos += read;
                            continue;
                        }
                    }

                    if (bufferFileStream.Length == readPos)
                    {
                        await Task.Delay(100, cts);
                        stableFor += TimeSpan.FromMilliseconds(100);
                        if (stableFor < TimeSpan.FromMilliseconds(500))
                            continue;
                    }
                    else
                    {
                        stableFor = TimeSpan.Zero;
                    }


                    if (_bufferFilesQueue!.IsAddingCompleted && bufferFileStream.Length == readPos)
                    {
                        _log.Information($"Завершение чтения буффер файла ({bufferFile}).");
                        break;
                    }

                    await Task.Delay(50, cts);
                }

                if (_bufferFilesQueue.IsAddingCompleted)
                {
                    if (!_bufferFilesQueue.TryTake(out bufferFile))
                        bufferFile = null;
                }
                else bufferFile ??= _bufferFilesQueue.Take(cts);

                readPos = 0;
            }

            await writer.WriteAsync("q\n".AsMemory(), cts);
            await writer.FlushAsync(cts);

            writer.Close();
            stdIn.Close();
            return (null, 0);
        }
        public async Task StartTranscoding720(string pathForOutputResult, TelegramChannelService tgChannel, Result720CleanerService result720Cleaner, CancellationToken cts)
        {
            if (FfmpegProc720 is not null && !FfmpegProc720.HasExited)
                return;

            var parentDir = Path.GetDirectoryName(pathForOutputResult);
            var currBuffDir = Directory.GetDirectories(parentDir!, "buffer_*").OrderByDescending(Path.GetFileName).FirstOrDefault();

            string resultDir = Path.Combine(pathForOutputResult, "720p");
            Directory.CreateDirectory(resultDir);

            var filesCount = Directory.EnumerateFiles(currBuffDir!, "*.ts", SearchOption.TopDirectoryOnly).Count();

            int i = 0;

            foreach (var buff in Directory.EnumerateFiles(currBuffDir!, "*.ts", SearchOption.TopDirectoryOnly))
            {
                i++;

                var outFile = Path.Combine(resultDir, "720rec_%Y-%m-%d_%H-%M-%S.mp4");

                _log.Information($"Запуск ffmpeg для перкодирования в 720p для файла {buff} ({i} из {filesCount})...");

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var args = BuildArgs(buff, outFile, TranscodeMode.p720);

                foreach (var arg in args) ffmpegPsi.ArgumentList.Add(arg);

                try
                {
                    FfmpegProc720 = Process.Start(ffmpegPsi);
                }
                catch (Exception ex)
                {
                    _log.Fatal(ex, $"Запуск ffmpeg для перекодирования в 720p для файла {buff} не удался. Ошибка:");
                    return;
                }

                if (FfmpegProc720 is null)
                {
                    _log.Fatal($"Запуск ffmpeg для перекодирования в 720p для файла {buff} не удался.");
                    return;
                }

                var progressRegex = new Regex(@"frame=\s*\d+.*time=(?<time>\d{2}:\d{2}:\d{2}\.\d+).*speed=\s*(?<speed>[\d\.]+)x", RegexOptions.Compiled);

                int lastProgressLen = 0;
                bool lastWasProgress = false;
                double lastPercent = 0;

                double fileDurationSec = await TelegramChannelService.GetDurationSeconds(buff, cts);

                FfmpegProc720!.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    var m = progressRegex.Match(e.Data);

                    if (m.Success)
                    {
                        var t = TimeSpan.Parse(m.Groups["time"].Value);
                        var speed = double.Parse(m.Groups["speed"].Value, CultureInfo.InvariantCulture);

                        var doneSec = t.TotalSeconds;
                        var percent = Math.Min(100, doneSec / fileDurationSec * 100);

                        var etaSec = (fileDurationSec - doneSec) / Math.Max(speed, 0.01);
                        var eta = TimeSpan.FromSeconds(etaSec);

                        var text = $"[ffmpeg] {percent,6:0.0}% | ETA {eta:hh\\:mm\\:ss} | {e.Data.Trim()}";
                        var pad = new string(' ', Math.Max(0, lastProgressLen - text.Length));

                        Console.Write($"\r[ffmpeg]: {text}{pad}");
                        lastProgressLen = text.Length;
                        lastWasProgress = true;
                        lastPercent = percent;
                    }
                    else
                    {
                        if (lastWasProgress)
                        {
                            Console.WriteLine();
                            lastWasProgress = false;
                            lastProgressLen = 0;
                        }

                        Console.WriteLine("[ffmpeg]: " + e.Data);
                    }
                };
                FfmpegProc720.BeginErrorReadLine();

                _log.Information($"ffmpeg для перекодирования в 720p для файла {buff} успешно запущен.");

                try
                {
                    await FfmpegProc720.WaitForExitAsync(cts);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Неожиданное завершение ffmpeg при попытке перекодирования файла {buff} в 720p. Оставшиеся файлы также не будет перекодированы -> никакие фрагменты в 720p не будут загружены в телеграм чат канала. Требуется ручное вмешательство. Файлы, которые были успешно перекодированы, будут удалены через 5 часов. Ошибка:");

                    return;
                }
                finally
                {
                    if (FfmpegProc720 != null)
                    {
                        if (!FfmpegProc720!.HasExited)
                        {
                            try
                            {
                                using var to = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                await FfmpegProc720.WaitForExitAsync(to.Token);
                            }
                            catch (OperationCanceledException) { }

                            if (!FfmpegProc720.HasExited)
                                FfmpegProc720.Kill(entireProcessTree: true);
                        }

                        _log.Warning($"ffmpeg exit code = {FfmpegProc720!.ExitCode}");
                        FfmpegProc720.Dispose();
                        FfmpegProc720 = null;
                    }
                }
            }

            await tgChannel.SendFinalStreamVOD720(Directory.GetFiles(resultDir, "*.mp4"), cts);

            Directory.Delete(resultDir, true);

            _ = result720Cleaner.StartAsync(2);
        }
        private async Task FastStartPassAsync(string dir, CancellationToken cts)
        {
            _log.Information("Запуск процесса сдвига метаданных перекодированных фрагментов для возможности потокового воспроизведения...");

            var maxDegree = Math.Max(1, Environment.ProcessorCount);
            using var limiter = new SemaphoreSlim(maxDegree);

            var tasks = new List<Task>();

            foreach (var tmp in Directory.EnumerateFiles(dir, "*.temp.mp4", SearchOption.TopDirectoryOnly))
            {
                await limiter.WaitAsync(cts);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var src = tmp.Replace(".temp.mp4", ".mp4");

                        _log.Information($"Запуск ffmpeg для файла {tmp} для копирования и сдвига метаданных...");

                        var psi = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            UseShellExecute = false,
                            RedirectStandardOutput = false,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        var args = BuildArgs(tmp, src, TranscodeMode.FastStart);

                        foreach (var arg in args) psi.ArgumentList.Add(arg);

                        Process? proc = null;

                        try
                        {
                            proc = Process.Start(psi)!;

                            if (proc is null)
                            {
                                _log.Fatal($"Запуск ffmpeg при попытке сдвинуть метаданные для файла {tmp} не удался. Требуется ручное вмешательство. Дальнейшее выполнение остановлено -> записи не будут выгружены в телеграм канал.");
                                throw new ThreadStateException();
                            }

                            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine(e.Data); };
                            proc.BeginErrorReadLine();

                            _log.Information($"ffmpeg для сдвига метаданных для файла {tmp} успешно запущен.");

                            await proc.WaitForExitAsync(cts);

                            if (proc.ExitCode != 0)
                            {
                                _log.Error($"Возможно ошибка при попытке сдвига метаданных для файла {tmp}, ffmpeg завершился с кодом: {proc.ExitCode}. Вероятно требуется ручное вмешательство. На всякий случай temp файл не будет сейчас удалён для дальнейшей диагностики, но процесс не остановлен -> в телеграм будет загружен возможно повреждённый файл {src}.");
                            }
                            else
                            {
                                _log.Information($"ffmpeg успешно сдвинул метаданные. Старый файл {tmp} будет удалён, его копия со сдвинутыми метаданными теперь в файле {src}.");
                                File.Delete(tmp);
                            }
                        }
                        catch (OperationCanceledException) { throw new ThreadStateException(); }
                        catch (ThreadStateException) { throw; }
                        catch (Exception ex)
                        {
                            _log.Fatal(ex, $"Запуск ffmpeg при попытке сдвинуть метаданные для файла {tmp} не удался. Дальнейшее выполнение остановлено -> записи не будут выгружены в телеграм канал. Ошибка:");
                            throw new ThreadStateException();
                        }
                        finally
                        {
                            proc?.Dispose();
                            proc = null;
                        }
                    }
                    catch (Exception) { throw; }
                    finally
                    {
                        limiter.Release();
                    }
                }, cts));
            }

            await Task.WhenAll(tasks);
            _log.Information("Процесс сдвига метаданных перекодированных фрагментов для возможности потокового воспроизведения завершён для всех фрагментов.");
        }
        public async Task ResetAsync(CancellationToken cts)
        {
            if (FfmpegProc != null)
            {
                await FfmpegProc!.WaitForExitAsync(cts);

                try { FfmpegProc!.StandardInput.Close(); }
                catch { }
            }
                
            _outputFileIndex = 0;
            FfmpegProc = null;
        }
        private string? FinalizeOutputFolder(string transcodeResultDirectory)
        {
            var src = transcodeResultDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var dst = src + "_FINISHED";
            int i = 1;

            while (Directory.Exists(dst))
                dst = $"{src}_FINISHED_{++i}";

            try
            {
                Directory.Move(src, dst);
                _log.Information($"Директория с результатами перекодирования переименована -> {dst}.");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Исключение при попытке переименовать директорию с результатами перекодирования. Обработанные файлы не будут загружены в телеграм. Требуется ручное вмешательство. Ошибка:");
                dst = null;
            }

            return dst;
        }
    }
}