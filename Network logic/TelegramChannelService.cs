using Microsoft.Data.Sqlite;
using Serilog;
using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TL;
using WTelegram;

namespace TwitchStreamsRecorder
{
    internal class TelegramChannelService : IAsyncDisposable
    {
        private class StreamInfo
        {
            public List<string> Titles { get; } = [];
            public List<string> Categories { get; } = [];
            public DateTime Date { get; set; }
        }

        private readonly StreamInfo _streamInfo;
        private readonly string _tgChannelId;
        private readonly string _tgChannelChatId;
        private readonly TelegramBotClient _tgBot;
        private int _streamOnlineMsgId = -1;

        private readonly SqliteConnection _db;
        private readonly Bot _bot;

        private readonly ILogger _log;

        private static readonly SemaphoreSlim _sendLocker = new(1, 1);
        private int _1080msgId = -1;
        private int _720msgId = -1;

        public TelegramChannelService(string channelId, string channeChatlId, TelegramBotClient tgBot, int apiId, string apiHash, ILogger logger)
        {
            _log = logger.ForContext("Source", "TelegramChannelService");

            WTelegram.Helpers.Log = (lvl, txt) =>
            {
                switch (lvl)
                {
                    //case 1: _log.Debug(txt); break;
                    case 2: _log.Information(txt); break;
                    case 3: _log.Warning(txt); break;
                    case 4: _log.Error(txt); break;
                    case 5: _log.Fatal(txt); break;
                    default: /*_log.Verbose(txt);*/ break;

                }
            };

            _tgBot = tgBot;

            _tgChannelId = channelId;
            _tgChannelChatId = channeChatlId;
            _streamInfo = new StreamInfo();

            _db = new SqliteConnection("Data Source=wtbot.db");
            _db.Open();

            _bot = new Bot(_tgBot.Token, apiId, apiHash, _db);
        }
        private Telegram.Bot.Types.MessageEntity[] BuildEntities(string text, bool endMsg, int msg1080Id = -1, int msg720Id = -1)
        {
            var list = new List<Telegram.Bot.Types.MessageEntity>();

            void Add(MessageEntityType type, string token, string? url = null)
            {
                int index = text.IndexOf(token);
                if (index < 0) return;
                var entity = new Telegram.Bot.Types.MessageEntity
                {
                    Type = type,
                    Offset = index,
                    Length = token.Length
                };
                if (url != null) entity.Url = url;
                list.Add(entity);
            }

            if (endMsg)
            {
                Add(MessageEntityType.Bold, "New stream");
                Add(MessageEntityType.Italic, "New stream");
                Add(MessageEntityType.Bold, "Тайтлы");
                Add(MessageEntityType.Bold, "Категории");
                Add(MessageEntityType.Bold, "Хайлайты");
                Add(MessageEntityType.Blockquote, "will be updated (мейби) ✍");
                Add(MessageEntityType.Code, "[таймкоды мейби будут в описаниях к записям]");
                Add(MessageEntityType.Italic, $"({_streamInfo.Date:dd.MM.yyyy})");
                Add(MessageEntityType.Blockquote, $"1080p\\/720p ({_streamInfo.Date:dd.MM.yyyy})");
                if (msg1080Id != -1)
                    Add(MessageEntityType.TextLink, "1080p", $"https://t.me/cuuterina_vods/{msg1080Id}");
                if (msg720Id != -1)
                    Add(MessageEntityType.TextLink, "720p", $"https://t.me/cuuterina_vods_chat/{msg720Id}");
                Add(MessageEntityType.TextLink, "Twitch", "https://www.twitch.tv/cuuterina");
                Add(MessageEntityType.TextLink, "TG", "https://t.me/cuuterina");
                Add(MessageEntityType.TextLink, "Inst", "http://www.instagram.com/cuuterina");
                Add(MessageEntityType.TextLink, "TikTok", "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1");
                Add(MessageEntityType.TextLink, "DA", "https://www.donationalerts.com/r/cuuterina");
            }
            else
            {
                Add(MessageEntityType.Bold, "New stream is live now");
                Add(MessageEntityType.Italic, "New stream is live now");
                Add(MessageEntityType.TextLink, "New stream is live now", "https://www.twitch.tv/cuuterina");
                Add(MessageEntityType.Bold, "Тайтлы");
                Add(MessageEntityType.Bold, "Категории");
                Add(MessageEntityType.Italic, $"({_streamInfo.Date:dd.MM.yyyy})");
                Add(MessageEntityType.Blockquote, $"({_streamInfo.Date:dd.MM.yyyy})");
                Add(MessageEntityType.TextLink, "Twitch", "https://www.twitch.tv/cuuterina");
                Add(MessageEntityType.TextLink, "TG", "https://t.me/cuuterina");
                Add(MessageEntityType.TextLink, "Inst", "http://www.instagram.com/cuuterina");
                Add(MessageEntityType.TextLink, "TikTok", "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1");
                Add(MessageEntityType.TextLink, "DA", "https://www.donationalerts.com/r/cuuterina");
            }

            return list.ToArray();
        }

        public async Task SendStreamOnlineMsg(string title, string category, CancellationToken cts)
        {
            _streamInfo.Titles.Add(title);
            _streamInfo.Categories.Add(category);
            _streamInfo.Date = DateTime.Today;

            var sb = new StringBuilder();
            sb.AppendLine("✨ТЫК --> New stream is live now <-- ТЫК✨");
            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            sb.AppendLine($"• {_streamInfo.Titles.First()}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            sb.AppendLine($"• {_streamInfo.Categories.First()}");
            sb.AppendLine();
            sb.AppendLine($"({_streamInfo.Date:dd.MM.yyyy})");
            sb.AppendLine("Twitch ⬩ TG ⬩ Inst ⬩ TikTok ⬩ DA");
            var msgText = sb.ToString();

            var entities = BuildEntities(msgText, false);

            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    await using var streamPreview = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "newStreamPreview_720p.mp4"));

                    var inputStreamPreview = new InputFileStream(streamPreview, "preview.mp4");


                    var msg = await _tgBot.SendVideo
                        (
                            chatId: _tgChannelId,
                            video: inputStreamPreview,
                            duration: 12,
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

            if (!_streamInfo.Titles.Contains(newTitle) && newTitle != null)
            {
                _streamInfo.Titles.Add(newTitle);
                nt = true;
            }

            if (!_streamInfo.Categories.Contains(newCategory) && newCategory != null)
            {
                _streamInfo.Categories.Add(newCategory);
                nt = true;
            }

            if (!nt && !nc)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("✨ТЫК --> New stream is live now <-- ТЫК✨");
            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            foreach (var t in _streamInfo.Titles) sb.AppendLine($"• {t}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            foreach (var c in _streamInfo.Categories) sb.AppendLine($"• {c}");
            sb.AppendLine();
            sb.AppendLine($"({_streamInfo.Date:dd.MM.yyyy})");
            sb.AppendLine("Twitch ⬩ TG ⬩ Inst ⬩ TikTok ⬩ DA");
            var msgText = sb.ToString();

            var entities = BuildEntities(msgText, false);

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

            var sb = new StringBuilder();
            sb.AppendLine("✨New stream✨ (Запись 1080p будет в течение +- пары часов, 720p - чуть позже)");
            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            foreach (var t in _streamInfo.Titles) sb.AppendLine($"• {t}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            foreach (var c in _streamInfo.Categories) sb.AppendLine($"• {c}");
            sb.AppendLine();
            sb.AppendLine("👉 Начало - will be updated ✍");
            sb.AppendLine();
            sb.AppendLine("😱 Хайлайты");
            sb.AppendLine("will be updated (мейби) ✍");
            sb.AppendLine();
            sb.AppendLine("👆[таймкоды мейби будут в описаниях к записям]👇");
            sb.AppendLine();
            sb.AppendLine($"1080p\\/720p ({_streamInfo.Date:dd.MM.yyyy})");
            sb.AppendLine("Twitch ⬩ TG ⬩ Inst ⬩ TikTok ⬩ DA");
            var msgText = sb.ToString();

            var entities = BuildEntities(msgText, true);

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

                    //_streamOnlineMsgId = -1;

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

            bool firstPack = true;

            int qscale = 1;

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

                        var thumb = Path.ChangeExtension(file, ".jpg");
                        if (!File.Exists(thumb))
                        {
                            CreateThumbnailForVideoFragment($"-ss 2 -i \"{file}\" -frames:v 1 -vf \"scale=320:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0\" -c:v mjpeg -qscale:v {qscale} -update 1 -map_metadata -1 \"{thumb}\"");

                            var thumbInfo = new FileInfo(thumb);

                            while (thumbInfo.Length >= (200 * 1024))
                            {
                                qscale++;
                                CreateThumbnailForVideoFragment($"-ss 2 -i \"{file}\" -frames:v 1 -vf \"scale=320:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0\" -c:v mjpeg -qscale:v {qscale} -update 1 -map_metadata -1 \"{thumb}\"");
                                thumbInfo.Refresh();
                            }
                        }

                        // -- (в) получаем длительность ---------------------------------------------------
                        var duration = GetDurationSeconds(file);     // ffprobe или TagLib#

                        var fs = File.OpenRead(file);
                        var thumbStream = File.OpenRead(thumb);

                        openedStreams.Add(fs);
                        openedStreams.Add(thumbStream);

                        media.Add(new InputMediaVideo(new InputFileStream(fs, Path.GetFileName(file)))
                        {
                            Width = videoWidth,
                            Height = videoHeight,
                            Duration = duration,
                            Thumbnail = new InputFileStream(thumbStream, Path.GetFileName(thumb)),
                            SupportsStreaming = true
                        });
                    }

                    _log.Information($"{media.Count} из {vodFiles.Length} перекодированных фрагментов ({videoHeight}p) стрима готовы к загрузке.");
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

                try
                {
                    await _sendLocker.WaitAsync(cts);

                    _log.Information($"Начало загрузки перекодированных фрагментов ({videoHeight}p) стрима в телеграм канал {_tgChannelId}...");

                    var msg1080 = await Retry(async () => await _bot.SendMediaGroup(_tgChannelId, media), cts);

                    _log.Information($"Фрагменты перекодированной трансляции ({videoHeight}p) ({media.Count} из {vodFiles.Length}) загружены успешно.");

                    var first1080 = msg1080.FirstOrDefault();
                    if (first1080 != null)
                    {
                        if (firstPack)
                        {
                            firstPack = false;
                            _1080msgId = first1080.MessageId;
                        }

                        _log.Information($"При отправке сообщения в канал {_tgChannelId} (в данной ситуации речь идёт о сообщении с фрагментами перекодированной трансляции в {videoHeight}p), оно автоматически отправляется в привязанный чат {_tgChannelChatId} и закрепляется в нём. Сейчас это сообщение будет найдено и откреплено, чтобы в закреплённых в чате оставались только заглавные сообщения (фрагментов записи в 720p, которые будут загружаться позже, это не касается, т.к. они загружаются сразу в чат, а не в канал).");
                        var chat = await _bot.GetChat(_tgChannelChatId);
                        await Task.Delay(TimeSpan.FromSeconds(3), cts);
                        var message = chat.PinnedMessage;

                        _log.Information("Попытка открепеления найденного последнего закреплённого сообщения.");
                        if (message != null)
                        {
                            while (!string.IsNullOrEmpty(message!.Text))
                            {
                                await Task.Delay(TimeSpan.FromSeconds(3), cts);
                                message = chat.PinnedMessage;
                            }

                            await _bot.PinUnpinChatMessage(_tgChannelChatId, message.MessageId, false, true);
                            _log.Information($"Сообщение с фрагментами записи в {videoHeight}p успешно откреплено в чате {_tgChannelChatId}.");
                        }
                        else
                        {
                            _log.Warning("Не было найдено закреплённое сообщение.");
                        }

                        chat = null;
                        message = null;
                    }
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
                }
            }

            if (_streamOnlineMsgId == -1)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("✨New stream✨");
            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            foreach (var t in _streamInfo.Titles) sb.AppendLine($"• {t}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            foreach (var c in _streamInfo.Categories) sb.AppendLine($"• {c}");
            sb.AppendLine();
            sb.AppendLine("👉 Начало - will be updated ✍");
            sb.AppendLine();
            sb.AppendLine("😱 Хайлайты");
            sb.AppendLine("will be updated (мейби) ✍");
            sb.AppendLine();
            sb.AppendLine("👆[таймкоды мейби будут в описаниях к записям]👇");
            sb.AppendLine();
            sb.AppendLine($"1080p\\/720p ({_streamInfo.Date:dd.MM.yyyy})");
            sb.AppendLine("Twitch ⬩ TG ⬩ Inst ⬩ TikTok ⬩ DA");
            var msgText = sb.ToString();

            var entities = BuildEntities(msgText, true, _1080msgId);

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

                    //_streamOnlineMsgId = -1;

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

            bool firstPack = true;

            int qscale = 1;

            for (int offset = 0; offset < vodFiles.Length; offset += BatchSize)
            {
                var media = new List<IAlbumInputMedia>();
                var openedStreams = new List<Stream>();

                _log.Information($"Подготовка перекодированных фрагментов стрима (720p) для загрузки в чат телеграм канала {_tgChannelId}...");

                try
                {
                    foreach (var file in vodFiles.Skip(offset).Take(BatchSize))
                    {
                        var thumb = Path.ChangeExtension(file, ".jpg");
                        if (!File.Exists(thumb))
                        {
                            CreateThumbnailForVideoFragment($"-ss 2 -i \"{file}\" -frames:v 1 -vf \"scale=320:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0\" -c:v mjpeg -qscale:v {qscale} -update 1 -map_metadata -1 \"{thumb}\"");

                            var thumbInfo = new FileInfo(thumb);

                            while (thumbInfo.Length >= (200 * 1024))
                            {
                                qscale++;
                                CreateThumbnailForVideoFragment($"-ss 2 -i \"{file}\" -frames:v 1 -vf \"scale=320:-1:flags=lanczos,format=yuv444p,unsharp=5:5:1.0:5:5:0.0\" -c:v mjpeg -qscale:v {qscale} -update 1 -map_metadata -1 \"{thumb}\"");
                                thumbInfo.Refresh();
                            }
                        }

                        var duration = GetDurationSeconds(file);

                        var fs = File.OpenRead(file);
                        var thumbStream = File.OpenRead(thumb);

                        openedStreams.Add(fs);
                        openedStreams.Add(thumbStream);

                        media.Add(new InputMediaVideo(new InputFileStream(fs, Path.GetFileName(file)))
                        {
                            Width = 1280,
                            Height = 720,
                            Duration = duration,
                            Thumbnail = new InputFileStream(thumbStream, Path.GetFileName(thumb)),
                            SupportsStreaming = true
                        });
                    }

                    _log.Information($"{media.Count} из {vodFiles.Length} перекодированных фрагментов (720p) стрима готовы к загрузке.");
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

                    _log.Information($"Начало загрузки перекодированных фрагментов (720p) стрима в чат телеграм канала {_tgChannelId}...");

                    var msg720 = await Retry(async () => await _bot.SendMediaGroup
                        (
                            _tgChannelChatId,
                            media,
                            disableNotification: true
                        ), cts);

                    if (firstPack)
                    {
                        firstPack = false;
                        var first720 = msg720.FirstOrDefault();

                        if (first720 != null)
                            _720msgId = first720.MessageId;
                    }

                    _log.Information($"Фрагменты перекодированной трансляции (720p) ({media.Count} из {vodFiles.Length}) загружены успешно.");
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
                }
            }

            if (_streamOnlineMsgId == -1)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("✨New stream✨");
            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            foreach (var t in _streamInfo.Titles) sb.AppendLine($"• {t}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            foreach (var c in _streamInfo.Categories) sb.AppendLine($"• {c}");
            sb.AppendLine();
            sb.AppendLine("👉 Начало - will be updated ✍");
            sb.AppendLine();
            sb.AppendLine("😱 Хайлайты");
            sb.AppendLine("will be updated (мейби) ✍");
            sb.AppendLine();
            sb.AppendLine("👆[таймкоды мейби будут в описаниях к записям]👇");
            sb.AppendLine();
            sb.AppendLine($"1080p\\/720p ({_streamInfo.Date:dd.MM.yyyy})");
            sb.AppendLine("Twitch ⬩ TG ⬩ Inst ⬩ TikTok ⬩ DA");
            var msgText = sb.ToString();

            var entities = BuildEntities(msgText, true, _1080msgId, _720msgId);

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

            _1080msgId = -1;
            _720msgId = -1;
            _streamOnlineMsgId = -1;
            _streamInfo.Titles.Clear();
            _streamInfo.Categories.Clear();
        }
        private static void CreateThumbnailForVideoFragment(string args)
        {
            var p = Process.Start(new ProcessStartInfo("ffmpeg", "-y -loglevel error " + args)
            { RedirectStandardError = true });
            p!.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("ffmpeg error: " + p.StandardError.ReadToEnd());
        }

        private static int GetDurationSeconds(string mp4)
        {
            using var info = TagLib.File.Create(mp4);
            return (int)info.Properties.Duration.TotalSeconds;
        }

        private async Task<WTelegram.Types.Message[]> Retry(Func<Task<WTelegram.Types.Message[]>> action, CancellationToken ct, int max = 10)
        {
            for (int i = 1; i <= max; i++)
                try { var msg = await action(); return msg; }
                catch (RpcException ex) when (ex.Code == 420 && i < 10)   // FLOOD_WAIT
                {
                    // "FLOOD_WAIT_37" → 37
                    var secs = int.Parse(ex.Message.AsSpan(11));          // 11 = "FLOOD_WAIT_".Length
                    _log.Warning(ex, $"Слишком много запросов при загрузке перекодированных фрагментов трансляции, повторная попытка через {secs}с.");
                    await Task.Delay(TimeSpan.FromSeconds(secs), ct);
                }
                catch (RpcException ex) when (ex.Code == 303 && i < 10)   // FILE/NETWORK_MIGRATE
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
        }
    }
}