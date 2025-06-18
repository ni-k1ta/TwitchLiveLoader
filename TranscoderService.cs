﻿using Microsoft.AspNetCore.Routing.Constraints;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace TwitchStreamsRecorder
{
    internal class TranscoderService
    {
        private int _outputFileIndex = 0;
        private int _bufferSize;
        private BlockingCollection<string>? _bufferFilesQueue;

        private Process? _ffmpegProc;

        public Process? FfmpegProc { get => _ffmpegProc; set => _ffmpegProc = value; }

        private readonly ILogger _log;

        public TranscoderService(ILogger logger) { _log = logger.ForContext("Source", "Transcoder"); }

        private string PrepareToTranscoding(string transcodeResultDirectory)
        {
            return Path.Combine(transcodeResultDirectory, $"rec{_outputFileIndex++}_%Y-%m-%d_%H-%M-%S.temp.mp4");
        }
        public async Task StartTranscoding(string pathForOutputResult, TelegramChannelService tgChannel, CancellationToken cts)
        {
            if (_bufferFilesQueue == null)
                throw new InvalidOperationException("Buffer queue is not set. Call SetBufferQueue() first.");


            if (FfmpegProc is not null && !FfmpegProc.HasExited)
                return;

            string? currentBufferFile = null;
            long currentBufferPos = 0;

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

                ffmpegPsi.ArgumentList.Add("-y");
                ffmpegPsi.ArgumentList.Add("-hwaccel"); ffmpegPsi.ArgumentList.Add("qsv");
                ffmpegPsi.ArgumentList.Add("-hwaccel_output_format"); ffmpegPsi.ArgumentList.Add("qsv");
                ffmpegPsi.ArgumentList.Add("-extra_hw_frames"); ffmpegPsi.ArgumentList.Add("64");
                ffmpegPsi.ArgumentList.Add("-i"); ffmpegPsi.ArgumentList.Add("pipe:0");
                ffmpegPsi.ArgumentList.Add("-c:v"); ffmpegPsi.ArgumentList.Add("hevc_qsv");
                ffmpegPsi.ArgumentList.Add("-preset"); ffmpegPsi.ArgumentList.Add("1");
                ffmpegPsi.ArgumentList.Add("-global_quality"); ffmpegPsi.ArgumentList.Add("20");
                ffmpegPsi.ArgumentList.Add("-c:a"); ffmpegPsi.ArgumentList.Add("copy");
                ffmpegPsi.ArgumentList.Add("-f"); ffmpegPsi.ArgumentList.Add("segment");
                ffmpegPsi.ArgumentList.Add("-segment_time"); ffmpegPsi.ArgumentList.Add("3600");
                ffmpegPsi.ArgumentList.Add("-reset_timestamps"); ffmpegPsi.ArgumentList.Add("1");
                ffmpegPsi.ArgumentList.Add("-segment_format"); ffmpegPsi.ArgumentList.Add("mp4");
                ffmpegPsi.ArgumentList.Add("-strftime"); ffmpegPsi.ArgumentList.Add("1");
                ffmpegPsi.ArgumentList.Add(outFile);

                //ffmpegPsi.ArgumentList.Add("-y");
                ////ffmpegPsi.ArgumentList.Add("-threads");
                ////ffmpegPsi.ArgumentList.Add("4");
                //ffmpegPsi.ArgumentList.Add("-i"); ffmpegPsi.ArgumentList.Add("pipe:0");
                //ffmpegPsi.ArgumentList.Add("-c:v"); ffmpegPsi.ArgumentList.Add("libx264");
                //ffmpegPsi.ArgumentList.Add("-preset"); ffmpegPsi.ArgumentList.Add("medium");
                //ffmpegPsi.ArgumentList.Add("-crf"); ffmpegPsi.ArgumentList.Add("22");
                ////ffmpegPsi.ArgumentList.Add("-c:a"); ffmpegPsi.ArgumentList.Add("aac");
                ////ffmpegPsi.ArgumentList.Add("-b:a"); ffmpegPsi.ArgumentList.Add("128k");
                //ffmpegPsi.ArgumentList.Add("-c:a"); ffmpegPsi.ArgumentList.Add("copy");
                //ffmpegPsi.ArgumentList.Add("-f"); ffmpegPsi.ArgumentList.Add("segment");
                //ffmpegPsi.ArgumentList.Add("-segment_time"); ffmpegPsi.ArgumentList.Add("3600");
                //ffmpegPsi.ArgumentList.Add("-reset_timestamps"); ffmpegPsi.ArgumentList.Add("1");
                //ffmpegPsi.ArgumentList.Add("-segment_format"); ffmpegPsi.ArgumentList.Add("mp4");
                //ffmpegPsi.ArgumentList.Add("-strftime"); ffmpegPsi.ArgumentList.Add("1");
                ////ffmpegPsi.ArgumentList.Add("-movflags"); ffmpegPsi.ArgumentList.Add("+faststart");
                //ffmpegPsi.ArgumentList.Add(outFile);

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

                _log.Information("ffmpeg успешно запущен.");

                var writer = FfmpegProc.StandardInput;
                var stdin = writer.BaseStream;

                FfmpegProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine(e.Data); };
                FfmpegProc.BeginErrorReadLine();

                try
                {
                    (currentBufferFile, currentBufferPos) = await StartStreamFragmentsFromBufferAsync(stdin, writer, currentBufferFile, currentBufferPos, cts);

                    if (currentBufferFile is null && _bufferFilesQueue.IsCompleted)
                    {
                        await FfmpegProc.WaitForExitAsync(cts);

                        _log.Information("ffmpeg успешно закончил перекодирование - все буффер-файлы оработаны.");

                        isReadyToUpload = true;

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
                    _log.Warning(ex, "Неожиданное заверешние ffmpeg - перезапуск...");
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
                catch (ThreadStateException)
                {
                    return;
                }

                string? finalDir = FinalizeOutputFolder(resultDir);

                if (finalDir == null) return;

                await tgChannel.SendFinalStreamVOD(Directory.GetFiles(finalDir, "*.mp4"), cts);
            }
        }

        private async Task FastStartPassAsync(string dir, CancellationToken cts)
        {
            _log.Information("Запуск процесса сдвига метаданных перекодированных фрагментов для возможности потокового воспроизведения...");

            Path.GetFileNameWithoutExtension(dir);

            foreach (var tmp in Directory.EnumerateFiles(dir, "*.temp.mp4", SearchOption.TopDirectoryOnly))
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

                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(tmp);
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
                psi.ArgumentList.Add(src);

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
                catch (ThreadStateException) { throw new ThreadStateException(); }
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
        public void SetBufferQueue(BlockingCollection<string> bufferFilesQueue, int bufferSize)
        {
            _bufferFilesQueue = bufferFilesQueue;
            _bufferSize = bufferSize;
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
                        _log.Information($"Заверешние чтения буффер файла ({bufferFile}).");
                        break;
                    }

                    await Task.Delay(50, cts);
                }

                if (_bufferFilesQueue.IsAddingCompleted)
                {
                    if (!_bufferFilesQueue.TryTake(out bufferFile))
                        bufferFile = null;
                }
                else if (bufferFile is null)
                {
                    bufferFile = _bufferFilesQueue.Take(cts);
                }

                readPos = 0;
            }

            await writer.WriteAsync("q\n".AsMemory(), cts);
            await writer.FlushAsync();

            writer.Close();
            stdIn.Close();
            return (null, 0);
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
