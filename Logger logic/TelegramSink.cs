using Serilog.Core;
using Serilog.Events;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TwitchStreamsRecorder
{
    internal sealed class TelegramSink : ILogEventSink
    {
        private readonly TelegramBotClient _bot;
        private readonly long _chatId;

        public TelegramSink(TelegramBotClient bot, long chatId)
            => (_bot, _chatId) = (bot, chatId);

        public void Emit(LogEvent le)
        {
            var msg = le.RenderMessage();
            if (le.Exception != null)
                msg += "\n```" + le.Exception + "```";

            _ = _bot.SendMessage(_chatId, "🔥 " + msg, ParseMode.Markdown);
        }
    }
}
