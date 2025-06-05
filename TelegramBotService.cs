using Telegram.Bot;
using Telegram.Bot.Types;
using TwitchLib.Api.Helix.Models.Schedule;

namespace TwitchStreamsRecorder
{
    internal static class TelegramBotService
    {
        private static readonly string _tgChannelId = "@test_twitchVODs_loader_bot";
        private static readonly string _botToken = "8031941569:AAFR2oGV3p7BPwkKy5xa5tNRVE-NTmFhmoI";
        private static readonly TelegramBotClient _tgBot = new(_botToken);
        private static int _streamOnlineMsgId;
        private static string? _streamOnlineMsgText;

        public static async Task SendStreamOnlineMsg(CancellationToken cts, string twitchLink, string title, string category)
        {
            var msgText = $"✨ТЫК --> New stream is live now <-- ТЫК✨\n\n" +
                $"💬 Тайтлы\n" +
                $"• {title}\n\n" +
                $"🎮 Категории\n" +
                $"• {category}\n\n" +
                $"({DateTime.Today.ToString("dd.MM.yyyy")})\n" +
                $"Twitch | TG | Inst | TikTok | DA";

            //var msgText = $"✨ТЫК --> New stream is live now <-- ТЫК✨\n\n" +
            //    $"💬 Тайтлы\n" +
            //    $"• {title}\n\n" +
            //    $"🎮 Категории\n" +
            //    $"• {category}\n\n" +
            //    $"👉 Начало - will be updated ✍\n\n" +
            //    $"😱 Хайлайты\n" +
            //    $"will be updated ✍\n\n" +
            //    $"👆[таймкоды в описаниях к записям]👇 - will be updated ✍\n\n" +
            //    $"({DateTime.Today.ToString("dd.MM.yyyy")})\n" +
            //    $"Twitch | TG | Inst | TikTok | DA";

            //var customEmojiID = "5404760560086556644";

            var entities = new[]
            {
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = msgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = msgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length,
                    Url = "https://www.twitch.tv/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = msgText.IndexOf("Тайтлы"),
                    Length = "Тайтлы".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = msgText.IndexOf("Категории"),
                    Length = "Категории".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = msgText.IndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Blockquote,
                    Offset = msgText.IndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("Twitch"),
                    Length = "Twitch".Length,
                    Url = "https://www.twitch.tv/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("TG"),
                    Length = "TG".Length,
                    Url = "https://t.me/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("Inst"),
                    Length = "Inst".Length,
                    Url = "http://www.instagram.com/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("TikTok"),
                    Length = "TikTok".Length,
                    Url = "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = msgText.IndexOf("DA"),
                    Length = "DA".Length,
                    Url = "https://www.donationalerts.com/r/cuuterina"
                }
            };
            //new MessageEntity
            //{
            //    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
            //    Offset = msgText.IndexOf("Хайлайты"),
            //    Length = "Хайлайты".Length
            //},
            //new MessageEntity
            //{
            //    Type = Telegram.Bot.Types.Enums.MessageEntityType.Code,
            //    Offset = msgText.IndexOf("[таймкоды в описаниях к записям]"),
            //    Length = "[таймкоды в описаниях к записям]".Length
            //},

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
            _streamOnlineMsgText = msgText;
        }

        public static async Task UpdateStreamOnlineMsg(string newTitle, string newCategory, CancellationToken cts)
        {
            var titles = _streamOnlineMsgText!
                [
                (_streamOnlineMsgText!.IndexOf('•') - 1)..(_streamOnlineMsgText.IndexOf("Категории") - 5)
                ];
            var categories = _streamOnlineMsgText
                [
                (_streamOnlineMsgText.IndexOf("Категории") + "Категории".Length + 1) .. (_streamOnlineMsgText.IndexOf('(') - 2)
                ];

            if ((titles.Contains(newTitle) || newTitle == null) && (categories.Contains(newCategory) || newCategory == null))
                return;

            if (newTitle != null && !titles.Contains(newTitle))
            {
                var titleIndex = _streamOnlineMsgText.IndexOf("Категории") - 5;
                var newInsertTitle = $"\n• {newTitle}";

                _streamOnlineMsgText = _streamOnlineMsgText.Insert(titleIndex, newInsertTitle);
            }

            if (newCategory != null && !categories.Contains(newCategory))
            {
                var categoryIndex = _streamOnlineMsgText!.IndexOf('(') - 2;
                var newInsertCategory = $"\n• {newCategory}";

                _streamOnlineMsgText = _streamOnlineMsgText.Insert(categoryIndex, newInsertCategory);
            }

            var entities = new[]
            {
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = _streamOnlineMsgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("New stream is live now"),
                    Length = "New stream is live now".Length,
                    Url = "https://www.twitch.tv/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("Тайтлы"),
                    Length = "Тайтлы".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("Категории"),
                    Length = "Категории".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = _streamOnlineMsgText.IndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Blockquote,
                    Offset = _streamOnlineMsgText.IndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("Twitch"),
                    Length = "Twitch".Length,
                    Url = "https://www.twitch.tv/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("TG"),
                    Length = "TG".Length,
                    Url = "https://t.me/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("Inst"),
                    Length = "Inst".Length,
                    Url = "http://www.instagram.com/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("TikTok"),
                    Length = "TikTok".Length,
                    Url = "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("DA"),
                    Length = "DA".Length,
                    Url = "https://www.donationalerts.com/r/cuuterina"
                }
            };

            await _tgBot.EditMessageCaption
                (
                chatId: _tgChannelId,
                messageId: _streamOnlineMsgId,
                caption: _streamOnlineMsgText,
                captionEntities: entities
                );
        }
        public static async Task FinalizeStreamOnlineMsg(CancellationToken cts)
        {
            var concatMsg = _streamOnlineMsgText!["✨ТЫК --> New stream is live now <-- ТЫК✨\n\n".Length..];
            var titlesAndCategories = concatMsg[..(concatMsg.IndexOf('(') - 1)];
            var additionalMsg = $"\n👉 Начало - will be updated (мейби) ✍\n\n" +
                $"😱 Хайлайты\n" +
                $"will be updated (мейби) ✍\n\n" +
                $"👆[таймкоды мейби будут в описаниях к записям]👇\n\n";
            var endMsg = concatMsg[concatMsg.IndexOf('(')..(concatMsg.IndexOf("DA") + "DA".Length)];
            _streamOnlineMsgText = "✨New stream✨\n\n" + titlesAndCategories + additionalMsg + endMsg;

            var entities = new[]
           {
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("New stream"),
                    Length = "New stream".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = _streamOnlineMsgText.IndexOf("New stream"),
                    Length = "New stream".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("Тайтлы"),
                    Length = "Тайтлы".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("Категории"),
                    Length = "Категории".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Bold,
                    Offset = _streamOnlineMsgText.IndexOf("Хайлайты"),
                    Length = "Хайлайты".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Blockquote,
                    Offset = _streamOnlineMsgText.LastIndexOf("will be updated (мейби) ✍"),
                    Length = "will be updated (мейби) ✍".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Code,
                    Offset = _streamOnlineMsgText.IndexOf("[таймкоды мейби будут в описаниях к записям]"),
                    Length = "[таймкоды мейби будут в описаниях к записям]".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Italic,
                    Offset = _streamOnlineMsgText.LastIndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.Blockquote,
                    Offset = _streamOnlineMsgText.LastIndexOf('('),
                    Length = $"({DateTime.Today.ToString("dd.MM.yyyy")})".Length
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("Twitch"),
                    Length = "Twitch".Length,
                    Url = "https://www.twitch.tv/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("TG"),
                    Length = "TG".Length,
                    Url = "https://t.me/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("Inst"),
                    Length = "Inst".Length,
                    Url = "http://www.instagram.com/cuuterina"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("TikTok"),
                    Length = "TikTok".Length,
                    Url = "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1"
                },
                new MessageEntity
                {
                    Type = Telegram.Bot.Types.Enums.MessageEntityType.TextLink,
                    Offset = _streamOnlineMsgText.IndexOf("DA"),
                    Length = "DA".Length,
                    Url = "https://www.donationalerts.com/r/cuuterina"
                }
            };
            await _tgBot.EditMessageCaption
                (
                chatId: _tgChannelId,
                messageId: _streamOnlineMsgId,
                caption: _streamOnlineMsgText,
                captionEntities: entities
                );
        }

        public static async Task SendFinalStreamVOD(IEnumerable<string> VODsFiles, CancellationToken cts)
        {
            const int Batch = 10;
            var VODsList = VODsFiles.OrderBy(f => f).ToList();

            for (int i = 0; i < VODsList.Count; i += Batch)
            {
                var media = VODsList.Skip(i).Take(Batch).Select((path, idx) =>
                {
                    var fs = File.OpenRead(path);
                    var file = new InputFileStream(fs, Path.GetFileName(path));

                    return (IAlbumInputMedia)new InputMediaVideo(file)
                    {
                        SupportsStreaming = true
                    };
                }).ToList();

                await _tgBot.SendMediaGroup(_tgChannelId, media, cancellationToken: cts);

                foreach(var m in media.OfType<InputMediaVideo>())
                    (m.Media as InputFileStream)?.Content?.Dispose();
            }
        }
    }
}
