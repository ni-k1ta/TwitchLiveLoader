using Serilog;
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

        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        private readonly ILogger _log;

        public TokenRefresher(Config cfg, TwitchAPI api, HttpClient http, ILogger logger)
       => (_cfg, _api, _http, ExpiresUtc, _log) = (cfg, api, http, DateTime.UtcNow, logger.ForContext("Source", "TokenRefresher"));

        public async Task RefreshAccessTokenAsync(bool persist, Action saveConfig)
        {
            await _refreshLock.WaitAsync();

            try
            {
                _log.Information("Обновление user токена...");

                var url = "https://id.twitch.tv/oauth2/token";
                var values = new[]
                {
                    new KeyValuePair<string,string>("client_id",     _cfg.ClientId),
                    new KeyValuePair<string,string>("client_secret", _cfg.ClientSecret),
                    new KeyValuePair<string,string>("grant_type",    "refresh_token"),
                    new KeyValuePair<string,string>("refresh_token", _cfg.RefreshToken)
                };
                var content = new FormUrlEncodedContent(values);

                var resp = await _http.PostAsync(url, content/*, cancellationToken*/);

                resp.EnsureSuccessStatusCode();

                var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                _cfg.UserToken = json.RootElement.GetProperty("access_token").GetString()!;
                _cfg.RefreshToken = json.RootElement.GetProperty("refresh_token").GetString()!;
                ExpiresUtc = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

                _api.Settings.AccessToken = _cfg.UserToken;

                _log.Information($"Токен обновлён -> дейтсвителен до {ExpiresUtc.ToLocalTime()}");

                if (persist) saveConfig();
            }
            catch (HttpRequestException hre)
            {
                _log.Warning(hre, "Token refresh HTTP error. Повор через 3м. Ошибка:");
                ExpiresUtc = DateTime.UtcNow.AddMinutes(3);
            }
            catch (JsonException je)
            {
                _log.Warning(je, "Token refresh parse error. Повор через 3м. Ошибка:");
                ExpiresUtc = DateTime.UtcNow.AddMinutes(3);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Token refresh FAILED. Повор через 3м. Ошибка:");
                ExpiresUtc = DateTime.UtcNow.AddMinutes(3);
            }
            finally
            {
                _refreshLock.Release();
            }
            
        }

        public async Task RefreshLoopAsync(Action saveConfig, CancellationToken cts)
        {
            _log.Debug("Token refresh loop started.");
            
            while (!cts.IsCancellationRequested)
            {
                var delay = ExpiresUtc - DateTime.UtcNow - TimeSpan.FromMinutes(2);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                try
                {
                    await Task.Delay(delay, cts);
                    await RefreshAccessTokenAsync(true, saveConfig);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.Warning(ex, $"Refresh loop error. Retrying in 1m. Ошибка:");
                    await Task.Delay(TimeSpan.FromMinutes(1), cts);
                }
            }
            _log.Debug("Token refresh loop stopped.");
        }
    }
}
