using Serilog;

namespace TwitchStreamsRecorder
{
    internal class DiskSpaceGuard : IDisposable
    {
        private readonly string _root;      
        private readonly long _minFreeBytes;
        private readonly TimeSpan _period;  
        private readonly ILogger _log;
        private readonly CancellationTokenSource _cts = new();

        public Task RunAsync() => Task.Run(LoopAsync);
        public void Dispose() => _cts.Cancel();

        public DiskSpaceGuard(string root, ILogger log, long minFreeBytes = 5L * 1024 * 1024 * 1024, TimeSpan? period = null) => 
            (_root, _minFreeBytes, _period, _log) = (root, minFreeBytes, period ?? TimeSpan.FromMinutes(15), log.ForContext("Source", "DiskGuard"));

        private async Task LoopAsync()
        {
            _log.Information($"Запуск лайв-мониторинга свободного места на диске (минимум = {_minFreeBytes} bytes, периодичность = {_period} мин.");

            while (!_cts.IsCancellationRequested)
            {
                try { CheckOnceAsync(); }
                catch (Exception ex) { _log.Error(ex, "Неожиданное исключение при попытке удалить буффер директории при лайв-мониторинге. Вероятно текущая запись будет прервана из-за недостатка свободного места на диске. Требуется ручное вмешательство. Ошибка:"); }

                try { await Task.Delay(_period, _cts.Token); }
                catch (TaskCanceledException) { }
            }

            _log.Information("Лайв-мониторинг свободного места на диске остановлен.");
        }

        private void CheckOnceAsync()
        {
            var drive = new DriveInfo(Path.GetPathRoot(_root)!);
            long free = drive.AvailableFreeSpace;
            if (free >= _minFreeBytes) return;

            _log.Warning($"Свободного места на диске ({free} bytes) меньше чем установленный минимум ({_minFreeBytes} bytes) -> запуск удаления старых буффер-директорий…");

            DateTime border = DateTime.Today.AddDays(-3);

            foreach (var dir in EnumerateBufferDirs().OrderBy(d => d.LastWriteTimeUtc))
            {
                if (free >= _minFreeBytes) break;

                if (dir.CreationTime >= border)
                {
                    _log.Warning($"Лайв монииторинг свободного места на диске при сканировании подходящих для удаления буффер-директорий дошёл до директорий, которые были созданы посзже чем {border} -> на диске недостаточно свободного места.");
                    continue;
                }

                try
                {
                    dir.Delete(true);
                    _log.Information($"Удалена buffer-директория {dir.FullName} по причине недостатка свободного места на диске.");
                    
                    free = new DriveInfo(drive.Name).AvailableFreeSpace;
                }
                catch (IOException io)
                {
                    _log.Error(io, $"Не удалось удалить буффер-директорию {dir.FullName} при лайв-мониторинге. Вероятно текущая запись может быть прервана из-за недостатка свободного места на диске. Требуется ручное вмешательство. Ошибка:");
                }
            }

            _log.Information($"Удаление старых буффер-директорий во время лайв мониторинга завершено успешно, свободного места на диске {free} bytes.");

            if (free <= _minFreeBytes)
                _log.Error($"Все буффер-директории были удалены, но свободного места всё равно недостаточно ({free} bytes). Требуется ручное вмешательство для очистки диска.");
        }
        private IEnumerable<DirectoryInfo> EnumerateBufferDirs() =>
        Directory.EnumerateDirectories(_root, "*_*", SearchOption.TopDirectoryOnly)
                .SelectMany(sessionDir => Directory.EnumerateDirectories(sessionDir, "buffer_*", SearchOption.TopDirectoryOnly))
                .Select(p => new DirectoryInfo(p));
    }
}
