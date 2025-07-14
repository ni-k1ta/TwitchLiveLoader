using Serilog;

namespace TwitchStreamsRecorder
{
    internal class Result720CleanerService : IAsyncDisposable
    {
        private readonly string _root;          
        private readonly TimeSpan _retention;    
        private readonly ILogger _log;
        private TimeSpan _timer;

        private readonly object _sync = new();
        private CancellationTokenSource? _loopCts;
        private Task? _worker;

        private int? _maxCycles;   // null = бесконечно
        private int _doneCycles;

        public Result720CleanerService(string root,
                                    TimeSpan retention,
                                    ILogger logger)
            => (_root, _retention, _log) = (root, retention, logger.ForContext("Source", "Result720Cleaner"));

        /// <summary>Возвращает <c>true</c>, если служба сейчас активна.</summary>
        public bool IsRunning
        {
            get
            {
                lock (_sync) return _worker is { IsCompleted: false };
            }
        }
        /// <summary>
        /// Запускает сервис. Если он уже работает – просто возвращает текущий Task.
        /// Можно указать <paramref name="cycles">сколько итераций выполнить</paramref>; по умолчанию — работает бесконечно.
        /// </summary>
        public Task StartAsync(int? cycles)
        {
            lock (_sync)
            {
                if (IsRunning) return _worker!;

                _loopCts = new CancellationTokenSource();
                _maxCycles = cycles;
                _doneCycles = 0;
                _log.Information($"Для мониторинга директорий с обработанными результатами в 720p установлено ограничеине проводимых циклов: {cycles}. После их завершения цикл мониторинг автоматически будет остановлен.");
                _worker = Task.Run(() => LoopAsync(_loopCts.Token));
                return _worker;
            }
        }
        public Task StartAsync()
        {
            lock (_sync)
            {
                if (IsRunning) return _worker!;

                _loopCts = new CancellationTokenSource();
                _worker = Task.Run(() => LoopAsync(_loopCts.Token));
                _maxCycles = null;
                return _worker;
            }
        }
        /// <summary>Останавливает цикл очистки и дожидается его завершения.</summary>
        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? worker;
            lock (_sync)
            {
                cts = _loopCts;
                worker = _worker;
                _loopCts = null;
                _worker = null;
            }

            if (cts is null) return; // уже остановлено

            cts.Cancel();
            try { if (worker != null) await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* ожидаемое */ }
            finally { cts.Dispose(); }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(5));
            _timer = TimeSpan.FromHours(5);

            _log.Information($"Запуск мониторинга директорий с обработанными результатами в 720p (периодичность: {_timer}; срок хранения: {_retention}).");

            while (await timer.WaitForNextTickAsync(ct))
            {
                try { CleanOnce(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Неожиданное исключение при попытке удалить директории с обработанными результатами в 720p. Ошибка:");
                }

                if (_maxCycles is not null && ++_doneCycles >= _maxCycles)
                {
                    _log.Information("Достиг лимита {_doneCycles}/{_maxCycles} циклов – останавливаюсь.", _doneCycles, _maxCycles);
                    break;
                }
            }
        }

        private void CleanOnce(CancellationToken ct)
        {
            int removed = 0;

            _log.Information("Начало сканирования директорий с обработанными результатами в 720p...");

            foreach (var sessionDir in Directory.EnumerateDirectories(_root, "*_*", SearchOption.TopDirectoryOnly))
            {
                foreach (var resDir in Directory.EnumerateDirectories(sessionDir, "result_*", SearchOption.TopDirectoryOnly))
                {
                    foreach (var res720Dir in Directory.EnumerateDirectories(resDir, "720*", SearchOption.TopDirectoryOnly))
                    {
                        ct.ThrowIfCancellationRequested();

                        var info = new DirectoryInfo(res720Dir);

                        var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                        if (age < _retention) continue;

                        try
                        {
                            info.Delete(recursive: true);
                            removed++;
                            _log.Information($"Удалена директория с обработанными результатами в 720p {info.FullName} по причине истечения срока хранения.");
                        }
                        catch (IOException io)
                        {
                            _log.Warning(io, $"Исключение при попытке удалить директорию с обработанными результатами в 720p {info.FullName}. Ошибка:");
                        }
                    }
                }
            }

            if (removed > 0)
                _log.Information($"Сканирование директорий с обработанными результатами в 720p завершено. Удалено {removed} директорий. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime().TimeOfDay}.");
            else
                _log.Information($"Сканирование директорий с обработанными результатами в 720p завершено. Не найдено директорий с истёкшим сроком хранения. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime().TimeOfDay}.");
        }

        // ------------ IDisposable ------------------------------------------------
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
