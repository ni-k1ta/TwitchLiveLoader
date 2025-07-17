using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TwitchStreamsRecorder.Network_logic
{
    internal static class HeadlineTelegramMessageBuilder
    {
        public class StreamInfo
        {
            public List<string> Titles { get; } = [];
            public List<string> Categories { get; } = [];
            public DateTime Date { get; set; }
        }
        public enum SessionStage
        {
            Live,
            LiveEnded,
            Vod1080Uploaded,
            Final
        }
        private static readonly IReadOnlyDictionary<string, string> SocialLinks = new Dictionary<string, string>
        {
            ["Twitch"] = "https://www.twitch.tv/cuuterina",
            ["TG"] = "https://t.me/cuuterina",
            ["Inst"] = "http://www.instagram.com/cuuterina",
            ["TikTok"] = "https://www.tiktok.com/@qqter1na?_t=8grZAk04CmI&_r=1",
            ["DA"] = "https://www.donationalerts.com/r/cuuterina"
        };

        public static (string Text, MessageEntity[] Entities) Build(StreamInfo info, SessionStage stage)
        {
            ArgumentNullException.ThrowIfNull(info);

            var sb = new StringBuilder();

            switch (stage)
            {
                case SessionStage.Live:
                    sb.AppendLine("✨ТЫК --> New stream is live now <-- ТЫК✨");
                    break;
                case SessionStage.LiveEnded:
                    sb.AppendLine("✨New stream✨ (Запись 1080p будет в течение +- пары часов, 720p - чуть позже в комментах)");
                    break;
                case SessionStage.Vod1080Uploaded or SessionStage.Final:
                    sb.AppendLine("✨New stream✨");
                    break;
            }

            sb.AppendLine();
            sb.AppendLine("💬 Тайтлы");
            foreach (var t in info.Titles) sb.AppendLine($"• {t}");
            sb.AppendLine();
            sb.AppendLine("🎮 Категории");
            foreach (var c in info.Categories) sb.AppendLine($"• {c}");
            sb.AppendLine();

            if (stage is SessionStage.LiveEnded or SessionStage.Vod1080Uploaded or SessionStage.Final)
            {
                sb.AppendLine("👉 Начало - will be updated ✍");
                sb.AppendLine();
                sb.AppendLine("😱 Хайлайты");
                sb.AppendLine("will be updated (мейби) ✍");
                sb.AppendLine();
                sb.AppendLine("👆[таймкоды мейби будут в описаниях к записям]👇");
                sb.AppendLine();
            }

            switch (stage)
            {
                case SessionStage.Vod1080Uploaded:
                    sb.AppendLine($"720p скоро будет в комментах ||| ({info.Date:dd.MM.yyyy})");
                    break;
                case SessionStage.Final:
                    sb.AppendLine($"720p в комментах ||| ({info.Date:dd.MM.yyyy})");
                    break;
                default:
                    sb.AppendLine($"({info.Date:dd.MM.yyyy})");
                    break;
            }

            // Соц‑сети в одну строку
            sb.AppendLine(string.Join(" ⬩ ", SocialLinks.Keys));

            var text = sb.ToString();

            // --- Entities ---------------------------------------------------
            var entities = new List<MessageEntity>();
            void Add(MessageEntityType type, string token, string? url = null)
            {
                var idx = text.IndexOf(token, StringComparison.Ordinal);
                if (idx < 0) return; // безопасность на случай, если шаблон изменён
                var e = new MessageEntity { Type = type, Offset = idx, Length = token.Length, Url = url };
                entities.Add(e);
            }

            Add(MessageEntityType.Bold, "Тайтлы");
            Add(MessageEntityType.Bold, "Категории");
            Add(MessageEntityType.Italic, $"({info.Date:dd.MM.yyyy})");

            if (stage is SessionStage.Live)
            {
                Add(MessageEntityType.Bold, "New stream is live now");
                Add(MessageEntityType.Italic, "New stream is live now");
                Add(MessageEntityType.TextLink, "New stream is live now", SocialLinks["Twitch"]);

                Add(MessageEntityType.Blockquote, $"({info.Date:dd.MM.yyyy})");
            }
            else
            {
                Add(MessageEntityType.Bold, "New stream");
                Add(MessageEntityType.Italic, "New stream");

                Add(MessageEntityType.Bold, "Хайлайты");
                Add(MessageEntityType.Blockquote, "will be updated (мейби) ✍");

                Add(MessageEntityType.Code, "[таймкоды мейби будут в описаниях к записям]");

                if (stage is SessionStage.LiveEnded)
                    Add(MessageEntityType.Blockquote, $"({info.Date:dd.MM.yyyy})");

                if (stage is SessionStage.Vod1080Uploaded)
                {
                    Add(MessageEntityType.Italic, "720p скоро будет в комментах");
                    Add(MessageEntityType.Blockquote, $"720p скоро будет в комментах ||| ({info.Date:dd.MM.yyyy})");
                }

                if (stage is SessionStage.Final)
                {
                    Add(MessageEntityType.Italic, "720p в комментах");
                    Add(MessageEntityType.Blockquote, $"720p в комментах ||| ({info.Date:dd.MM.yyyy})");
                }
            }

            // Ссылки на соцсети
            foreach (var kv in SocialLinks) Add(MessageEntityType.TextLink, kv.Key, kv.Value);

            entities.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            return (text, entities.ToArray());
        }
    }
}
