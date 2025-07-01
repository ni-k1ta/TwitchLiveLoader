using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwitchStreamsRecorder.Helpers
{
    public sealed class FfmpegGate : IDisposable
    {
        private readonly SemaphoreSlim _sem = new(1, 1);

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(_sem);
        }
        private readonly struct Releaser : IAsyncDisposable
        {
            private readonly SemaphoreSlim _s;
            public Releaser(SemaphoreSlim s) => _s = s;
            public ValueTask DisposeAsync()
            {
                _s.Release();
                return ValueTask.CompletedTask;
            }
        }

        public void Dispose() => _sem.Dispose();
    }

    /// <summary>
    /// Упрощённый фоновой апдейтер ffmpeg без DI/HostedService.
    /// Проверяет apt‑репозитории раз в неделю и ставит обновление, когда ffmpeg‑процессы не запущены.
    /// На время установки блокирует общий <see cref="FfmpegGate"/> так, что новые процессы ffmpeg ожидают завершения апдейта.
    /// </summary>
    public sealed class FfmpegUpdater
    {
        private readonly TimeSpan _checkEvery;
        private readonly TimeSpan _retryDelay;
        private readonly FfmpegGate _gate;
        private readonly ILogger _log;
        private readonly CancellationToken _ct;

        public FfmpegUpdater(FfmpegGate gate,
                             TimeSpan? checkEvery,
                             TimeSpan? retryDelay,
                             ILogger log,
                             CancellationToken ct)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _checkEvery = checkEvery ?? TimeSpan.FromDays(7);
            _retryDelay = retryDelay ?? TimeSpan.FromHours(3);
            _log = log.ForContext("Source", "FfmpegUpdater") ?? throw new ArgumentNullException(nameof(log));
            _ct = ct;
        }

        /// <summary>Запускает петлю в пуле потоков и возвращает Task, который можно ожидать при остановке приложения.</summary>
        public Task RunAsync() => Task.Run(LoopAsync, _ct);

        // ------------------------------- private ----------------------------
        private async Task LoopAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _log.Warning("Платформа не Linux – апдейтер ffmpeg не запущен!");
                return;
            }

            var timer = new PeriodicTimer(_checkEvery);

            _log.Information($"Запуск мониторинга обновлений для ffmpeg (периодичность: {_checkEvery}).");
            
            while (await timer.WaitForNextTickAsync(_ct).ConfigureAwait(false))
            {
                try
                {
                    _log.Information("Проверка наличия обновлений для ffmpeg...");
                    if (!await UpdateAvailableAsync())
                    {
                        _log.Information("Нет новых версий для ffmpeg.");
                        continue;
                    }

                    _log.Information("Обнаружено обновление для ffmpeg.");

                    // ждём, пока не останется процессов ffmpeg
                    while (Process.GetProcessesByName("ffmpeg").Length > 0)
                    {
                        _log.Warning("Найден запущенный процесс ffmpeg – откладываем обновление на {Delay}.", _retryDelay);
                        await Task.Delay(_retryDelay, _ct);
                    }

                    // блокируем gate, чтобы не стартовали новые процессы
                    await using var _ = await _gate.AcquireAsync(_ct);

                    await Task.Delay(TimeSpan.FromSeconds(30));

                    _log.Information("Начинаю apt‑upgrade ffmpeg...");
                    var ok = await RunBashAsync("sudo apt-get update && sudo apt-get install -y ffmpeg");
                    _log.Information(ok ? "ffmpeg обновлён успешно." : "ffmpeg apt‑upgrade завершился ошибкой – см. консоль лог.");
                }
                catch (OperationCanceledException) { /* graceful stop */ }
                catch (Exception ex)
                {
                    _log.Error(ex, "Неожиданная ошибка в цикле обновления ffmpeg. Требуется ручное вмешательство. Ошибка:");
                }
            }
        }

        private static async Task<bool> UpdateAvailableAsync()
        {
            var result = await RunBashCaptureAsync("apt list --upgradeable 2>/dev/null | grep -E '^ffmpeg/' || true");
            return !string.IsNullOrWhiteSpace(result);
        }

        private static async Task<bool> RunBashAsync(string cmd)
        {
            var psi = new ProcessStartInfo("bash", "-c \"" + cmd.Replace("\"", "\\\"") + "\"")
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("[ffmpeg update bash]: " + e.Data); };
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }

        private static async Task<string> RunBashCaptureAsync(string cmd)
        {
            var psi = new ProcessStartInfo("bash", "-c \"" + cmd.Replace("\"", "\\\"") + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("[ffmpeg update bash]: " + e.Data); };
            p.BeginErrorReadLine();
            var txt = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return txt.Trim();
        }
    }
}