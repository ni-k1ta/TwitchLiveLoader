using Serilog;
using System.Net;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Core.Exceptions;
using TwitchLib.EventSub.Websockets;

namespace TwitchStreamsRecorder
{
    internal class TwitchEventSubscribeManager
    {

        private readonly TwitchAPI _api;
        private readonly EventSubWebsocketClient _ws;
        private readonly Config _cfg;
        private readonly string _channelId;
        private readonly ILogger _log;

        private int _isRunning;

        private static readonly (string Type, string Version)[] _events =
        {
            ("stream.online",  "1"),
            ("stream.offline", "1"),
            ("channel.update", "2")
        };


        public TwitchEventSubscribeManager(TwitchAPI api, EventSubWebsocketClient ws, Config cfg, string channelId, ILogger logger)
            => (_api, _ws, _cfg, _channelId, _log) = (api, ws, cfg, channelId, logger.ForContext("Source", "TwitchEventSubscriber"));

        public async Task SubscribeStreamStatusAsync(TokenRefresher token, ConfigService json)
        {
            if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            {
                _log.Debug("SubscribeStreamStatusAsync: уже в процессе — пропускаем новый вызов.");
                return;
            }

            await DeleteAllSubscriptionsAsync(token, json);

            var condition = new Dictionary<string, string> { { "broadcaster_user_id", _channelId } };

            await CreateAllSubscriptionsAsync(condition, token, json);

            _log.Information($"All subscriptions (online/offline/channel.update) created successfully.");

            Interlocked.Exchange(ref _isRunning, 0);
        }
        private async Task<T> CallWithTokenRetryAsync<T>(Func<Task<T>> action, TokenRefresher token, ConfigService cfgSvc)
        {
            int delayMs = 2000;

            int maxDelayMs = 30_000;

            int i = 0;
            while (true) 
            {
                try
                {
                    i++;
                    return await action();
                }
                catch (BadScopeException ex)
                {
                    if (i % 10 == 0)
                    {
                        _log.Error(ex, "Длительное время не получается выполнить обработку подписок. Вероятно проблемы на стороне сервера или проблемы с интерентом. Возможно требуется ручное вмешательство. Ошибка:");

                        maxDelayMs = Math.Min(maxDelayMs * 2, 300_000);
                    }
                    else
                    {
                        _log.Warning(ex, $"BadScopeException 401 ⇒ попытка ({i}) обновления токена не увенчалась успехом. Повтор через {delayMs}с. Ошибка:");
                    }

                    delayMs = (int)Math.Min(Math.Round(delayMs * 1.5, 0), maxDelayMs);

                    await Task.Delay(delayMs);
                    await token.RefreshAccessTokenAsync(true, () => cfgSvc.SaveConfig(_cfg, ConfigService.GetDefaultConfigPath()));
                }
                catch (TooManyRequestsException ex)
                {
                    if (i % 10 == 0)
                    {
                        _log.Error(ex, "Длительное время наблюдается исключение TooManyRequestsException. Вероятно наличие проблемы на стороне сервера. Возможно требуется ручное вмешательство. Ошибка:");
                    }

                    var resetUtc = DateTimeOffset.FromUnixTimeSeconds(
                        long.Parse(ex.Data["Ratelimit-Reset"]!.ToString()!));
                    var delay = resetUtc - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);

                    _log.Warning(ex, $"429 ⇒ Слишком много запросов, попытка ({i}) повтор через {delay.TotalSeconds:F0}с.");
                    await Task.Delay(delay);
                }
                catch (InternalServerErrorException ex)
                {
                    if (i % 10 == 0)
                    {
                        _log.Error(ex, "Длительное время не получается выполнить обработку подписок. Вероятно проблемы на стороне сервера или проблемы с интерентом. Возможно требуется ручное вмешательство. Ошибка:");

                        maxDelayMs = Math.Min(maxDelayMs * 2, 300_000);
                    }
                    else
                    {
                        _log.Warning(ex, $"Внутренняя ошибка сервера. Попытка ({i}) запроса не увенчалась успехом. Повтор через {delayMs}с. Ошибка:");
                    }

                    delayMs = (int)Math.Min(Math.Round(delayMs * 1.5, 0), maxDelayMs);

                    await Task.Delay(delayMs);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadGateway)
                {
                    if (i % 10 == 0)
                    {
                        _log.Error(ex, "Длительное время не получается выполнить обработку подписок. Вероятно проблемы на стороне сервера или проблемы с интерентом. Возможно требуется ручное вмешательство. Ошибка:");

                        maxDelayMs = Math.Min(maxDelayMs * 2, 300_000);
                    }
                    else
                    {
                        _log.Warning(ex, $"Ошибка сети. Попытка ({i}) запроса не увенчалась успехом. Повтор через {delayMs}с. Ошибка:");
                    }

                    delayMs = (int)Math.Min(Math.Round(delayMs * 1.5, 0), maxDelayMs);

                    await Task.Delay(delayMs);
                }
                catch (Exception ex)
                {
                    if (i % 10 == 0)
                    {
                        _log.Error(ex, "Длительное время не получается выполнить обработку подписок. Вероятно проблемы на стороне сервера или проблемы с интерентом. Возможно требуется ручное вмешательство. Ошибка:");

                        maxDelayMs = Math.Min(maxDelayMs * 2, 300_000);
                    }
                    else
                    {
                        _log.Warning(ex, $"Неожиданное исключение. Попытка ({i}) запроса не увенчалась успехом. Повтор через {delayMs}с. Ошибка:");
                    }
                }
            }
        }

        private async Task CreateAllSubscriptionsAsync(Dictionary<string, string> condition, TokenRefresher token, ConfigService json)
        {
            _log.Information("Создание подписок...");
            foreach (var (type, version) in _events)
            {
                await CallWithTokenRetryAsync(() =>_api.Helix.EventSub.CreateEventSubSubscriptionAsync(
                    type, version, condition,
                    EventSubTransportMethod.Websocket,
                    _ws.SessionId,
                    accessToken: _cfg.UserToken), token, json);

                _log.Information($"Created subscription for {type}");
            }
        }

        public async Task DeleteAllSubscriptionsAsync(TokenRefresher token, ConfigService json)
        {
            string? cursor = null;
            do
            {
                _log.Information("Запрос существующих подписок...");
                var page = await CallWithTokenRetryAsync(
                    () => _api.Helix.EventSub.GetEventSubSubscriptionsAsync(
                    after: cursor,
                    clientId: _cfg.ClientId,
                    accessToken: _cfg.UserToken),
                    token, json);

                var toDelete = page.Subscriptions.Where
                    (
                        s => (s.Type == "stream.online" || s.Type == "stream.offline" || s.Type == "channel.update") && s.Condition.TryGetValue("broadcaster_user_id", out var id) && id == _channelId
                    ).Select
                    (
                        s => (s.Id, s.Type)
                    ).ToList();

                if (toDelete.Count > 0)
                    _log.Information("Удаление подписок...");

                foreach (var (subId, subType) in toDelete)
                {
                    await CallWithTokenRetryAsync(
                    () => _api.Helix.EventSub.DeleteEventSubSubscriptionAsync(
                    subId,
                    _cfg.ClientId,
                    _cfg.UserToken),
                    token, json);

                    _log.Information($"Deleted subscription {subId} (type: {subType})");
                }

                cursor = page.Pagination?.Cursor;
            } while (cursor is not null);
        }

        public async Task EnsureConnectedAsync()
        {
            int delayMs = 3000;

            int maxDelayMs = 30_000;

            int i = 1;

            while (!await _ws.ReconnectAsync())
            {
                i++;

                if (i % 10 == 0)
                {
                    _log.Error("Длительное время не получается восстановить соединение. Вероятны проблемы на стороне сервера или проблемы с интернетом. Возможно требуется ручное вмешательство.");

                    maxDelayMs = Math.Min(maxDelayMs*2, 300_000);
                }
                else
                {
                    _log.Warning($"Попытка {(i)} восстановить соединение не увенчалась успехом. Повтор через {delayMs / 1000.0}с.");
                }

                await Task.Delay(delayMs);

                delayMs = (int)Math.Min(Math.Round(delayMs * 1.5, 0), maxDelayMs);
            }

            _log.Information("Соединение восстановлено.");
        }
    }
}
