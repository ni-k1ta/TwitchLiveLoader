using FFMpegCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TL;
using TwitchStreamsRecorder.Network_logic;
using TwitchStreamsRecorder.Telegram_logic;
using WTelegram;

namespace TwitchStreamsRecorder
{
    internal class TelegramChannelService : IAsyncDisposable
    {
        private readonly ThumbnailGenerator _thumbnailGenerator;

        private readonly HeadlineTelegramMessageBuilder.StreamInfo _streamInfo;
        private readonly string _tgChannelId;
        private readonly string _tgChannelChatId;
        private readonly TelegramBotClient _tgBot;
        private int _streamOnlineMsgId = -1;
        private WTelegram.Types.Message[]? _1080Msg;
        private readonly MemoryCache _map = new(new MemoryCacheOptions());

        private readonly SqliteConnection _db;
        private readonly Bot _bot;

        private readonly ILogger _log;

        private static readonly SemaphoreSlim _sendLocker = new(1, 1);

        private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _rootMsgIds = new();
        private int _maxBatch = 0;
        private int _lastPin = -1;
        private readonly UpdateManager? _manager;

        private TaskCompletionSource<int> GetTcs(int batchNo) =>_rootMsgIds.GetOrAdd(batchNo, _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));

        public TelegramChannelService(string channelId, string channeChatId, TelegramBotClient tgBot, int apiId, string apiHash, ILogger logger)
        {
            _log = logger.ForContext("Source", "TelegramChannelService");

            WTelegram.Helpers.Log = (lvl, txt) =>
            {
                if (lvl == 4 && txt.Contains("RpcError 420"))
                {
                    _log.Warning(txt);
                    return;
                }

                switch (lvl)
                {
                    //case 1: _log.Debug(txt); break;
                    case 2:
                        if (!txt.StartsWith("Got Updates_State")) _log.Information(txt);
                        else Console.WriteLine(txt);
                        break;
                    case 3: _log.Warning(txt); break;
                    case 4: _log.Error(txt); break;
                    case 5: _log.Fatal(txt); break;
                    default: /*_log.Verbose(txt);*/ break;

                }
            };

            _tgBot = tgBot;

            _tgChannelId = channelId;
            _tgChannelChatId = channeChatId;
            _streamInfo = new HeadlineTelegramMessageBuilder.StreamInfo();

            _db = new SqliteConnection("Data Source=wtbot.db");
            _db.Open();

            string? TgConfig(string key) => key switch
            {
                "bot_token" => _tgBot.Token,
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,

                "upload_threads" => "2",
                "upload_chunk" => "2097152",
                "flood_delay" => "750",
                _ => null
            };

            _bot = new Bot(TgConfig, _db);
            
            
            _bot.Client.PingInterval = 300;
            _manager = _bot.Client.WithUpdateManager(OnUpdate, "Updates.state");
            _manager.InactivityThreshold = TimeSpan.FromMinutes(60);

            _thumbnailGenerator = new(logger);
        }
        public async Task BotLoginAsync() => await _bot.Client.LoginBotIfNeeded();
        private async Task OnUpdate(TL.Update upd)
        {
            if (upd is UpdateNewChannelMessage { message: MessageService svc })
                await TryDeleteIfJoinOrLeaveAsync(svc);

            if (upd is UpdateEditChannelMessage edited)
                await TryEdit720MediaCaption(edited);
        }
        private async Task TryEdit720MediaCaption(UpdateEditChannelMessage edited)
        {
            if (_map.TryGetValue(edited.message.ID, out int chatMsgId))
            {
                var m = (TL.Message)edited.message;

                var htmlCaption = _bot.Client.EntitiesToHtml(m.message, m.entities);

                var normalized = string.IsNullOrWhiteSpace(htmlCaption) ? string.Empty : htmlCaption;

                try
                {
                    await _tgBot.EditMessageCaption
                    (
                        chatId: _tgChannelChatId,
                        messageId: chatMsgId,
                        caption: normalized == string.Empty ? null : normalized,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
                    );
                }
                catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message"))
                {
                    _log.Information($"Подпись у видео в канале по ID: {edited.message.ID} не изменилась — игнорирую (message is not modified, скорее всего просто была проставлена реакция одним из админов).");
                    return;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Не удалось добавить описание для 720p видео в чате канала по ID: {chatMsgId}. Требуется ручное вмешательство. Ошибка:");
                }

                _log.Information($"Успешное добавление описания для 720p видео в чате канала по ID: {chatMsgId}.");
            }
        }
        private async Task TryDeleteIfJoinOrLeaveAsync(MessageService svc)
        {
            if (svc.action is MessageActionChatAddUser or MessageActionChatJoinedByLink or MessageActionChatJoinedByRequest or MessageActionChatDeleteUser)
            {
                var chat = await _bot.GetChat(_tgChannelChatId);

                try
                {
                    await _bot.DeleteMessages(chat, svc.ID);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Не удалось удалить сервисное сообщение в чате канала о добавлении нового/удалении участника чата. Требуется ручное вмешательство. Ошибка: ");
                }

                _log.Information("Удаление сервисного сообщения в чате канала о добавлении нового/удалении участника чата прошло успешно.");
            }
        }
        public async Task SendStreamOnlineMsg(string title, string category, CancellationToken cts)
        {
            _streamInfo.Titles.Add(title);
            _streamInfo.Categories.Add(category);
            _streamInfo.Date = DateTime.UtcNow.AddHours(3);

            (var msgText, var entities) = HeadlineTelegramMessageBuilder.Build(_streamInfo, HeadlineTelegramMessageBuilder.SessionStage.Live);

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    var previewPath = Path.Combine(AppContext.BaseDirectory, "newStreamPreview_720p.mp4");

                    await using var streamPreview = File.OpenRead(previewPath);
                    var inputStreamPreview = new InputFileStream(streamPreview, "preview.mp4");
                    
                    var duration = await GetDurationSeconds(previewPath);

                    var msg = await _tgBot.SendVideo
                        (
                            chatId: _tgChannelId,
                            video: inputStreamPreview,
                            duration: duration,
                            width: 1280,
                            height: 720,
                            caption: msgText,
                            captionEntities: entities,
                            supportsStreaming: true,
                            cancellationToken: cts
                        );
                    _streamOnlineMsgId = msg.MessageId;

                    _log.Information("Сообщение о начале стрима опубликовано успешно.");

                    break;
                }
                catch (Exception fex) when (fex is SystemException)
                {
                    _log.Error(fex, "Не удалось открыть файл с preview для сообщения о начале стрима. Требуется ручное вмешательство. Ошибка:");

                    break;
                }
                catch (Exception ex) when (ex is ApiRequestException)
                {
                    if (i == 10)
                    {
                        _log.Error("Не удалось опубликовать сообщение о начале стрима после нескольких попыток. Требуется ручное вмешательство.");

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) опубликовать сообщение о начале стрима не увенчалась успехом. Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }
        }
        public async Task UpdateStreamOnlineMsg(string newTitle, string newCategory, CancellationToken cts)
        {
            if (_streamOnlineMsgId == -1)
                return;

            bool nt = false;
            bool nc = false;

            if (!string.IsNullOrWhiteSpace(newTitle) && !_streamInfo.Titles.Contains(newTitle))
            {
                _streamInfo.Titles.Add(newTitle);
                nt = true;
            }

            if (!string.IsNullOrWhiteSpace(newCategory) && !_streamInfo.Categories.Contains(newCategory))
            {
                _streamInfo.Categories.Add(newCategory);
                nc = true;
            }

            if (!nt && !nc)
                return;

            (var msgText, var entities) = HeadlineTelegramMessageBuilder.Build(_streamInfo, HeadlineTelegramMessageBuilder.SessionStage.Live);

            if (msgText.Length > 1024)
            {
                _log.Error($"Попытка редактирования заглавного сообщения привела к тому, что количество символов превысило 1024 => редактирование отменено. Планировалось добавление: {(nt ? "тайтла - " + newTitle + ". " : string.Empty)} {(nc ? "категории - " + newCategory + "." : string.Empty)}");
                return;
            }

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    await _tgBot.EditMessageCaption
                    (
                        chatId: _tgChannelId,
                        messageId: _streamOnlineMsgId,
                        caption: msgText,
                        captionEntities: entities,
                        cancellationToken: cts
                    );

                    _log.Information($"Редактирование сообщения о начале стрима прошло успешно. Добавлены:\n" +
                        $"{(
                            (!nt) ? string.Empty : ("Новый тайтл: " + newTitle + "\n")
                          )}" +
                        $"{(
                            (!nc) ? string.Empty : ("Новая категория: " + newCategory)
                          )}");

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, "Редактирование сообщения о начале стрима после нескольких попыток не удалось (добавление новых тайтла\\категории). Требуется ручное вмешательство. Ошибка:");

                        _streamOnlineMsgId = -1;

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) редактирования сообщения о начале стрима не увенчалась успехом. Повтор через: {5 * i}c. Ошибка: ");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }
        }
        public async Task FinalizeStreamOnlineMsg(CancellationToken cts)
        {
            if (_streamOnlineMsgId == -1)
                return;

            (var msgText, var entities) = HeadlineTelegramMessageBuilder.Build(_streamInfo, HeadlineTelegramMessageBuilder.SessionStage.LiveEnded);

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    await _tgBot.EditMessageCaption
                    (
                        chatId: _tgChannelId,
                        messageId: _streamOnlineMsgId,
                        caption: msgText,
                        captionEntities: entities,
                        cancellationToken: cts
                    );

                    _log.Information("Редактирование сообщения о начале стрима перед загрузкой записей прошло успешно.");

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, "Редактирование сообщения о начале стрима перед загрузкой записей после нескольких попыток не удалось. Требуется ручное вмешательство. Ошибка:");

                        _streamOnlineMsgId = -1;

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) редактирования сообщения о начале стрима перед загрузкой записей не увенчалась успехом. Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }
        }
        public async Task SendFinalStreamVOD(IEnumerable<string> VODsFiles, int videoWidth, int videoHeight, CancellationToken cts)
        {
            await _bot.Client.LoginBotIfNeeded();

            const int BatchSize = 10;

            var vodFiles = VODsFiles.OrderBy(Path.GetFileName).ToArray();

            for (int offset = 0; offset < vodFiles.Length; offset += BatchSize)
            {
                var media = new List<IAlbumInputMedia>();
                var openedStreams = new List<Stream>();

                _log.Information($"Подготовка перекодированных фрагментов стрима ({videoHeight}p) для загрузки в телеграм канал {_tgChannelId}...");

                try
                {
                    foreach (var file in vodFiles.Skip(offset).Take(BatchSize))
                    {
                        if (file.Contains("temp"))
                            continue;

                        var thumb = await _thumbnailGenerator.GenerateAsync(file, new ThumbnailOptions(Seek: TimeSpan.FromSeconds(2)), cts);

                        var duration = await GetDurationSeconds(file);

                        var fs = File.OpenRead(file);

                        FileStream? thumbStream = thumb != null ? File.OpenRead(thumb) : null;

                        openedStreams.Add(fs);
                        if (thumbStream != null)
                            openedStreams.Add(thumbStream);

                        media.Add(new InputMediaVideo(new InputFileStream(fs, Path.GetFileName(file)))
                        {
                            Width = videoWidth,
                            Height = videoHeight,
                            Duration = duration,
                            Thumbnail = thumbStream != null ? new InputFileStream(thumbStream, Path.GetFileName(thumb)) : null,
                            SupportsStreaming = true
                        });
                    }

                    _log.Information($"{offset + media.Count} из {vodFiles.Length} перекодированных фрагментов ({videoHeight}p) стрима готовы к загрузке.");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Не удалось подготовить список перекодированных фрагментов ({videoHeight}p) трансляции для загрузки. Требуется ручное вмешательство. Ошибка:");

                    foreach (var s in openedStreams)
                        s.Dispose();

                    _streamInfo.Titles.Clear();
                    _streamInfo.Categories.Clear();
                    _streamOnlineMsgId = -1;

                    break;
                }

                var batchNo = offset / BatchSize;
                var tcs = GetTcs(batchNo);

                try
                {
                    await _sendLocker.WaitAsync(cts);

                    _log.Information($"Начало загрузки перекодированных фрагментов ({videoHeight}p) стрима в телеграм канал {_tgChannelId}...");

                    _1080Msg = await Retry(async () => await _bot.SendMediaGroup(_tgChannelId, media), cts);

                    _log.Information($"Фрагменты перекодированной трансляции ({videoHeight}p) ({offset + media.Count} из {vodFiles.Length}) загружены успешно.");

                    _log.Information($"При отправке сообщения в канал {_tgChannelId} (в данной ситуации речь идёт о сообщении с фрагментами перекодированной трансляции в {videoHeight}p),оно автоматически отправляется в привязанный чат {_tgChannelChatId} и закрепляется в нём. Сейчас это сообщение будет найдено и откреплено, чтобы в закреплённых в чате оставались только заглавные сообщения (фрагментов записи в 720p, которые будут загружаться позже, это не касается, т.к. они загружаются в комментарии к записям в {videoHeight}p, а не в канал).");
                    
                    await Task.Delay(TimeSpan.FromSeconds(3), cts);
                    var chat = await _bot.GetChat(_tgChannelChatId);
                    var message = chat.PinnedMessage;

                    _log.Information("Попытка открепеления найденного последнего закреплённого сообщения.");
                    if (message != null)
                    {
                        int i = 0;
                        while ((_lastPin != -1 && message!.MessageId == _lastPin) || !string.IsNullOrEmpty(message!.Caption))
                        {
                            if (i == 10)
                            {
                                _log.Warning("Не удалось получить нужное закреплённое сообщение в чате канала. Требуется ручное вмешательство.");
                                throw new OperationCanceledException();
                            }

                            i++;
                            await Task.Delay(TimeSpan.FromSeconds(3), cts);
                            chat = await _bot.GetChat(_tgChannelChatId); //это здесь надо, т.к. информация не обновляется в объекте чата в реальном времени, требуется пересоздавать объект заново.
                            message = chat.PinnedMessage;
                        }

                        _lastPin = message.MessageId;
                        await _bot.PinUnpinChatMessage(_tgChannelChatId, message.MessageId, false, true);
                        _log.Information($"Сообщение с фрагментами записи в {videoHeight}p успешно откреплено в чате {_tgChannelChatId}.");

                        tcs.TrySetResult(message.MessageId);
                        _maxBatch = batchNo;
                    }
                    else
                    {
                        _log.Warning("Не было найдено закреплённое сообщение.");
                    }

                    chat = null;
                    message = null;
                }
                catch (OperationCanceledException)
                {
                    _streamInfo.Titles.Clear();
                    _streamInfo.Categories.Clear();
                    _streamOnlineMsgId = -1;
                    tcs.TrySetCanceled(cts);

                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Неожиданные проблемы при цикле загрузки в телеграм канал {_tgChannelChatId} и последующей обработки фрагментов записей в {videoHeight}p. Ошибка:");

                    break;
                }
                finally
                {
                    _sendLocker.Release();

                    foreach (var s in openedStreams)
                        s.Dispose();
                }
            }

            _lastPin = -1;

            if (_streamOnlineMsgId == -1)
                return;

            (var msgText, var entities) = HeadlineTelegramMessageBuilder.Build(_streamInfo, HeadlineTelegramMessageBuilder.SessionStage.Vod1080Uploaded);

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    await _tgBot.EditMessageCaption
                    (
                        chatId: _tgChannelId,
                        messageId: _streamOnlineMsgId,
                        caption: msgText,
                        captionEntities: entities,
                        cancellationToken: cts
                    );

                    _log.Information($"Редактирование сообщения о начале стрима прошло успешно (добавлена информация о записях в {videoHeight}p).");

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, $"Pедактирование сообщения о начале стрима после нескольких попыток не удалось (информация о записях в {videoHeight}p). Требуется ручное вмешательство. Ошибка:");

                        _streamOnlineMsgId = -1;
                        _streamInfo.Titles.Clear();
                        _streamInfo.Categories.Clear();

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) редактирования сообщения о начале стрима не увенчалась успехом (информация о записях в {videoHeight}p). Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }
        }
        public async Task SendFinalStreamVOD720(IEnumerable<string> VODsFiles, CancellationToken cts)
        {
            await _bot.Client.LoginBotIfNeeded();

            const int BatchSize = 10;

            var vodFiles = VODsFiles.OrderBy(Path.GetFileName).ToArray();

            for (int offset = 0; offset < vodFiles.Length; offset += BatchSize)
            {
                var media = new List<IAlbumInputMedia>();
                var openedStreams = new List<Stream>();

                _log.Information($"Подготовка перекодированных фрагментов стрима (720p) для загрузки в чат (в комментарии к предыдущим загруженным записям) телеграм канала {_tgChannelId}...");

                try
                {
                    foreach (var file in vodFiles.Skip(offset).Take(BatchSize))
                    {
                        var thumb = await _thumbnailGenerator.GenerateAsync(file, new ThumbnailOptions(Seek: TimeSpan.FromSeconds(2)), cts);

                        var duration = await GetDurationSeconds(file);

                        var fs = File.OpenRead(file);
                        FileStream? thumbStream = thumb != null ? File.OpenRead(thumb) : null;

                        openedStreams.Add(fs);
                        if (thumbStream != null)
                            openedStreams.Add(thumbStream);

                        media.Add(new InputMediaVideo(new InputFileStream(fs, Path.GetFileName(file)))
                        {
                            Width = 1280,
                            Height = 720,
                            Duration = duration,
                            Thumbnail = thumbStream != null ? new InputFileStream(thumbStream, Path.GetFileName(thumb)) : null,
                            SupportsStreaming = true
                        });
                    }

                    _log.Information($"{offset + media.Count} из {vodFiles.Length} перекодированных фрагментов (720p) стрима готовы к загрузке.");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Не удалось подготовить список перекодированных фрагментов (720p) трансляции для загрузки. Требуется ручное вмешательство. Ошибка:");

                    foreach (var s in openedStreams)
                        s.Dispose();

                    _streamInfo.Titles.Clear();
                    _streamInfo.Categories.Clear();
                    _streamOnlineMsgId = -1;

                    break;
                }

                try
                {
                    await _sendLocker.WaitAsync(cts);

                    _log.Information($"Начало загрузки перекодированных фрагментов (720p) стрима в чат (в комментарии к предыдущим загруженным записям) телеграм канала {_tgChannelId}...");

                    var batch = (offset / BatchSize);

                    var batchNo = batch > _maxBatch ? _maxBatch : batch;
                    var tcs = GetTcs(batchNo);

                    int replyTo;

                    try
                    {
                        replyTo = await tcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Error($"Batch {batchNo}: 1080 отменён – пропускаем 720.");
                        throw;
                    }

                    ReplyParameters replyParameters = new()
                    {
                        AllowSendingWithoutReply = true,
                        MessageId = replyTo
                    };

                    var massage720 = await Retry(async () => await _bot.SendMediaGroup
                        (
                            _tgChannelChatId,
                            media,
                            disableNotification: true,
                            replyParameters: replyParameters
                        ), cts);

                    _log.Information($"Фрагменты перекодированной трансляции (720p) ({offset + media.Count} из {vodFiles.Length}) загружены успешно.");

                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(14)
                    };
                    var captionCacheOptions = new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(14)
                    };

                    foreach (var (msg1080, msg720) in _1080Msg!.Zip(massage720))
                    {
                        _map.Set(
                            key: msg1080.MessageId,
                            value: msg720.MessageId,
                            options: cacheOptions);
                    }

                    _log.Information("В памяти сохранены связи между элементами медиа-альбома в 1080p в канале и элементами медиа-альбома в 720p в чате канала для автоматического редиктирования описаний второго при редактированиии описаний первого. Срок хранения связей 2 недели до {date}, после этого срока любое редактирование соответствующих описаний медиа-альбома в 1080p в канале не будут копироваться в описания медиа-альбома в 720p в чате канала.", DateTime.UtcNow.Add(TimeSpan.FromDays(14)).ToLocalTime());
                }
                catch (OperationCanceledException)
                {
                    _streamInfo.Titles.Clear();
                    _streamInfo.Categories.Clear();
                    _streamOnlineMsgId = -1;

                    break;
                }
                finally
                {
                    _sendLocker.Release();

                    foreach (var s in openedStreams)
                        s.Dispose();

                    _1080Msg = null;
                }
            }

            if (_streamOnlineMsgId == -1)
                return;

            (var msgText, var entities) = HeadlineTelegramMessageBuilder.Build(_streamInfo, HeadlineTelegramMessageBuilder.SessionStage.Final);

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    await _tgBot.EditMessageCaption
                    (
                        chatId: _tgChannelId,
                        messageId: _streamOnlineMsgId,
                        caption: msgText,
                        captionEntities: entities,
                        cancellationToken: cts
                    );

                    _log.Information
                        (
                        "Редактирование сообщения о начале стрима прошло успешно (добавлена информация о записях в 720p). Это финальное редактирование сообщения.\n" +
                        "================================================================================"
                        );

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, "Финальное редактирование сообщения о начале стрима после нескольких попыток не удалось (информация о записях в 720p). Требуется ручное вмешательство. Ошибка:");

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) финального редактирования сообщения о начале стрима не увенчалась успехом (информация о записях в 720p). Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }

            _rootMsgIds.Clear();
            _streamOnlineMsgId = -1;
            _streamInfo.Titles.Clear();
            _streamInfo.Categories.Clear();
        }
        public static async Task<int> GetDurationSeconds(string videoFile)
        {
            if (!File.Exists(videoFile))
                return 1;

            var mediaInfo = await FFProbe.AnalyseAsync(videoFile);

            int retTime = (int)Math.Round(mediaInfo.Duration.TotalSeconds);

            return retTime == 0 ? 1 : retTime;
        }
        private async Task<WTelegram.Types.Message[]> Retry(Func<Task<WTelegram.Types.Message[]>> action, CancellationToken ct, int max = 10)
        {
            for (int i = 1; i <= max; i++)
                try { var msg = await action(); return msg; }
                catch (RpcException ex) when (ex.Code == 420 && i < 10)
                {
                    var secs = int.Parse(ex.Message.AsSpan(11));
                    _log.Warning(ex, $"Слишком много запросов при загрузке перекодированных фрагментов трансляции, повторная попытка через {secs}с.");
                    await Task.Delay(TimeSpan.FromSeconds(secs), ct);
                }
                catch (RpcException ex) when (ex.Code == 303 && i < 10)
                {
                    _log.Warning(ex, "FILE/NETWORK_MIGRATE (DC migrate) при загрузке перекодированных фрагментов трансляции. Повтор...");
                    continue;
                }
                catch (Exception ex) when (i < max)
                {
                    _log.Warning(ex, $"Попытка ({i}) загрузки перекодированных фрагментов трансляции не увенчалась успехом. Повтор через: {5 * i}c. Ошибка:");
                    await Task.Delay(TimeSpan.FromSeconds(i * 5), ct);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Не удалось загрузить перекодированные фрагменты трансляции после нескольких попыток. Требуется ручное вмешательство. Ошибка:");
                    throw new OperationCanceledException();
                }

            throw new OperationCanceledException();
        }
        public async ValueTask DisposeAsync()
        {
            _bot.Dispose();
            await _db.DisposeAsync();
            _manager?.SaveState("Updates.state");
        }
    }
}