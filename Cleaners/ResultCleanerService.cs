using Serilog;

namespace TwitchStreamsRecorder
{
    internal class ResultCleanerService
    {
        private readonly string _root;          
        private readonly TimeSpan _retention;    
        private readonly ILogger _log;
        private readonly CancellationToken _stop;

        public ResultCleanerService(string root,
                                    TimeSpan retention,
                                    ILogger logger,
                                    CancellationToken stop)
            => (_root, _retention, _log, _stop) = (root, retention, logger.ForContext("Source", "ResultCleaner"), stop);

        public Task RunAsync() => Task.Run(LoopAsync);     

        private async Task LoopAsync()
        {
            _log.Debug("Запуск мониторинга директорий с обработанными результатами в 1080p.");

            var timer = new PeriodicTimer(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(10));

            while (await timer.WaitForNextTickAsync(_stop))
            {
                try { CleanOnce(); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Неожиданное исключение при попытке удалить директории с обработанными результатамив 1080p. Ошибка:");
                }
            }
        }

        private void CleanOnce()
        {
            int removed = 0;

            _log.Information("Начало сканирования директорий с обработанными результатами в 1080p...");

            foreach (var sessionDir in Directory.EnumerateDirectories(_root, "*_*", SearchOption.TopDirectoryOnly))
            {
                foreach (var resDir in Directory.EnumerateDirectories(sessionDir, "result_*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(resDir);

                    var age = DateTime.Now - info.LastWriteTimeUtc;
                    if (age < _retention) continue;

                    try
                    {
                        info.Delete(recursive: true);
                        removed++;
                        _log.Information($"Удалена директория с обработанными результатами в 1080p {info.FullName} по причине истчения срока хранения.");
                    }
                    catch (IOException io)
                    {
                        _log.Warning(io, $"Исключение при попытке удалить директорию с обработанными результатами в 1080p {info.FullName}. Ошибка:");
                    }
                }
            }

            if (removed > 0)
                _log.Information($"Сканирование директорий с обработанными результатами в 1080p завершено. Удалено {removed} директорий.");
            else
                _log.Information("Сканирование директорий с обработанными результатами в 1080p завершено. Не найдено директорий с истёкшым сроком хранения.");
        }
    }
}
