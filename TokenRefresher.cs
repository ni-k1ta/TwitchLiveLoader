using System.Text.Json;
using TwitchLib.Api;

namespace TwitchStreamsRecorder
{
    internal class TokenRefresher
    {
        private readonly Config _cfg;
        private readonly TwitchAPI _api;
        private readonly HttpClient _http;
        public DateTime ExpiresUtc { get; private set; }

        public TokenRefresher(Config cfg, TwitchAPI api, HttpClient http)
       => (_cfg, _api, _http, ExpiresUtc) = (cfg, api, http, DateTime.UtcNow);

        public async Task RefreshAccessTokenAsync(bool persist, Action saveConfig)
        {
            try
            {
                Console.WriteLine("refreshing user token…");
                var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _cfg.ClientId,
                    ["client_secret"] = _cfg.ClientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _cfg.RefreshToken
                }));
                resp.EnsureSuccessStatusCode();

                var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                _cfg.UserToken = json.RootElement.GetProperty("access_token").GetString()!;
                _cfg.RefreshToken = json.RootElement.GetProperty("refresh_token").GetString()!;
                ExpiresUtc = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

                _api.Settings.AccessToken = _cfg.UserToken;

                Console.WriteLine("token refreshed -> valid till " + ExpiresUtc.ToLocalTime());

                if (persist) saveConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine("token refresh FAILED: " + ex.Message);
                ExpiresUtc = DateTime.UtcNow.AddMinutes(1);
            }
            
        }

        public async Task RefreshLoopAsync(Action saveConfig, CancellationToken cts)
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var delay = ExpiresUtc - DateTime.UtcNow - TimeSpan.FromMinutes(2);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    await Task.Delay(delay, cts);
                    await RefreshAccessTokenAsync(persist: true, saveConfig);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Refresh loop error: {ex}");
            }
        }
    }
}
