using System.Text.Json.Serialization;

namespace TwitchStreamsRecorder
{
    public class Config
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string UserToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string ChannelLogin { get; set; } = string.Empty;
        public string TelegramChannelId { get; set; } = string.Empty;
        public string TelegramBotToken { get; set; } = string.Empty;

        [JsonIgnore] public string? OutputDir { get; set; }

        public void Validate()
        {
            static string Req(string n) => $"Обязательное поле «{n}» не заполнено.";
            if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException(Req(nameof(ClientId)));
            if (string.IsNullOrWhiteSpace(ClientSecret)) throw new InvalidOperationException(Req(nameof(ClientSecret)));
            if (string.IsNullOrWhiteSpace(ChannelLogin)) throw new InvalidOperationException(Req(nameof(ChannelLogin)));
        }
    }

    [JsonSerializable(typeof(Config))]
    internal partial class ConfigJsonContext : JsonSerializerContext { }
}