using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace TwitchStreamsRecorder
{
    internal class RecorderService
    {
        private int _bufferFileIndex = 0;
        private int _bufferSize;
        private BlockingCollection<string>? _bufferFilesQueue;
        private readonly ConcurrentQueue<Task> _pendingBufferCopies;
        private Process? _streamlinkProc;

        public Process? StreamlinkProc { get => _streamlinkProc; set => _streamlinkProc = value; }

        private readonly ILogger _log;

        DiskSpaceGuard? _diskGuard = null;

        public RecorderService(ConcurrentQueue<Task> pendingBufferCopies, ILogger log)
        {
            _pendingBufferCopies = pendingBufferCopies;
            _log = log.ForContext("Source", "Recorder");
        }
        private string PrepareToRecording(string recordBufferDirectory)
        {
            var bufferFile = Path.Combine(recordBufferDirectory, $"buffer{_bufferFileIndex++}.ts");

            _bufferFilesQueue!.Add(bufferFile);

            return bufferFile;
        }
        public async Task StartRecording(string twitchChannelLink, string pathForRecordBuffer, TelegramChannelService tgChannel, string OAuthToken, CancellationToken cts)
        {
            if (_bufferFilesQueue is null)
                throw new InvalidOperationException($"[{DateTime.Now:HH:mm:ss}] Buffer queue is not set. Call SetBufferQueue() first.");

            if (StreamlinkProc is { HasExited: false }) return;

            var bufferDir = DirectoriesManager.CreateRecordBufferDirectory(pathForRecordBuffer);

            int i = 0;

            _diskGuard = new(AppContext.BaseDirectory, _log);
            _ = _diskGuard.RunAsync();

            while (!cts.IsCancellationRequested && Program.IsLive)
            {
                i++;
                if (i % 20 == 0)
                {
                    _log.Fatal("Множество попыток перезапуска Streamlink завершились неудачно. Дальнейшее выполнение невозможно. Требуется ручное вмешательство.");
                    break;
                }

                var bufferFile = PrepareToRecording(bufferDir);

                _log.Information("Запуск Streamlink...");

                var streamlinkPsi = new ProcessStartInfo
                {
                    FileName = "streamlink",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                streamlinkPsi.ArgumentList.Add("--stdout");
                streamlinkPsi.ArgumentList.Add("--twitch-disable-ads");
                streamlinkPsi.ArgumentList.Add("--twitch-api-header");
                streamlinkPsi.ArgumentList.Add($"Authorization=Bearer {OAuthToken}");
                streamlinkPsi.ArgumentList.Add(twitchChannelLink);
                streamlinkPsi.ArgumentList.Add("best");

                try
                {
                    StreamlinkProc = Process.Start(streamlinkPsi);
                }
                catch (Exception ex)
                {
                    _log.Fatal(ex, "Запуск Streamlink не удался. Ошибка:");
                    return;
                }
                
                if (StreamlinkProc is null)
                {
                    _log.Fatal("Запуск Streamlink не удался.");
                    return;
                }

                _log.Information("Streamlink успешно запущен.");

                _pendingBufferCopies.Enqueue
                    (
                    StartWritingFragmentsToBufferAsync(StreamlinkProc.StandardOutput.BaseStream, bufferFile, cts).ContinueWith(t =>
                    {
                        if (t.Exception != null)
                            Console.Error.WriteLine(t.Exception);
                    }
                    ));

                StreamlinkProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine(e.Data); };
                StreamlinkProc.BeginErrorReadLine();

                await StreamlinkProc.WaitForExitAsync(cts);

                if (StreamlinkProc.ExitCode == 0 || StreamlinkProc.ExitCode == 1)
                {
                    _log.Information("Streamlink закончил работу - Стрим завершён - Запись остановлена");

                    await tgChannel.FinalizeStreamOnlineMsg(cts);

                    break;
                }

                _log.Warning($"!!! Streamlink CRASHED with exit code {StreamlinkProc.ExitCode} — restarting…");
            }

            try
            {
                await Task.WhenAll(_pendingBufferCopies.ToArray());
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Запись буффер файлов не завершилась корректно. Вероятнее всего конечный результат будет повреждён. Ошибка:");
            }
            finally
            {
                _bufferFilesQueue!.CompleteAdding(); 
            }

            _diskGuard?.Dispose();

            StreamlinkProc?.Dispose();
            StreamlinkProc = null;
        }
        public async Task ResetAsync(CancellationToken cts)
        {
            if (StreamlinkProc != null)
                await StreamlinkProc!.WaitForExitAsync(cts);

            await Task.WhenAll(_pendingBufferCopies);

            if (StreamlinkProc != null)
            {
                try { StreamlinkProc!.StandardOutput.Close(); }
                catch { }
            }

            _bufferFileIndex = 0;
            StreamlinkProc = null;
        }
        public void SetBufferQueue(BlockingCollection<string> bufferFilesQueue, int bufferSize)
        {
            _bufferFilesQueue = bufferFilesQueue;
            _bufferSize = bufferSize;
        }
        private Task StartWritingFragmentsToBufferAsync(Stream stdout, string bufferFile, CancellationToken cts)
        {
            _log.Information($"Запущен процесс записи потокового вывода Streamlink в буффер файл ({bufferFile})");
            return Task.Run(async () =>
            {
                await using var bufferFileStream = new FileStream
                    (
                        bufferFile, FileMode.Create, FileAccess.Write, FileShare.Read, _bufferSize, useAsync: true
                    );

                await stdout.CopyToAsync(bufferFileStream, _bufferSize, cts);

                _log.Information($"Запись в буффер файл ({bufferFile}) успешно завершена.");
            }, cts);
        }
    }
}
