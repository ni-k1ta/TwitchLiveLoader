using Serilog;
using Serilog.Core;
using Serilog.Events;
using Telegram.Bot;

namespace TwitchStreamsRecorder
{
    internal static class Logging
    {
        public static readonly LoggingLevelSwitch Level = new(LogEventLevel.Verbose);
        public const string Template = "{Level:u3}: |{Source}| [{Timestamp:dd-MM-yyyy HH:mm:ss}] ({Channel}) {Message:lj}{NewLine}{Exception}";
        public static ILogger InitRoot(TelegramBotClient bot, long tgChatId)
        {
            return new LoggerConfiguration()
                .MinimumLevel.ControlledBy(Level)
                .Enrich.WithProperty("Channel", "-")         
                .Enrich.WithProperty("Source", "Main")       
                .Enrich.FromLogContext()                     
                .WriteTo.Console(outputTemplate: Template)

                .WriteTo.Sink(new TelegramSink(bot, tgChatId),
                              restrictedToMinimumLevel: LogEventLevel.Error)

                .CreateLogger();
        }
    }
}
