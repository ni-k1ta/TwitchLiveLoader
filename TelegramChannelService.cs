using Serilog;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TwitchStreamsRecorder
{
    internal class TelegramChannelService
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

        private readonly ILogger _log;

        public TelegramChannelService(string channelId, TelegramBotClient tgBot, ILogger logger)
        {
            _tgChannelId = channelId;
            _streamInfo = new StreamInfo();
            _tgBot = tgBot;
            _log = logger.ForContext("Source", "TelegramChannelService");
        }
        private MessageEntity[] BuildEntities(string text, bool endMsg)
        {
            var list = new List<MessageEntity>();

            void Add(MessageEntityType type, string token, string? url = null)
            {
                int index = text.IndexOf(token);
                if (index < 0) return;
                var entity = new MessageEntity
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
                    await using var streamPreview = File.OpenRead("C:\\Users\\sosit\\Videos\\newStreamPreview_720p.mp4");

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

            bool needToUpdate = false;

            if (!_streamInfo.Titles.Contains(newTitle) && newTitle != null)
            {
                _streamInfo.Titles.Add(newTitle);
                needToUpdate = true;
            }

            if (!_streamInfo.Categories.Contains(newCategory) && newCategory != null)
            {
                _streamInfo.Categories.Add(newCategory);
                needToUpdate = true;
            }

            if (!needToUpdate)
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
                            (newTitle is null) ? string.Empty : ("Новый тайтл: " + newTitle + "\n")
                          )}" +
                        $"{(
                            (newCategory is null) ? string.Empty : ("Новая категория: " + newCategory)
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

        public async Task SendFinalStreamVOD(IEnumerable<string> VODsFiles, CancellationToken cts)
        {
            bool fatalBreak = false;
            const int Batch = 10;
            var VODsList = VODsFiles.OrderBy(f => f).ToList();

            for (int i = 0; i < VODsList.Count && !fatalBreak; i += Batch)
            {
                List<IAlbumInputMedia>? media = [];
                try
                {
                    media = VODsList.Skip(i).Take(Batch).Select((path, idx) =>
                    {
                        var fs = File.OpenRead(path);
                        var file = new InputFileStream(fs, Path.GetFileName(path));

                        return (IAlbumInputMedia)new InputMediaVideo(file)
                        {
                            SupportsStreaming = true
                        };
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Не удалось подготовить список перекодированных фрагментов трансляции для загрузки. Требуется ручное вмешательство. Ошибка:");

                    foreach (var m in media.OfType<InputMediaVideo>())
                        (m.Media as InputFileStream)?.Content?.Dispose();
                    media = null;

                    break;
                }

                for (int j = 1; j <= 10; j++)
                {
                    try
                    {
                        await _tgBot.SendMediaGroup(_tgChannelId, media, cancellationToken: cts);

                        _log.Information($"Фрагменты перекодированной трансляции ({media.Count} из {VODsList.Count}) загружены успешно.");

                        break;
                    }
                    catch (Exception ex)
                    {
                        if (j == 10)
                        {
                            _log.Error(ex, "Не удалось загрузить перекодированные фрагменты трансляции. Требуется ручное вмешательство. Ошибка:");

                            fatalBreak = true;

                            break;
                        }
                        else
                        {
                            _log.Warning(ex, $"Попытка ({j}) загрузки перекодированных фрагментов трансляции ({media.Count} из {VODsList.Count}) не увенчалась успехом. Повтор через: {5 * j}c. Ошибка:");

                            await Task.Delay(TimeSpan.FromSeconds(5 * j), cts);

                            continue;
                        }
                    }
                }

                foreach (var m in media.OfType<InputMediaVideo>())
                    (m.Media as InputFileStream)?.Content?.Dispose();
                media = null;
            }
        }
    }
}
