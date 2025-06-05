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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using TwitchLib.Api;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs.Channel;
using TwitchStreamsRecorder;

internal class Program
{
    // ========== Twitch clients ========== //
    private static readonly EventSubWebsocketClient _ws = new();

    private static readonly HttpClient _httpClient = new(); // reuse HttpClient

    // ========== runtime ========== //
    private static volatile bool _isLive;
    private static readonly CancellationTokenSource _cts = new();

    static readonly ManualResetEventSlim _shutdownDone = new();
    static int _shutdownStarted;               // 0 → ещё нет, 1 → уже иду

    public static bool IsLive { get => _isLive; set => _isLive = value; }

    static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(8);
    static CancellationTokenSource? _debounceCts;

    private static TwitchLib.EventSub.Core.AsyncEventHandler<ChannelUpdateArgs>? _channelUpdHandler;

    // ========== ENTRY ========== //
    private static async Task Main()
    {
        Console.WriteLine("Twitch auto‑recorder starting…\n");

        var bufferFilesQueue = new BlockingCollection<string>();
        var pendingBufferCopies = new List<Task>();

        Task? recordingTask = null;
        Task? transcodingTask = null;

        var json = new ConfigService();

        var config  = json.LoadConfig(Path.Combine(AppContext.BaseDirectory, "recorder.json"));
        var twitchLink = $"twitch.tv/{config.ChannelLogin}";
        var bufferSize = 512 * 1024;
        
        var recorder = new RecorderService(pendingBufferCopies);
        var transcoder = new TranscoderService();

        var api = new TwitchAPI();

        api.Settings.ClientId = config.ClientId;
        api.Settings.AccessToken = config.UserToken;

        var token = new TokenRefresher(config, api, _httpClient);

        await token.RefreshAccessTokenAsync(true, () => json.SaveConfig(config, Path.Combine(AppContext.BaseDirectory, "recorder.json")));

        var channelId = await ResolveChannelIdAsync(config.ChannelLogin, api);
        Console.WriteLine($"channelId = {channelId} for user login = {config.ChannelLogin}\n");

        var eventSubscribe = new TwitchEventSubscribeManager(api, _ws, config, channelId);

        _ws.WebsocketConnected += async (_, __) =>
        {
            Console.WriteLine($"WebSocket connected (session {_ws.SessionId})");
            await eventSubscribe.SubscribeStreamStatusAsync(token, json);
        };
        _ws.WebsocketReconnected += async(_, __) =>
        {
            Console.WriteLine("WebSocket reconnected — re‑subscribing…");
            await eventSubscribe.SubscribeStreamStatusAsync(token, json);
        };

        _ws.WebsocketDisconnected += async (_, __) =>
        {
            Console.WriteLine("WebSocket disconnected");

            await token.RefreshAccessTokenAsync(true, () => json.SaveConfig(config, Path.Combine(AppContext.BaseDirectory, "recorder.json")));

            await eventSubscribe.EnsureConnectedAsync();
        };

        _ws.ErrorOccurred += (_, e) => { Console.WriteLine($"WebSocket error: {e.Message}"); return Task.CompletedTask; };

        _ws.StreamOnline += async (_, e) =>
        { 
            IsLive = true;

            var sessionDirectory = DirectoriesManager.CreateSessionDirectory(null, config.ChannelLogin);

            recorder.SetBufferQueue(bufferFilesQueue, bufferSize);
            transcoder.SetBufferQueue(bufferFilesQueue, bufferSize);

            recordingTask = Task.Run(() => recorder.StartRecording(twitchLink, sessionDirectory, _cts.Token));
            transcodingTask = Task.Run(() => transcoder.StartTranscoding(sessionDirectory, _cts.Token));

            var (title, category) = await GetStreamInfoAsync(api, channelId);

            await TelegramBotService.SendStreamOnlineMsg(_cts.Token, twitchLink, title, category);

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

                        await TelegramBotService.UpdateStreamOnlineMsg(newTitle, newCategory, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {

                    }
                };

                _ws.ChannelUpdate += _channelUpdHandler;
            }
        };

        _ws.StreamOffline += async (_, e) => 
        {
            IsLive = false;

            _ = recorder.ResetAsync(_cts.Token);
            _ = transcoder.ResetAsync(_cts.Token);

            bufferFilesQueue = [];
            pendingBufferCopies.Clear();

            await Task.Delay(TimeSpan.FromSeconds(10));

            _ws.ChannelUpdate -= _channelUpdHandler;

            await TelegramBotService.FinalizeStreamOnlineMsg(_cts.Token);
        };

        await _ws.ConnectAsync();

        await CheckLiveAndStartAsync(api, twitchLink, channelId, recorder, transcoder, config, recordingTask!, transcodingTask!, bufferFilesQueue, bufferSize);

        _ = Task.Run(() => token.RefreshLoopAsync(() => json.SaveConfig(config, Path.Combine(AppContext.BaseDirectory, "recorder.json")), _cts.Token));
        _ = Task.Run(() => PendingQueueWatchDogAsync(pendingBufferCopies));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown(bufferFilesQueue, pendingBufferCopies, recordingTask, transcodingTask!, recorder, transcoder);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown(bufferFilesQueue, pendingBufferCopies, recordingTask, transcodingTask!, recorder, transcoder);

        _shutdownDone.Wait();
    }
    private static void Shutdown(BlockingCollection<string> bufferFilesQueue, List<Task> pendingBufferCopies, Task? recordingTask, Task transcodingTask, RecorderService recorder, TranscoderService transcoder)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        Task.Run(async () =>
        {
            try
            {
                _cts.Cancel();                // 1) посылаем отмену

                // 2) ждём основные фоновые задачи
                await Task.WhenAll(Safe(recordingTask), Safe(transcodingTask));

                // 3) закрываем очередь, чтобы FeedBuffersAsync вышел, если ещё нет
                bufferFilesQueue.CompleteAdding();

                // 4) дожидаемся «хвостовых» копий из pendingCopies
                await Task.WhenAll([.. pendingBufferCopies]);

                // 5) мягко ждём процессы
               
                await KillProcessAsync(recorder.StreamlinkProc);
                recorder.StreamlinkProc = null;

                await KillProcessAsync(transcoder.FfmpegProc);
                transcoder.FfmpegProc = null;

                // 6) закрываем WebSocket
                await Safe(() => _ws?.DisconnectAsync()!);

                Console.WriteLine("Shutdown finished.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Shutdown error: {ex}");
            }
            finally
            {
                _shutdownDone.Set();          // разблокируем Main / ProcessExit
            }
        });
    }

    static async Task KillProcessAsync(Process? p)
    {
        if (p is null) return;

        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);     // мягкий Kill ⇒ EOF в stdin/pipe

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            p.Dispose();
        }
    }

    static Task Safe(Task? t) => t ?? Task.CompletedTask;
    static Task Safe(Func<Task> asyncCall) => asyncCall == null ? Task.CompletedTask : Safe(asyncCall());

    private static async Task CheckLiveAndStartAsync(TwitchAPI api, string twitchLink, string channelId, RecorderService recorder, TranscoderService transcoder ,Config config, Task recordingTask, Task transcodingTask, BlockingCollection<string> bufferFilesQueue, int bufferSize)
    {
        var live = await api.Helix.Streams.GetStreamsAsync(userIds: [channelId]);

        if (live.Streams.Length > 0)
        {
            IsLive = true;
            var sessionDirectory = DirectoriesManager.CreateSessionDirectory(null, config.ChannelLogin);

            recorder.SetBufferQueue(bufferFilesQueue, bufferSize);
            transcoder.SetBufferQueue(bufferFilesQueue, bufferSize);

            recordingTask = Task.Run(() => recorder.StartRecording(twitchLink, sessionDirectory, _cts.Token));
            transcodingTask = Task.Run(() => transcoder.StartTranscoding(sessionDirectory, _cts.Token));

            var (title, category) = await GetStreamInfoAsync(api, channelId);

            await TelegramBotService.SendStreamOnlineMsg(_cts.Token, twitchLink, title, category);

            _channelUpdHandler = async (sender, e) =>
            {
                _debounceCts?.Cancel();
                _debounceCts = new CancellationTokenSource();

                try
                {
                    await Task.Delay(DebounceDelay, _debounceCts.Token);

                    var (newTitle, newCategory) = await GetStreamInfoAsync(api, channelId);

                    await TelegramBotService.UpdateStreamOnlineMsg(newTitle, newCategory, _cts.Token);
                }
                catch (OperationCanceledException)
                {

                }
            };

            _ws.ChannelUpdate += _channelUpdHandler;
        }
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
            throw new Exception("login not found in Helix");
        return users.Users[0].Id;
    }
    private static async Task PendingQueueWatchDogAsync(List<Task> pendingBufferCopies)
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token);
            if (IsLive && pendingBufferCopies.Count != 0)
            {
                pendingBufferCopies.RemoveAll(t => t.IsCompleted);
            }
        }
    }
}
[JsonSerializable(typeof(Config))]
internal partial class ConfigJsonContext : JsonSerializerContext { }