using Serilog;

namespace TwitchStreamsRecorder
{
    internal class Result720CleanerService
    {
        private readonly string _root;          
        private readonly TimeSpan _retention;    
        private readonly ILogger _log;
        private readonly CancellationToken _stop;
        private TimeSpan _timer;

        public Result720CleanerService(string root,
                                    TimeSpan retention,
                                    ILogger logger,
                                    CancellationToken stop)
            => (_root, _retention, _log, _stop) = (root, retention, logger.ForContext("Source", "Result720Cleaner"), stop);

        public Task RunAsync() => Task.Run(LoopAsync);     

        private async Task LoopAsync()
        {
            var timer = new PeriodicTimer(TimeSpan.FromHours(5));
            _timer = TimeSpan.FromHours(5);

            _log.Debug($"Запуск мониторинга директорий с обработанными результатами в 720p (периодичность: {_timer}; срок хранения: {_retention}).");

            while (await timer.WaitForNextTickAsync(_stop))
            {
                try { CleanOnce(); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Неожиданное исключение при попытке удалить директории с обработанными результатами в 720p. Ошибка:");
                }
            }
        }

        private void CleanOnce()
        {
            int removed = 0;

            _log.Information("Начало сканирования директорий с обработанными результатами в 720p...");

            foreach (var sessionDir in Directory.EnumerateDirectories(_root, "*_*", SearchOption.TopDirectoryOnly))
            {
                foreach (var resDir in Directory.EnumerateDirectories(sessionDir, "result_*", SearchOption.TopDirectoryOnly))
                {
                    foreach (var res720Dir in Directory.EnumerateDirectories(resDir, "720", SearchOption.TopDirectoryOnly))
                    {
                        var info = new DirectoryInfo(res720Dir);

                        var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                        if (age < _retention) continue;

                        try
                        {
                            info.Delete(recursive: true);
                            removed++;
                            _log.Information($"Удалена директория с обработанными результатами в 720p {info.FullName} по причине истчения срока хранения.");
                        }
                        catch (IOException io)
                        {
                            _log.Warning(io, $"Исключение при попытке удалить директорию с обработанными результатами в 720p {info.FullName}. Ошибка:");
                        }
                    }
                }
            }

            if (removed > 0)
                _log.Information($"Сканирование директорий с обработанными результатами в 720p завершено. Удалено {removed} директорий. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime()}.");
            else
                _log.Information($"Сканирование директорий с обработанными результатами в 720p завершено. Не найдено директорий с истёкшым сроком хранения. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime()}.");
        }
    }
}
