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

        private string PrepareToTranscoding(string transcodeResultDirectory)
        {
            return Path.Combine(transcodeResultDirectory, $"rec{_outputFileIndex++}_%Y-%m-%d_%H-%M-%S.mp4");
        }
        public async Task StartTranscoding(string pathForOutputResult, CancellationToken cts)
        {
            if (FfmpegProc is not null && !FfmpegProc.HasExited)
                return;

            string? currentBufferFile = null;
            long currentBufferPos = 0;

            string resultDir = DirectoriesManager.CreateTranscodeResultDirectory(pathForOutputResult);

            while (!cts.IsCancellationRequested)
            {
                while (_bufferFilesQueue!.Count == 0 && !cts.IsCancellationRequested)
                    await Task.Delay(100, cts);

                var outFile = PrepareToTranscoding(resultDir);

                Console.WriteLine("///StartTranscoding///");

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -threads 4 -i pipe:0 -c:v libx264 -preset fast -crf 22 " +
                   $"-c:a aac -b:a 128k -f segment -segment_time 3600 " +
                   $"-reset_timestamps 1 -segment_format mp4 -strftime 1 -movflags +faststart " +
                   $"\"{outFile}\"",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                FfmpegProc = Process.Start(ffmpegPsi) ?? throw new Exception("Failed to START ffmpeg");

                var writer = FfmpegProc.StandardInput;
                var stdin = writer.BaseStream;

                FfmpegProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine(e.Data); };
                FfmpegProc.BeginErrorReadLine();

                Console.WriteLine("ffmpeg was STARTED");

                try
                {
                    (currentBufferFile, currentBufferPos) = await StartStreamFragmentsFromBufferAsync(stdin, writer, currentBufferFile, currentBufferPos, cts);

                    if (currentBufferFile is null && _bufferFilesQueue.IsCompleted)
                    {
                        await FfmpegProc.WaitForExitAsync(cts);
                        Console.WriteLine("ffmpeg finished transcoding - all buffers processed");

                        string finalDir = FinalizeOutputFolder(resultDir);

                        await TelegramBotService.SendFinalStreamVOD(Directory.GetFiles(finalDir, "*.mp4"), cts);

                        FfmpegProc.Dispose();
                        FfmpegProc = null;

                        return;
                    }
                }
                catch (IOException io) when (io.InnerException is
                { HResult: unchecked((int)0x8007006D) } /*ERROR_BROKEN_PIPE*/)
                {
                    Console.Error.WriteLine("ffmpeg pipe closed unexpectedly — restarting...");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Fatal error in transcoding: {ex} - restarting...");
                }
                finally
                {
                    if ( FfmpegProc != null )
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

                        Console.WriteLine($"ffmpeg exit code = {FfmpegProc!.ExitCode}");
                        FfmpegProc.Dispose();
                        FfmpegProc = null;
                    }
                }
            }
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

                Console.WriteLine($"START read buffer file {bufferFile}, transfer data to ffmpeg stream input and transcoding video");

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
                        Console.WriteLine($"Complete read buffer file {bufferFile} and transfer data to ffmpeg stream input");
                        break;
                    }

                    await Task.Delay(50, cts);
                }

                if (_bufferFilesQueue.IsAddingCompleted && !_bufferFilesQueue.TryTake(out bufferFile))
                {
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
        private static string FinalizeOutputFolder(string transcodeResultDirectory)
        {
            var src = transcodeResultDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var dst = src + "_FINISHED";
            int i = 1;

            while (Directory.Exists(dst))
                dst = $"{src}_FINISHED_{++i}";

            try
            {
                Directory.Move(src, dst);
                Console.WriteLine($"Folder renamed → {dst}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Rename failed: {ex.Message}");
            }

            return dst;
        }
    }
}
