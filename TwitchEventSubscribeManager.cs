using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.EventSub.Websockets;

namespace TwitchStreamsRecorder
{
    internal class TwitchEventSubscribeManager
    {
        private readonly TwitchAPI _api;
        private readonly EventSubWebsocketClient _ws;
        private readonly Config _cfg;
        private readonly string _channelId;

        public TwitchEventSubscribeManager(TwitchAPI api, EventSubWebsocketClient ws, Config cfg, string channelId)
            => (_api, _ws, _cfg, _channelId) = (api, ws, cfg, channelId);

        public async Task SubscribeStreamStatusAsync(TokenRefresher token, ConfigService json)
        {
            //var resp = await _api.Helix.EventSub.GetEventSubSubscriptionsAsync(
            //    status: null,
            //    type: null,
            //    userId: _channelId,
            //    after: null,
            //    clientId: _cfg.ClientId,
            //    accessToken: _cfg.UserToken);

            //foreach (var sub in resp.Subscriptions)
            //{
            //    if ((sub.Type == "stream.online" || sub.Type == "stream.offline" || sub.Type == "channel.update")
            //        && sub.Condition.TryGetValue("broadcaster_user_id", out var id)
            //        && id == _channelId)
            //    {
            //        await _api.Helix.EventSub.DeleteEventSubSubscriptionAsync(
            //            id: sub.Id,
            //            clientId: _cfg.ClientId,
            //            accessToken: _cfg.UserToken);
            //    }
            //}
            string? cursor = null;
            do
            {
                var page = await _api.Helix.EventSub.GetEventSubSubscriptionsAsync(
                    after: cursor,
                    clientId: _cfg.ClientId,
                    accessToken: _cfg.UserToken);

                foreach (var sub in page.Subscriptions)
                {
                    bool isOurType = sub.Type is "stream.online" or "stream.offline" or "channel.update";
                    bool isOurChan = sub.Condition.TryGetValue("broadcaster_user_id", out var id) && id == _channelId;

                    if (isOurType && isOurChan)
                        await _api.Helix.EventSub.DeleteEventSubSubscriptionAsync(
                                sub.Id, _cfg.ClientId, _cfg.UserToken);
                }

                cursor = page.Pagination?.Cursor;
            } while (cursor is not null);

            var condition = new Dictionary<string, string> { { "broadcaster_user_id", _channelId } };

            try
            {
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("stream.online", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("stream.offline", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("channel.update", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
            }
            catch (TwitchLib.Api.Core.Exceptions.BadScopeException)
            {
                Console.WriteLine("Helix 401 ⇒ refreshing token…");
                await token.RefreshAccessTokenAsync(true, () => json.SaveConfig(_cfg, Path.Combine(AppContext.BaseDirectory, "recorder.json")));
                // retry once with fresh token
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("stream.online", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("stream.offline", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
                await _api.Helix.EventSub.CreateEventSubSubscriptionAsync("channel.update", "1", condition, EventSubTransportMethod.Websocket, _ws.SessionId, _cfg.UserToken);
            }

            Console.WriteLine("subscriptions created (online/offline)");
        }

        public async Task EnsureConnectedAsync()
        {
            while (!await _ws.ReconnectAsync())
            {
                Console.WriteLine("reconnect failed, retry 3 s…");
                await Task.Delay(3000);
            }
        }
    }
}
