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
            public List<string> Titles { get; } = new();
            public List<string> Categories { get; } = new();
            public DateTime Date { get; set; }
        }

        private readonly StreamInfo _streamInfo;
        private readonly string _tgChannelId;
        private readonly TelegramBotClient _tgBot;
        private int _streamOnlineMsgId = -1;

        private readonly SqliteConnection _db;
        private readonly Bot _bot;

        private readonly ILogger _log;

        public TelegramChannelService(string channelId, TelegramBotClient tgBot, int apiId, string apiHash, ILogger logger)
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
            _streamInfo = new StreamInfo();

            _db = new SqliteConnection("Data Source=wtbot.db");
            _db.Open();

            _bot = new Bot(_tgBot.Token, apiId, apiHash, _db);
        }
        private Telegram.Bot.Types.MessageEntity[] BuildEntities(string text, bool endMsg)
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
                Add(MessageEntityType.Blockquote, $"({_streamInfo.Date:dd.MM.yyyy})");
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
            sb.AppendLine("✨New stream✨ (Запись будет в течение пары часов)");
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
            sb.AppendLine($"({_streamInfo.Date:dd.MM.yyyy})");
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

                    _log.Information("Редактирование сообщения о начале стрима с информацией о завершении стрима прошло успешно.");

                    //_streamOnlineMsgId = -1;

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, "Редактирование сообщения о начале стрима с информацией о завершении стрима после нескольких попыток не удалось. Требуется ручное вмешательство. Ошибка:");

                        _streamOnlineMsgId = -1;

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) редактирования сообщения о начале стрима с информацией о завершении стрима не увенчалась успехом. Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }
        }

        public async Task SendFinalStreamVOD(IEnumerable<string> VODsFiles, CancellationToken cts)
        {
            await _bot.Client.LoginBotIfNeeded();              // MTProto-логин (делается 1 раз)

            const int BatchSize = 10;

            var vodFiles = VODsFiles.OrderBy(Path.GetFileName).ToArray();

            for (int offset = 0; offset < vodFiles.Length; offset += BatchSize)
            {
                var media = new List<IAlbumInputMedia>();
                var openedStreams = new List<Stream>();

                _log.Information($"Подготовка перекодированных фрагментов стрима для загрузки в телеграммм канал {_tgChannelId}...");

                try
                {
                    foreach (var file in vodFiles.Skip(offset).Take(BatchSize))
                    {
                        if (file.Contains("temp"))
                            continue;

                        var thumb = Path.ChangeExtension(file, ".jpg");
                        if (!File.Exists(thumb))
                            CreateThumbnailForVideoFragment($"-ss 2 -i \"{file}\" -frames:v 1 -vf scale=320:-1 \"{thumb}\"");

                        // -- (в) получаем длительность ---------------------------------------------------
                        var duration = GetDurationSeconds(file);     // ffprobe или TagLib#

                        var fs = File.OpenRead(file);
                        var thumbStream = File.OpenRead(thumb);

                        openedStreams.Add(fs);
                        openedStreams.Add(thumbStream);

                        media.Add(new InputMediaVideo(new InputFileStream(fs, Path.GetFileName(file)))
                        {
                            Width = 1920,
                            Height = 1080,
                            Duration = duration,
                            Thumbnail = new InputFileStream(thumbStream, Path.GetFileName(thumb)),
                            SupportsStreaming = true
                        });
                    }

                    _log.Information($"{media.Count} из {vodFiles.Length} перекодированных фрагментов стрима готовы к загрузке.");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Не удалось подготовить список перекодированных фрагментов трансляции для загрузки. Требуется ручное вмешательство. Ошибка:");

                    foreach (var s in openedStreams)
                        s.Dispose();

                    _streamInfo.Titles.Clear();
                    _streamInfo.Categories.Clear();
                    _streamOnlineMsgId = -1;

                    break;
                }

                try
                {
                    _log.Information($"Начало загрузки перекодированных фрагментов стрима в телеграм канал {_tgChannelId}...");

                    await Retry(async () => await _bot.SendMediaGroup(_tgChannelId, media), cts);

                    _log.Information($"Фрагменты перекодированной трансляции ({media.Count} из {vodFiles.Length}) загружены успешно.");
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
            sb.AppendLine($"({_streamInfo.Date:dd.MM.yyyy})");
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

                    _log.Information("Финальное редактирование сообщения о начале стрима прошло успешно.");

                    _streamOnlineMsgId = -1;

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 10)
                    {
                        _log.Error(ex, "Финальное редактирование сообщения о начале стрима после нескольких попыток не удалось. Требуется ручное вмешательство. Ошибка:");

                        _streamOnlineMsgId = -1;

                        break;
                    }
                    else
                    {
                        _log.Warning(ex, $"Попытка ({i}) финального редактирования сообщения о начале стрима не увенчалась успехом. Повтор через: {5 * i}c. Ошибка:");

                        await Task.Delay(TimeSpan.FromSeconds(5 * i), cts);

                        continue;
                    }
                }
            }

            _streamInfo.Titles.Clear();
            _streamInfo.Categories.Clear();
        }
        static void CreateThumbnailForVideoFragment(string args)
        {
            var p = Process.Start(new ProcessStartInfo("ffmpeg", "-y -loglevel error " + args)
            { RedirectStandardError = true });
            p!.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("ffmpeg error: " + p.StandardError.ReadToEnd());
        }

        static int GetDurationSeconds(string mp4)
        {
            var info = TagLib.File.Create(mp4);
            return (int)info.Properties.Duration.TotalSeconds;
        }

        private async Task Retry(Func<Task> action, CancellationToken ct, int max = 10)
        {
            for (int i = 1; i <= max; i++)
                try { await action(); return; }
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
        }
        public async ValueTask DisposeAsync()
        {
            _bot.Dispose();
            await _db.DisposeAsync();
        }

        //public async Task SendFinalStreamVOD(IEnumerable<string> VODsFiles, CancellationToken cts)
        //{
        //    // 1. откроем SQLite-файл
        //    var db = new SqliteConnection("Data Source=wtbot.db");
        //    db.Open();

        //    using var bot = new Bot(_telegramBotToken, _apiId, _apiHash, db);

        //    await bot.Client.LoginBotIfNeeded();

        //    var chat = await bot.GetChat(_tgChannelId);

        //    const int BatchSize = 10;
        //    bool fatalBreak = false;

        //    // Telegram предпочитает «естественную» сортировку имён файлов
        //    var vodFiles = VODsFiles.OrderBy(Path.GetFileName).ToArray();

        //    for (int offset = 0; offset < vodFiles.Length && !fatalBreak; offset += BatchSize)
        //    {
        //        // 1. Формируем группу (≤ 10 объектов)
        //        List<IAlbumInputMedia> media;

        //        // ---------- готовим до 10 файлов ----------
        //        try
        //        {
        //            media = vodFiles.Skip(offset).Take(BatchSize)
        //                .Select(path =>
        //                {
        //                    var fs = File.OpenRead(path);
        //                    var file = new InputFileStream(fs, Path.GetFileName(path));

        //                    return (IAlbumInputMedia)new InputMediaVideo(file)
        //                    {
        //                        SupportsStreaming = true
        //                    };
        //                })
        //                .ToList();
        //        }
        //        catch (Exception ex)
        //        {
        //            _log.Error(ex, "Не удалось подготовить медиагруппу к загрузке; требуется ручное вмешательство");

        //            break;
        //        }

        //        // 2. Пытаемся отправить медиагруппу с экспоненциальным бэк-оффом
        //        for (int attempt = 1; attempt <= 10; attempt++)
        //        {
        //            try
        //            {
        //                await bot.SendMediaGroup
        //                    (
        //                        chatId: _tgChannelId,
        //                        media: media
        //                    );

        //                _log.Information($"✅ batch {(offset / BatchSize) + 1}: {media.Count} файлов загружено");
        //                break;                             // выходим из retry-цикла
        //            }
        //            catch (RpcException ex) when (attempt < 10)
        //            {
        //                var delay = TimeSpan.FromSeconds(5 * attempt);
        //                _log.Warning(ex, $"Попытка {attempt} не удалась, retry через {delay.TotalSeconds} с");
        //                await Task.Delay(delay, cts);
        //            }
        //            catch (Exception ex)
        //            {
        //                _log.Error(ex, "🥵 10 ошибок подряд — прерываю дальнейшую отправку");
        //                fatalBreak = true;
        //            }
        //        }
        //        foreach (var v in media.OfType<InputMediaVideo>())
        //            (v.Media as InputFileStream)?.Content?.Dispose();
        //    }

        //    //const int Batch = 10;
        //    //var vods = VODsFiles.OrderBy(f => f).ToList();

        //    //for (int i = 0; i < vods.Count; i += Batch)
        //    //{
        //    //    var slice = vods.Skip(i).Take(Batch).ToList();

        //    //    bool uploaded = false;
        //    //    for (int attempt = 1; attempt <= 10 && !uploaded; attempt++)
        //    //    {
        //    //        // ── 1. строим новую медиагруппу ─────────────────────────────
        //    //        var media = slice.Select(path =>
        //    //        {
        //    //            var fs = File.OpenRead(path);                       // fresh stream
        //    //            var file = new InputFileStream(fs, Path.GetFileName(path));
        //    //            return (IAlbumInputMedia)new InputMediaVideo(file)
        //    //            { SupportsStreaming = true };
        //    //        }).ToList();

        //    //        try
        //    //        {
        //    //            await _tgBot.SendMediaGroup(_tgChannelId, media, cancellationToken: cts);
        //    //            _log.Information($"Фрагменты перекодированной трансляции ({slice.Count} из {vods.Count}) загружены успешно.");
        //    //            uploaded = true;
        //    //        }
        //    //        catch (Exception ex) when (attempt < 10)
        //    //        {
        //    //            _log.Warning(ex,
        //    //                $"Попытка ({attempt}) загрузки перекодированных фрагментов трансляции ({media.Count} из {vods.Count}) не увенчалась успехом. Повтор через: {5 * attempt}c. Ошибка:");
        //    //            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cts);
        //    //        }
        //    //        catch (Exception ex)   // 10-я не удалась
        //    //        {
        //    //            _log.Error(ex, "Не удалось загрузить перекодированные фрагменты трансляции. Требуется ручное вмешательство. Ошибка:");
        //    //            return;            // или set fatalBreak=true
        //    //        }
        //    //        finally
        //    //        {
        //    //            // ── 2. закрываем все потоки независимо от успеха ───────
        //    //            foreach (var m in media.OfType<InputMediaVideo>())
        //    //                (m.Media as InputFileStream)?.Content?.Dispose();
        //    //        }
        //    //    }
        //    //}


        //    //bool fatalBreak = false;
        //    //const int Batch = 10;
        //    //var VODsList = VODsFiles.OrderBy(f => f).ToList();

        //    //for (int i = 0; i < VODsList.Count && !fatalBreak; i += Batch)
        //    //{
        //    //    List<IAlbumInputMedia>? media = [];
        //    //    try
        //    //    {
        //    //        media = VODsList.Skip(i).Take(Batch).Select((path, idx) =>
        //    //        {
        //    //            var fs = File.OpenRead(path);
        //    //            var file = new InputFileStream(fs, Path.GetFileName(path));

        //    //            return (IAlbumInputMedia)new InputMediaVideo(file)
        //    //            {
        //    //                SupportsStreaming = true
        //    //            };
        //    //        }).ToList();
        //    //    }
        //    //    catch (Exception ex)
        //    //    {
        //    //        _log.Error(ex, "Не удалось подготовить список перекодированных фрагментов трансляции для загрузки. Требуется ручное вмешательство. Ошибка:");

        //    //        foreach (var m in media.OfType<InputMediaVideo>())
        //    //            (m.Media as InputFileStream)?.Content?.Dispose();
        //    //        media = null;

        //    //        break;
        //    //    }

        //    //    for (int j = 1; j <= 10; j++)
        //    //    {
        //    //        try
        //    //        {
        //    //            await _tgBot.SendMediaGroup(_tgChannelId, media, cancellationToken: cts);

        //    //            _log.Information($"Фрагменты перекодированной трансляции ({media.Count} из {VODsList.Count}) загружены успешно.");

        //    //            break;
        //    //        }
        //    //        catch (Exception ex)
        //    //        {
        //    //            if (j == 10)
        //    //            {
        //    //                _log.Error(ex, "Не удалось загрузить перекодированные фрагменты трансляции. Требуется ручное вмешательство. Ошибка:");

        //    //                fatalBreak = true;

        //    //                break;
        //    //            }
        //    //            else
        //    //            {
        //    //                _log.Warning(ex, $"Попытка ({j}) загрузки перекодированных фрагментов трансляции ({media.Count} из {VODsList.Count}) не увенчалась успехом. Повтор через: {5 * j}c. Ошибка:");

        //    //                await Task.Delay(TimeSpan.FromSeconds(5 * j), cts);

        //    //                continue;
        //    //            }
        //    //        }
        //    //    }

        //    //    foreach (var m in media.OfType<InputMediaVideo>())
        //    //        (m.Media as InputFileStream)?.Content?.Dispose();
        //    //    media = null;
        //    //}
        //}
    }
}
