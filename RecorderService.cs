using System.Collections.Concurrent;
using System.Diagnostics;

namespace TwitchStreamsRecorder
{
    internal class RecorderService
    {
        private int _bufferFileIndex = 0;
        private int _bufferSize;
        private BlockingCollection<string>? _bufferFilesQueue;
        private readonly List<Task> _pendingBufferCopies;
        private Process? _streamlinkProc;

        public Process? StreamlinkProc { get => _streamlinkProc; set => _streamlinkProc = value; }

        public RecorderService(List<Task> pendingBufferCopies)
        {
            _pendingBufferCopies = pendingBufferCopies;
        }
        private string PrepareToRecording(string recordBufferDirectory)
        {
            var bufferFile = Path.Combine(recordBufferDirectory, $"buffer{_bufferFileIndex++}.ts");

            _bufferFilesQueue!.Add(bufferFile);

            return bufferFile;
        }
        public async Task StartRecording(string twitchChannelLink, string pathForRecordBuffer, CancellationToken cts)
        {
            if (StreamlinkProc is { HasExited: false }) return;

            var bufferDir = DirectoriesManager.CreateRecordBufferDirectory(pathForRecordBuffer);

            while (!cts.IsCancellationRequested && Program.IsLive)
            {
                var bufferFile = PrepareToRecording(bufferDir);

                Console.WriteLine("///StartRecording///");

                var streamlinkPsi = new ProcessStartInfo
                {
                    FileName = "streamlink.exe",
                    Arguments = $"--stdout --twitch-disable-ads {twitchChannelLink} best",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                StreamlinkProc = Process.Start(streamlinkPsi) ?? throw new Exception("Failed to START streamlink");

                Console.WriteLine("Streamlink was STARTED");

                _pendingBufferCopies.Add
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
                    Console.WriteLine("STOP recording — stream offline - streamlink finished");
                    break;
                }

                Console.WriteLine($"!!! Streamlink CRASHED with exit code {StreamlinkProc.ExitCode} — restarting…");
            }

            try
            {
                await Task.WhenAll(_pendingBufferCopies);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Copy failed: {ex}");
            }
            finally
            {
                _bufferFilesQueue!.CompleteAdding(); 
            }

            // цикл закончился штатно
            StreamlinkProc?.Dispose();
            StreamlinkProc = null;          // ← ключевая строчка
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
            return Task.Run(async () =>
            {
                await using var bufferFileStream = new FileStream
                    (
                        bufferFile, FileMode.Create, FileAccess.Write, FileShare.Read, _bufferSize, useAsync: true
                    );

                await stdout.CopyToAsync(bufferFileStream, _bufferSize, cts);

                Console.WriteLine($"Finished writing to the buffer file {bufferFile}");
            }, cts);
        }
    }
}
