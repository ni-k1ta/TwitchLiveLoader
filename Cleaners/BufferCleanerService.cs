using Serilog;

namespace TwitchStreamsRecorder
{
    internal class BufferCleanerService
    {
        private readonly string _root;
        private readonly TimeSpan _retention;
        private readonly ILogger _log;
        private readonly CancellationToken _stop;

        private TimeSpan _timer;

        public BufferCleanerService(string root,
                                    TimeSpan retention,
                                    ILogger logger,
                                    CancellationToken stop)
            => (_root, _retention, _log, _stop) = (root, retention, logger.ForContext("Source", "BufferCleaner"), stop);

        public Task RunAsync() => Task.Run(LoopAsync);   

        private async Task LoopAsync()
        {
            var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            _timer = TimeSpan.FromHours(24);

            _log.Debug($"Запуск мониторинга буффер директорий (периодичность: {_timer}; срок хранения: {_retention}).");

            while (await timer.WaitForNextTickAsync(_stop))
            {
                try { CleanOnce(); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Неожиданное исключение при попытке удалить буффер директории. Ошибка:");
                }
            }
        }

        private void CleanOnce()
        {
            int removed = 0;

            _log.Information("Начало сканирования буффер директорий...");

            foreach (var sessionDir in Directory.EnumerateDirectories(_root, "*_*", SearchOption.TopDirectoryOnly))
            {
                foreach (var bufDir in Directory.EnumerateDirectories(sessionDir, "buffer_*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(bufDir);

                    var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                    if (age < _retention) continue;

                    try
                    {
                        info.Delete(recursive: true);
                        removed++;
                        _log.Information($"Удалена буффер директория {info.FullName} по причине истчения срока хранения.");
                    }
                    catch (IOException io)
                    {
                        _log.Warning(io, $"Исключение при попытке удалить буффер директорию {info.FullName}. Ошибка:");
                    }
                }
            }

            if (removed > 0)
                _log.Information($"Сканирование буффер директорий завершено. Удалено {removed} директорий. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime().TimeOfDay}.");
            else
                _log.Information($"Сканирование буффер директорий завершено. Не найдено директорий с истёкшым сроком хранения. Следующее сканирование через {_timer} в {(DateTime.UtcNow + _timer).ToLocalTime().TimeOfDay}.");
        }
    }
}
