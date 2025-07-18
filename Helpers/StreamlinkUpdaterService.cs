using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwitchStreamsRecorder.Helpers
{
        /// <summary>
        /// Глобальный «шлагбаум» для запуска streamlink-процессов
        /// (блокируется на время apt/pip-upgrade).
        /// </summary>
        public sealed class StreamlinkGate : IDisposable
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
        /// Проверяет обновления streamlink (apt или pip — см. <c>UsePip</c>)
        /// раз в неделю; во время апдейта блокирует <see cref="StreamlinkGate"/>.
        /// </summary>
        public sealed class StreamlinkUpdater
        {
            private readonly TimeSpan _checkEvery;
            private readonly TimeSpan _retryDelay;
            private readonly StreamlinkGate _gate;
            private readonly ILogger _log;
            private readonly CancellationToken _ct;
            private readonly bool _usePip;   // true => pip install --upgrade streamlink

            public StreamlinkUpdater(
                StreamlinkGate gate,
                TimeSpan? checkEvery,
                TimeSpan? retryDelay,
                ILogger log,
                CancellationToken ct,
                bool usePip = false)
            {
                _gate = gate ?? throw new ArgumentNullException(nameof(gate));
                _checkEvery = checkEvery ?? TimeSpan.FromDays(7);
                _retryDelay = retryDelay ?? TimeSpan.FromHours(3);
                _log = log.ForContext("Source", "StreamlinkUpdater") ?? throw new ArgumentNullException(nameof(log));
                _ct = ct;
                _usePip = usePip;
            }

            public Task RunAsync() => Task.Run(LoopAsync, _ct);

            // ================= private =================
            private async Task LoopAsync()
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _log.Warning("Платформа не Linux – апдейтер streamlink не запущен!");
                    return;
                }

                _log.Information($"Запуск мониторинга обновлений для streamlink (периодичность: {_checkEvery}).");
                var timer = new PeriodicTimer(_checkEvery);

                while (await timer.WaitForNextTickAsync(_ct).ConfigureAwait(false))
                {
                    try
                    {
                        _log.Information("Проверка наличия обновлений для streamlink...");
                        if (!await UpdateAvailableAsync())
                        {
                            _log.Information("Нет новых версий для streamlink.");
                            continue;
                        }

                        _log.Information("Обнаружено обновление для streamlink.");

                        // ждём, пока не запущен streamlink
                        while (Process.GetProcessesByName("streamlink").Length > 0)
                        {
                            _log.Warning("Найден запущенный процесс streamlink – откладываем обновление на {Delay}.", _retryDelay);
                            await Task.Delay(_retryDelay, _ct);
                        }

                        await using var _ = await _gate.AcquireAsync(_ct); // блокируем новые

                        var cmd = _usePip
                            ? "python3 -m pip install --upgrade streamlink"
                            : "sudo apt-get update && sudo apt-get install -y streamlink";

                        _log.Information($"Начинаю {cmd} для streamlink...");
                        var ok = await RunBashAsync(cmd);
                        _log.Information(ok ? "Streamlink обновлён успешно."
                                            : "Streamlink upgrade завершился ошибкой – см. консоль лог.");
                    }
                    catch (OperationCanceledException) { /* стоп приложения */ }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Неожиданная ошибка в цикле обновления streamlink. Требуется ручное вмешательство. Ошибка:");
                    }
                }
            }

            private async Task<bool> UpdateAvailableAsync()
            {
                if (_usePip)
                {
                    var txt = await RunBashCaptureAsync("python3 -m pip list --outdated | grep '^streamlink' || true");
                    return !string.IsNullOrWhiteSpace(txt);
                }
                else
                {
                    var txt = await RunBashCaptureAsync("apt list --upgradeable 2>/dev/null | grep '^streamlink/' || true");
                    return !string.IsNullOrWhiteSpace(txt);
                }
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
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("[streamlink update bash]: " + e.Data); };
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
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("[streamlink update bash]: " + e.Data); };
                p.BeginErrorReadLine();
                var txt = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                return txt.Trim();
            }
        }
}