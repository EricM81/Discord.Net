﻿#pragma warning disable CS1591
using Discord.API.Rpc;
using Discord.Net.Queue;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.Rpc;
using Discord.Serialization;
using Discord.Serialization.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Formatting;
using System.Threading;
using System.Threading.Tasks;

namespace Discord.API
{
    internal class DiscordRpcApiClient : DiscordRestApiClient, IDisposable
    {
        private interface IRpcRequest
        {
            Task SetResultAsync(ReadOnlyBuffer<byte> data);
            Task SetExceptionAsync(ReadOnlyBuffer<byte> data);
        }

        private class RpcRequest<T> : IRpcRequest
            where T : class, new()
        {
            public TaskCompletionSource<T> Promise { get; set; }

            public RpcRequest(RequestOptions options)
            {
                Promise = new TaskCompletionSource<T>();
                Task.Run(async () =>
                {
                    await Task.Delay(options?.Timeout ?? 15000).ConfigureAwait(false);
                    Promise.TrySetCanceled(); //Doesn't need to be async, we're already in a separate task
                });
            }
            public Task SetResultAsync(ReadOnlyBuffer<byte> data)
            {
                return Promise.TrySetResultAsync(DiscordRestJsonSerializer.Global.Read<T>(data));
            }
            public Task SetExceptionAsync(ReadOnlyBuffer<byte> data)
            {
                var error = DiscordRestJsonSerializer.Global.Read<ErrorEvent>(data);
                return Promise.TrySetExceptionAsync(new RpcException(error.Code, error.Message));
            }
        }

        private object _eventLock = new object();
        
        public event Func<string, Task> SentRpcMessage { add { _sentRpcMessageEvent.Add(value); } remove { _sentRpcMessageEvent.Remove(value); } }
        private readonly AsyncEvent<Func<string, Task>> _sentRpcMessageEvent = new AsyncEvent<Func<string, Task>>();

        public event Func<string, Optional<string>, Optional<ReadOnlyBuffer<byte>>, Task> ReceivedRpcEvent { add { _receivedRpcEvent.Add(value); } remove { _receivedRpcEvent.Remove(value); } }
        private readonly AsyncEvent<Func<string, Optional<string>, Optional<ReadOnlyBuffer<byte>>, Task>> _receivedRpcEvent = new AsyncEvent<Func<string, Optional<string>, Optional<ReadOnlyBuffer<byte>>, Task>>();
        public event Func<Exception, Task> Disconnected { add { _disconnectedEvent.Add(value); } remove { _disconnectedEvent.Remove(value); } }
        private readonly AsyncEvent<Func<Exception, Task>> _disconnectedEvent = new AsyncEvent<Func<Exception, Task>>();

        private readonly string _clientId;
        private readonly ConcurrentDictionary<Guid, IRpcRequest> _requests;
        private readonly IWebSocketClient _webSocketClient;
        private readonly SemaphoreSlim _connectionLock;
        private readonly MemoryStream _decompressionStream;
        private readonly StreamReader _decompressionReader;
        private CancellationTokenSource _stateCancelToken;
        private string _origin;

        public ConnectionState ConnectionState { get; private set; }

        public DiscordRpcApiClient(string clientId, string userAgent, string origin, RestClientProvider restClientProvider, WebSocketProvider webSocketProvider,
                Serializer serializer, RetryMode defaultRetryMode = RetryMode.AlwaysRetry)
            : base(restClientProvider, userAgent, serializer, defaultRetryMode)
        {
            _connectionLock = new SemaphoreSlim(1, 1);
            _clientId = clientId;
            _origin = origin;
            
            _requests = new ConcurrentDictionary<Guid, IRpcRequest>();

            _decompressionStream = new MemoryStream(10 * 1024); //10 KB 
            _decompressionReader = new StreamReader(_decompressionStream);

            _webSocketClient = webSocketProvider();
            //_webSocketClient.SetHeader("user-agent", DiscordConfig.UserAgent); (Causes issues in .Net 4.6+)
            _webSocketClient.SetHeader("origin", _origin);
            _webSocketClient.Message += async (data, isText) =>
            {
                if (!isText)
                {
                    using (var compressed = new MemoryStream(data.ToArray(), 2, data.Length - 2))
                    {
                        _decompressionStream.Position = 0;
                        using (var zlib = new DeflateStream(compressed, CompressionMode.Decompress))
                            zlib.CopyTo(_decompressionStream);
                        _decompressionStream.SetLength(_decompressionStream.Position);

                        _decompressionStream.Position = 0;
                        var msg = _serializer.Read<RpcFrame>(_decompressionStream.ToReadOnlyBuffer());
                        if (msg != null)
                        {
                            await _receivedRpcEvent.InvokeAsync(msg.Cmd, msg.Event, msg.Data).ConfigureAwait(false);
                            if (msg.Nonce.IsSpecified && msg.Nonce.Value.HasValue)
                                ProcessMessage(msg);
                        }
                    }
                }
                else
                {
                    var msg = _serializer.Read<RpcFrame>(data);
                    if (msg != null)
                    {
                        await _receivedRpcEvent.InvokeAsync(msg.Cmd, msg.Event, msg.Data).ConfigureAwait(false);
                        if (msg.Nonce.IsSpecified && msg.Nonce.Value.HasValue)
                            ProcessMessage(msg);
                    }
                }
            };
            _webSocketClient.Closed += async ex =>
            {
                await DisconnectAsync().ConfigureAwait(false);
                await _disconnectedEvent.InvokeAsync(ex).ConfigureAwait(false);
            };
        }
        internal override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _stateCancelToken?.Dispose();
                    (_webSocketClient as IDisposable)?.Dispose();
                }
                _isDisposed = true;
            }
        }

        public async Task ConnectAsync()
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ConnectInternalAsync().ConfigureAwait(false);
            }
            finally { _connectionLock.Release(); }
        }
        internal override async Task ConnectInternalAsync()
        {
            /*if (LoginState != LoginState.LoggedIn)
                throw new InvalidOperationException("Client is not logged in.");*/

            ConnectionState = ConnectionState.Connecting;
            try
            {
                _stateCancelToken = new CancellationTokenSource();
                if (_webSocketClient != null)
                    _webSocketClient.SetCancelToken(_stateCancelToken.Token);

                bool success = false;
                int port;
                string uuid = Guid.NewGuid().ToString();

                for ( port = DiscordRpcConfig.PortRangeStart; port <= DiscordRpcConfig.PortRangeEnd; port++)
                {
                    try
                    {
                        string url = $"wss://{uuid}.discordapp.io:{port}/?v={DiscordRpcConfig.RpcAPIVersion}&client_id={_clientId}";
                        await _webSocketClient.ConnectAsync(url).ConfigureAwait(false);
                        success = true;
                        break;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (!success)
                    throw new Exception("Unable to connect to the RPC server.");
                
                SetBaseUrl($"https://{uuid}.discordapp.io:{port}/");
                ConnectionState = ConnectionState.Connected;
            }
            catch (Exception)
            {
                await DisconnectInternalAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisconnectInternalAsync().ConfigureAwait(false);
            }
            finally { _connectionLock.Release(); }
        }
        internal override async Task DisconnectInternalAsync()
        {
            if (_webSocketClient == null)
                throw new NotSupportedException("This client is not configured with websocket support.");

            if (ConnectionState == ConnectionState.Disconnected) return;
            ConnectionState = ConnectionState.Disconnecting;
            
            try { _stateCancelToken?.Cancel(false); }
            catch { }

            await _webSocketClient.DisconnectAsync().ConfigureAwait(false);

            ConnectionState = ConnectionState.Disconnected;
        }

        //Core
        public async Task<TResponse> SendRpcAsync<TResponse>(string cmd, object payload, Optional<string> evt = default, RequestOptions options = null)
            where TResponse : class, new()
        {
            return await SendRpcAsyncInternal<TResponse>(cmd, payload, evt, options).ConfigureAwait(false);
        }
        private async Task<TResponse> SendRpcAsyncInternal<TResponse>(string cmd, object payload, Optional<string> evt, RequestOptions options)
            where TResponse : class, new()
        {
            var data = _formatters.Rent();
            try
            {
                var guid = Guid.NewGuid();
                var frame = new API.Rpc.RpcFrame { Cmd = cmd, Event = evt, Args = payload, Nonce = guid };

                var requestTracker = new RpcRequest<TResponse>(options);
                _requests[guid] = requestTracker;

                await RequestQueue.SendAsync(new WebSocketRequest(_webSocketClient, null, SerializeJson(data, frame), true, options)).ConfigureAwait(false);
                await _sentRpcMessageEvent.InvokeAsync(cmd).ConfigureAwait(false);
                return await requestTracker.Promise.Task.ConfigureAwait(false);
            }
            finally
            {
                data.Clear();
                _formatters.Return(data);
            }
        }

        //Rpc
        public async Task<AuthenticateResponse> SendAuthenticateAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new AuthenticateParams
            {
                AccessToken = AuthToken
            };
            options.IgnoreState = true;
            return await SendRpcAsync<AuthenticateResponse>("AUTHENTICATE", msg, options: options).ConfigureAwait(false);
        }
        public async Task<AuthorizeResponse> SendAuthorizeAsync(IReadOnlyCollection<string> scopes, string rpcToken = null, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new AuthorizeParams
            {
                ClientId = _clientId,
                Scopes = scopes,
                RpcToken = rpcToken ?? Optional.Create<string>()
            };
            if (options.Timeout == null)
                options.Timeout = 60000; //This requires manual input on the user's end, lets give them more time
            options.IgnoreState = true;
            return await SendRpcAsync<AuthorizeResponse>("AUTHORIZE", msg, options: options).ConfigureAwait(false);
        }

        public async Task<GetGuildsResponse> SendGetGuildsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendRpcAsync<GetGuildsResponse>("GET_GUILDS", null, options: options).ConfigureAwait(false);
        }
        public async Task<Rpc.Guild> SendGetGuildAsync(ulong guildId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new GetGuildParams
            {
                GuildId = guildId
            };
            return await SendRpcAsync<Rpc.Guild>("GET_GUILD", msg, options: options).ConfigureAwait(false);
        }
        public async Task<GetChannelsResponse> SendGetChannelsAsync(ulong guildId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new GetChannelsParams
            {
                GuildId = guildId
            };
            return await SendRpcAsync<GetChannelsResponse>("GET_CHANNELS", msg, options: options).ConfigureAwait(false);
        }
        public async Task<Rpc.Channel> SendGetChannelAsync(ulong channelId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new GetChannelParams
            {
                ChannelId = channelId
            };
            return await SendRpcAsync<Rpc.Channel>("GET_CHANNEL", msg, options: options).ConfigureAwait(false);
        }
        
        public async Task<Rpc.Channel> SendSelectTextChannelAsync(ulong channelId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new SelectChannelParams
            {
                ChannelId = channelId
            };
            return await SendRpcAsync<Rpc.Channel>("SELECT_TEXT_CHANNEL", msg, options: options).ConfigureAwait(false);
        }
        public async Task<Rpc.Channel> SendSelectVoiceChannelAsync(ulong channelId, bool force = false, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new SelectChannelParams
            {
                ChannelId = channelId,
                Force = force
            };
            return await SendRpcAsync<Rpc.Channel>("SELECT_VOICE_CHANNEL", msg, options: options).ConfigureAwait(false);
        }

        public async Task<SubscriptionResponse> SendGlobalSubscribeAsync(string evt, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendRpcAsync<SubscriptionResponse>("SUBSCRIBE", null, evt: evt, options: options).ConfigureAwait(false);
        }
        public async Task<SubscriptionResponse> SendGlobalUnsubscribeAsync(string evt, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendRpcAsync<SubscriptionResponse>("UNSUBSCRIBE", null, evt: evt, options: options).ConfigureAwait(false);
        }

        public async Task<SubscriptionResponse> SendGuildSubscribeAsync(string evt, ulong guildId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new GuildSubscriptionParams
            {
                GuildId = guildId
            };
            return await SendRpcAsync<SubscriptionResponse>("SUBSCRIBE", msg, evt: evt, options: options).ConfigureAwait(false);
        }
        public async Task<SubscriptionResponse> SendGuildUnsubscribeAsync(string evt, ulong guildId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new GuildSubscriptionParams
            {
                GuildId = guildId
            };
            return await SendRpcAsync<SubscriptionResponse>("UNSUBSCRIBE", msg, evt: evt, options: options).ConfigureAwait(false);
        }

        public async Task<SubscriptionResponse> SendChannelSubscribeAsync(string evt, ulong channelId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new ChannelSubscriptionParams
            {
                ChannelId = channelId
            };
            return await SendRpcAsync<SubscriptionResponse>("SUBSCRIBE", msg, evt: evt, options: options).ConfigureAwait(false);
        }
        public async Task<SubscriptionResponse> SendChannelUnsubscribeAsync(string evt, ulong channelId, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            var msg = new ChannelSubscriptionParams
            {
                ChannelId = channelId
            };
            return await SendRpcAsync<SubscriptionResponse>("UNSUBSCRIBE", msg, evt: evt, options: options).ConfigureAwait(false);
        }

        public async Task<API.Rpc.VoiceSettings> GetVoiceSettingsAsync(RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            return await SendRpcAsync<API.Rpc.VoiceSettings>("GET_VOICE_SETTINGS", null, options: options).ConfigureAwait(false);
        }
        public async Task SetVoiceSettingsAsync(API.Rpc.VoiceSettings settings, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            await SendRpcAsync<API.Rpc.VoiceSettings>("SET_VOICE_SETTINGS", settings, options: options).ConfigureAwait(false);
        }
        public async Task SetUserVoiceSettingsAsync(ulong userId, API.Rpc.UserVoiceSettings settings, RequestOptions options = null)
        {
            options = RequestOptions.CreateOrClone(options);
            settings.UserId = userId;
            await SendRpcAsync<API.Rpc.UserVoiceSettings>("SET_USER_VOICE_SETTINGS", settings, options: options).ConfigureAwait(false);
        }

        private bool ProcessMessage(API.Rpc.RpcFrame msg)
        {
            if (_requests.TryGetValue(msg.Nonce.Value.Value, out IRpcRequest requestTracker))
            {
                if (msg.Event.GetValueOrDefault("") == "ERROR")
                {
                    var _ = requestTracker.SetExceptionAsync(msg.Data.GetValueOrDefault());
                }
                else
                {
                    var _ = requestTracker.SetResultAsync(msg.Data.GetValueOrDefault());
                }
                return true;
            }
            else
                return false;
        }
    }
}
