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

namespace TwitchStreamsRecorder
{
    public class Config
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string UserToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string ChannelLogin { get; set; } = string.Empty;
        public string? OutputDir { get; set; }
    }
}