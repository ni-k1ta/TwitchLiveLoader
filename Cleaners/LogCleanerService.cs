﻿using Serilog;

internal sealed class LogCleanerService(string directory,
                         string pattern,
                         TimeSpan retention,
                         ILogger logger,
                         CancellationToken ct)
{
    private readonly string _dir = directory;         
    private readonly string _pattern = pattern;     
    private readonly TimeSpan _retention = retention; 
    private readonly ILogger _log = logger.ForContext("Source", "LogCleaner");
    private readonly CancellationToken _ct = ct;
    private TimeSpan _timer;

    public Task RunAsync() => Task.Run(LoopAsync, _ct);

    private async Task LoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5));
        _timer = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5);

        _log.Debug($"Запуск мониторинга устаревших лог файлов (периодичность: {_timer}; срок хранения: {_retention}).");

        while (await timer.WaitForNextTickAsync(_ct))
        {
            try { CleanOnce(); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Неожиданное исключение при попытке удалить устаревшие лог файлы. Ошибка:");
            }
        }
    }

    private void CleanOnce()
    {
        int removed = 0;

        _log.Information("Начало сканирования лог файлов...");

        foreach (var file in Directory.EnumerateFiles(_dir, _pattern,
                                                      SearchOption.TopDirectoryOnly))
        {
            _ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);

            var age = DateTime.UtcNow - info.LastWriteTimeUtc;
            if (age < _retention) continue;

            try
            {
                info.Delete();
                removed++;
                _log.Information("Удалён старый лог файл {File}", info.Name);
            }
            catch (IOException io)
            {
                _log.Warning(io, "Не удалось удалить лог файл {File}", info.Name);
            }
            catch (UnauthorizedAccessException ua)
            {
                _log.Warning(ua, "Нет прав на удаление лог файла {File}", info.Name);
            }
        }

        if (removed > 0)
            _log.Information($"Сканирование лог файлов завершено. Удалено {removed} файлов. Следующее сканирование через {_timer} в {(DateTime.UtcNow.Add(_timer)).ToLocalTime()}.");
        else
            _log.Information($"Сканирование лог файлов завершено. Не найдено файлов с истёкшым сроком хранения. Следующее сканирование через {_timer} в {(DateTime.UtcNow.Add(_timer)).ToLocalTime()}.");
    }
}
