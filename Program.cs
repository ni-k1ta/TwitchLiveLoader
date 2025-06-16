// Twitch auto‑recorder — JSON config + persistent token refresh
// -----------------------------------------------------------
// Packages:
//   TwitchLib.EventSub.Websockets 0.5.x
//   TwitchLib.Api
// Requires Streamlink in PATH
// ────────────────────────────────────────────────────────────
// recorder.json ( лежит рядом с exe )
/*
{
  "ClientId":      "aaaa1111bbbb2222",
  "ClientSecret":  "cccc3333dddd4444",
  "UserToken":     "eyJh…",          // access_token (может устареть)
  "RefreshToken":  "eyJy…",          // refresh_token (долго живёт)
  "ChannelLogin":  "lirik",
  "OutputDir":     ""               // если пусто → создаём YYYY‑MM‑DD рядом с exe
}
*/

using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using Telegram.Bot;
using TwitchLib.Api;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs.Channel;
using TwitchStreamsRecorder;

internal class Program
{
    private static readonly EventSubWebsocketClient _ws = new();

    private static readonly HttpClient _httpClient = new();

    private static volatile bool _isLive;
    private static readonly CancellationTokenSource _cts = new();

    static readonly ManualResetEventSlim _shutdownDone = new();
    static int _shutdownStarted;              

    public static bool IsLive { get => _isLive; set => _isLive = value; }

    static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(8);
    static CancellationTokenSource? _debounceCts;

    private static TwitchLib.EventSub.Core.AsyncEventHandler<ChannelUpdateArgs>? _channelUpdHandler;

    private static ILogger? _log;


    private static async Task Main()
    {
        Console.WriteLine("Twitch auto‑recorder starting…\n");

        var bufferFilesQueue = new BlockingCollection<string>();
        var pendingBufferCopies = new ConcurrentQueue<Task>();

        Task? recordingTask = null;
        Task? transcodingTask = null;

        var json = new ConfigService();

        var config  = json.LoadConfig(Path.Combine(AppContext.BaseDirectory, "recorder.json"));
        var twitchLink = $"twitch.tv/{config.ChannelLogin}";
        var bufferSize = 512 * 1024;

        var tgBot = new TelegramBotClient(config.TelegramBotToken);

        Log.Logger = Logging.InitRoot(tgBot, 448442642);

        var channelPath = Path.Combine(AppContext.BaseDirectory,
                                       $"{config.ChannelLogin}_.log");

        var channelLogger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Logging.Level)          
            .WriteTo.Logger(Log.Logger)                        
            .WriteTo.File(channelPath,
                          rollingInterval: RollingInterval.Day,
                          outputTemplate: Logging.Template)
            .CreateLogger()
            .ForContext("Channel", config.ChannelLogin);       

        _log = channelLogger;

        var recorder = new RecorderService(pendingBufferCopies, _log);
        var transcoder = new TranscoderService(_log);

        var api = new TwitchAPI();

        api.Settings.ClientId = config.ClientId;
        api.Settings.AccessToken = config.UserToken;

        var token = new TokenRefresher(config, api, _httpClient, _log);

        await token.RefreshAccessTokenAsync(true, () => json.SaveConfig(config, ConfigService.GetDefaultConfigPath()));

        string channelId = string.Empty;

        while (true)
        {
            channelId = await ResolveChannelIdAsync(config.ChannelLogin, api);
            
            if (string.IsNullOrEmpty(channelId))
            {
                _log.Information("Повторная попытка получить Twitch логин в Helix через 1 мин...");
                await Task.Delay(TimeSpan.FromMinutes(1));
                continue;
            }

            break;
        }

        _log.Information($"channelId = {channelId} for user login = {config.ChannelLogin}");

        var eventSubscribe = new TwitchEventSubscribeManager(api, _ws, config, channelId, _log);

        await using var tgChannel = new TelegramChannelService(config.TelegramChannelId, tgBot, 27680895, "8f219bef3d3da075c59e3084c7c0134c", _log);

        _ws.WebsocketConnected += async (_, __) =>
        {
            _log.Information($"WebSocket connected (session {_ws.SessionId})");
            await eventSubscribe.SubscribeStreamStatusAsync(token, json);
        };
        _ws.WebsocketReconnected += (_, __) =>
        {
            _log.Information("WebSocket reconnected — re‑subscribing (подписки будут пересозданы, т.к. прежний ID WebSocket'а более невалидный)…");
            return Task.CompletedTask;
        };

        _ws.WebsocketDisconnected += async (_, __) =>
        {
            _log.Warning("WebSocket disconnected");

            await token.RefreshAccessTokenAsync(true, () => json.SaveConfig(config, ConfigService.GetDefaultConfigPath()));

            await eventSubscribe.EnsureConnectedAsync();
        };

        _ws.ErrorOccurred += (_, e) => 
        {
            _log.Warning(e.Message, "WebSocket error:");
            return Task.CompletedTask; 
        };

        _ws.StreamOnline += async (_, e) =>
        {
            _log.Information("Стрим запущен, начало запсиси...");
            IsLive = true;

            var sessionDirectory = DirectoriesManager.CreateSessionDirectory(null, config.ChannelLogin);

            recorder.SetBufferQueue(bufferFilesQueue, bufferSize);
            transcoder.SetBufferQueue(bufferFilesQueue, bufferSize);

            recordingTask = Task.Run(() => recorder.StartRecording(twitchLink, sessionDirectory, _cts.Token, config.UserToken));
            transcodingTask = Task.Run(() => transcoder.StartTranscoding(sessionDirectory, tgChannel, _cts.Token));

            if (_channelUpdHandler == null)
            {
                _channelUpdHandler = async (sender, e) =>
                {
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = new CancellationTokenSource();

                    try
                    {
                        await Task.Delay(DebounceDelay, _debounceCts.Token);

                        var (newTitle, newCategory) = await GetStreamInfoAsync(api, channelId);

                        await tgChannel.UpdateStreamOnlineMsg(newTitle, newCategory, _cts.Token);
                    }
                    catch (OperationCanceledException) { }
                };

                _ws.ChannelUpdate += _channelUpdHandler;
            }

            var (title, category) = await GetStreamInfoAsync(api, channelId);

            await tgChannel.SendStreamOnlineMsg(title, category, _cts.Token);
        };

        _ws.StreamOffline += async (_, e) => 
        {
            _log.Information("Стрим завершён.");

            IsLive = false;

            _ = recorder.ResetAsync(_cts.Token);
            _ = transcoder.ResetAsync(_cts.Token);

            bufferFilesQueue = new BlockingCollection<string>();

            pendingBufferCopies = new ConcurrentQueue<Task>();

            await Task.Delay(TimeSpan.FromSeconds(10));

            _ws.ChannelUpdate -= _channelUpdHandler;
            _channelUpdHandler = null;

            await tgChannel.FinalizeStreamOnlineMsg(_cts.Token);
        };

        await _ws.ConnectAsync();

        (recordingTask, transcodingTask) = await CheckLiveAndStartAsync(api, twitchLink, channelId, recorder, transcoder, config, bufferFilesQueue, bufferSize, tgChannel);

        _ = Task.Run(() => token.RefreshLoopAsync(() => json.SaveConfig(config, ConfigService.GetDefaultConfigPath()), _cts.Token));
        _ = Task.Run(() => PendingQueueWatchDogAsync(pendingBufferCopies));

        var bufferCleaner = new BufferCleanerService(
                 root: AppContext.BaseDirectory,
                 retention: TimeSpan.FromDays(7),
                 logger: _log,
                 stop: _cts.Token);
        _ = bufferCleaner.RunAsync();

        var resultCleaner = new ResultCleanerService(
                 root: AppContext.BaseDirectory,
                 retention: TimeSpan.FromDays(15),
                 logger: _log,
                 stop: _cts.Token);
        _ = resultCleaner.RunAsync();

        var emptyCleaner = new EmptyDirectoryCleaner(AppContext.BaseDirectory, _cts.Token);
        _ = emptyCleaner.RunAsync();

        var logCleaner = new LogCleanerService(AppContext.BaseDirectory, "*.log", TimeSpan.FromDays(10), _log, _cts.Token);
        _ = logCleaner.RunAsync();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown(bufferFilesQueue, pendingBufferCopies, recordingTask, transcodingTask!, recorder, transcoder, eventSubscribe, token, json);
        };
        //AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        //{
        //    Shutdown(bufferFilesQueue, pendingBufferCopies, recordingTask, transcodingTask!, recorder, transcoder, eventSubscribe, token, json);
        //    _shutdownDone.Wait(TimeSpan.FromSeconds(10));
        //};

        _shutdownDone.Wait();
    }

    private static void Shutdown(BlockingCollection<string> bufferFilesQueue, ConcurrentQueue<Task> pendingBufferCopies, Task? recordingTask, Task transcodingTask, RecorderService recorder, TranscoderService transcoder, TwitchEventSubscribeManager eventSubscribe, TokenRefresher token, ConfigService json)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        Task.Run(async () =>
        {
            try
            {
                _cts.Cancel();

                await Task.WhenAll(Safe(recordingTask), Safe(transcodingTask));

                bufferFilesQueue.CompleteAdding();

                //await Task.WhenAll([.. pendingBufferCopies]);
                while (pendingBufferCopies.TryDequeue(out var t))
                    await Safe(t);

                await KillProcessAsync(recorder.StreamlinkProc);
                recorder.StreamlinkProc = null;

                await KillProcessAsync(transcoder.FfmpegProc);
                transcoder.FfmpegProc = null;

                using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _ = eventSubscribe.DeleteAllSubscriptionsAsync(token, json).WaitAsync(subCts.Token).ContinueWith(_ => { });

                await Safe(() => _ws?.DisconnectAsync()!);

                _log!.Information("Shutdown finished.");
            }
            catch (Exception ex)
            {
                _log!.Warning(ex, "Shutdown failed.");
            }
            finally
            {
                _shutdownDone.Set();
            }
        });
    }

    static async Task KillProcessAsync(Process? p)
    {
        if (p is null) return;

        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) { }
        }
        finally
        {
            p.Dispose();
        }
    }

    static Task Safe(Task? t) => t ?? Task.CompletedTask;
    static Task Safe(Func<Task> asyncCall) => asyncCall == null ? Task.CompletedTask : Safe(asyncCall());

    private static async Task<(Task? recordingTask, Task? transcodingTask)> CheckLiveAndStartAsync(TwitchAPI api, string twitchLink, string channelId, RecorderService recorder, TranscoderService transcoder, Config config, BlockingCollection<string> bufferFilesQueue, int bufferSize, TelegramChannelService tgChannel)
    {
        var live = await api.Helix.Streams.GetStreamsAsync(userIds: [channelId]);

        if (live.Streams.Length > 0)
        {
            _log!.Information("Стрим уже идёт, начало записи (но не с начала стрима)...");

            IsLive = true;
            var sessionDirectory = DirectoriesManager.CreateSessionDirectory(null, config.ChannelLogin);

            recorder.SetBufferQueue(bufferFilesQueue, bufferSize);
            transcoder.SetBufferQueue(bufferFilesQueue, bufferSize);

            var recordingTask = Task.Run(() => recorder.StartRecording(twitchLink, sessionDirectory, _cts.Token, config.UserToken));
            var transcodingTask = Task.Run(() => transcoder.StartTranscoding(sessionDirectory, tgChannel, _cts.Token));

            if (_channelUpdHandler == null)
            {
                _channelUpdHandler = async (sender, e) =>
                {
                    _debounceCts?.Cancel();
                    _debounceCts = new CancellationTokenSource();

                    try
                    {
                        await Task.Delay(DebounceDelay, _debounceCts.Token);

                        var (newTitle, newCategory) = await GetStreamInfoAsync(api, channelId);

                        await tgChannel.UpdateStreamOnlineMsg(newTitle, newCategory, _cts.Token);
                    }
                    catch (OperationCanceledException){}
                };

                _ws.ChannelUpdate += _channelUpdHandler;
            }

            var (title, category) = await GetStreamInfoAsync(api, channelId);

            await tgChannel.SendStreamOnlineMsg(title, category, _cts.Token);

            return (recordingTask, transcodingTask);
        }

        return (null, null);
    }
    static async Task<(string title, string game)> GetStreamInfoAsync(TwitchAPI api, string channelId)
    {
        var resp = await api.Helix.Channels.GetChannelInformationAsync(broadcasterId: channelId);
        var ch = resp.Data[0];
        return (ch.Title, ch.GameName);
    }

    private static async Task<string> ResolveChannelIdAsync(string login, TwitchAPI api)
    {
        var users = await api.Helix.Users.GetUsersAsync(logins: [login]);

        if (users.Users.Length == 0)
        {
            _log!.Warning("Twitch логин не найден в Helix. Неверный логин или проблемы с подключением. Дальнейшее корректное выполнение невозможно.");
            return string.Empty;
        }

        return users.Users[0].Id;
    }
    private static async Task PendingQueueWatchDogAsync(ConcurrentQueue<Task> pendingBufferCopies)
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token);
            if (IsLive)
            {
                while (pendingBufferCopies.TryPeek(out var t) && t.IsCompleted)
                    pendingBufferCopies.TryDequeue(out _);
            }
        }
    }
}