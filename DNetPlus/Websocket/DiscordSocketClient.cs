using Discord.API;
using Discord.API.Gateway;
using Discord.Logging;
using Discord.Net.Converters;
using Discord.Net.Udp;
using Discord.Net.WebSockets;
using Discord.Rest;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameModel = Discord.API.GameJson;

namespace Discord.WebSocket
{
    /// <summary>
    ///     Represents a WebSocket-based Discord client.
    /// </summary>
    public partial class DiscordSocketClient : BaseSocketClient, IDiscordClient
    {
        private readonly ConcurrentQueue<ulong> _largeGuilds;
        private readonly JsonSerializer _serializer;
        private readonly DiscordShardedClient _shardedClient;
        private readonly DiscordSocketClient _parentClient;
        private readonly ConcurrentQueue<long> _heartbeatTimes;
        private readonly ConnectionManager _connection;
        private readonly Logger _gatewayLogger;
        private readonly SemaphoreSlim _stateLock;

        private string _sessionId;
        private int _lastSeq;
        private ImmutableDictionary<string, RestVoiceRegion> _voiceRegions;
        private Task _heartbeatTask, _guildDownloadTask;
        private int _unavailableGuildCount;
        private long _lastGuildAvailableTime, _lastMessageTime;
        private int _nextAudioId;
        private DateTimeOffset? _statusSince;
        private RestApplication _applicationInfo;
        private bool _isDisposed;
        private GatewayIntents _gatewayIntents;

        /// <summary>
        ///     Provides access to a REST-only client with a shared state from this client.
        /// </summary>
        public override DiscordSocketRestClient Rest { get; }
        /// <summary> Gets the shard of of this client. </summary>
        public int ShardId { get; }
        /// <summary> Gets the current connection state of this client. </summary>
        public ConnectionState ConnectionState => _connection.State;
        /// <inheritdoc />
        public override int Latency { get; protected set; }
        /// <inheritdoc />
        public override UserStatus Status { get; protected set; } = UserStatus.Online;
        /// <inheritdoc />
        public override IActivity Activity { get; protected set; }

        //From DiscordSocketConfig
        internal int TotalShards { get; private set; }
        internal int MessageCacheSize { get; private set; }
        internal int LargeThreshold { get; private set; }
        internal ClientState State { get; private set; }
        internal UdpSocketProvider UdpSocketProvider { get; private set; }
        internal WebSocketProvider WebSocketProvider { get; private set; }
        internal bool AlwaysDownloadUsers { get; private set; }
        internal int? HandlerTimeout { get; private set; }
        internal bool DownloadedVoice { get; private set; }

        internal new DiscordSocketApiClient ApiClient => base.ApiClient as DiscordSocketApiClient;
        /// <inheritdoc />
        public override IReadOnlyCollection<SocketGuild> Guilds => State.Guilds;
        /// <inheritdoc />
        public override IReadOnlyCollection<ISocketPrivateChannel> PrivateChannels => State.PrivateChannels;
        /// <summary>
        ///     Gets a collection of direct message channels opened in this session.
        /// </summary>
        /// <remarks>
        ///     This method returns a collection of currently opened direct message channels.
        ///     <note type="warning">
        ///         This method will not return previously opened DM channels outside of the current session! If you
        ///         have just started the client, this may return an empty collection.
        ///     </note>
        /// </remarks>
        /// <returns>
        ///     A collection of DM channels that have been opened in this session.
        /// </returns>
        public IReadOnlyCollection<SocketDMChannel> DMChannels
            => State.PrivateChannels.OfType<SocketDMChannel>().ToImmutableArray();
        /// <summary>
        ///     Gets a collection of group channels opened in this session.
        /// </summary>
        /// <remarks>
        ///     This method returns a collection of currently opened group channels.
        ///     <note type="warning">
        ///         This method will not return previously opened group channels outside of the current session! If you
        ///         have just started the client, this may return an empty collection.
        ///     </note>
        /// </remarks>
        /// <returns>
        ///     A collection of group channels that have been opened in this session.
        /// </returns>
        public IReadOnlyCollection<SocketGroupChannel> GroupChannels
            => State.PrivateChannels.OfType<SocketGroupChannel>().ToImmutableArray();

        /// <summary>
        ///     Initializes a new REST/WebSocket-based Discord client.
        /// </summary>
        public DiscordSocketClient() : this(new DiscordSocketConfig()) { }
        /// <summary>
        ///     Initializes a new REST/WebSocket-based Discord client with the provided configuration.
        /// </summary>
        /// <param name="config">The configuration to be used with the client.</param>
#pragma warning disable IDISP004
        public DiscordSocketClient(DiscordSocketConfig config) : this(config, CreateApiClient(config), null, null) { }
        internal DiscordSocketClient(DiscordSocketConfig config, DiscordShardedClient shardedClient, DiscordSocketClient parentClient) : this(config, CreateApiClient(config), shardedClient, parentClient) { }
#pragma warning restore IDISP004
        private DiscordSocketClient(DiscordSocketConfig config, API.DiscordSocketApiClient client, DiscordShardedClient shardedClient, DiscordSocketClient parentClient)
            : base(config, client)
        {
            if (config.Debug == null)
                config.Debug = new DiscordDebugConfig();
            if (config.Debug.Events == null)
                config.Debug.Events = new DiscordDebugEvents();

            ShardId = config.ShardId ?? 0;
            TotalShards = config.TotalShards ?? 1;
            MessageCacheSize = config.MessageCacheSize;
            LargeThreshold = config.LargeThreshold;
            UdpSocketProvider = config.UdpSocketProvider;
            WebSocketProvider = config.WebSocketProvider;
            AlwaysDownloadUsers = config.AlwaysDownloadUsers;
            HandlerTimeout = config.HandlerTimeout;
            State = new ClientState(0, 0);
            base._shardedClient = shardedClient;

            Rest = new DiscordSocketRestClient(config, ApiClient, shardedClient);
            _heartbeatTimes = new ConcurrentQueue<long>();
            _gatewayIntents = config.GatewayIntents;

            _stateLock = new SemaphoreSlim(1, 1);
            _gatewayLogger = LogManager.CreateLogger(ShardId == 0 && TotalShards == 1 ? "Gateway" : $"Shard #{ShardId}");
            _connection = new ConnectionManager(_stateLock, _gatewayLogger, config.ConnectionTimeout,
                OnConnectingAsync, OnDisconnectingAsync, x => ApiClient.Disconnected += x);
            _connection.Connected += () => TimedInvokeAsync(_connectedEvent, nameof(Connected));
            _connection.Disconnected += (ex, recon) => TimedInvokeAsync(_disconnectedEvent, nameof(Disconnected), ex);

            _nextAudioId = 1;
            _shardedClient = shardedClient;
            _parentClient = parentClient;

            _serializer = new JsonSerializer { ContractResolver = new DiscordContractResolver() };
            _serializer.Error += (s, e) =>
            {
                _gatewayLogger.WarningAsync("Serializer Error", e.ErrorContext.Error).GetAwaiter().GetResult();
                e.ErrorContext.Handled = true;
            };

            ApiClient.SentGatewayMessage += async opCode => await _gatewayLogger.DebugAsync($"Sent {opCode}").ConfigureAwait(false);
            ApiClient.ReceivedGatewayEvent += ProcessMessageAsync;

            LeftGuild += async g => await _gatewayLogger.InfoAsync($"Left {g.Name}").ConfigureAwait(false);
            JoinedGuild += async g => await _gatewayLogger.InfoAsync($"Joined {g.Name}").ConfigureAwait(false);
            GuildAvailable += async g => await _gatewayLogger.VerboseAsync($"Connected to {g.Name}").ConfigureAwait(false);
            GuildUnavailable += async g => await _gatewayLogger.VerboseAsync($"Disconnected from {g.Name}").ConfigureAwait(false);
            LatencyUpdated += async (old, val) => await _gatewayLogger.DebugAsync($"Latency = {val} ms").ConfigureAwait(false);

            GuildAvailable += g =>
            {
                if (_guildDownloadTask?.IsCompleted == true && ConnectionState == ConnectionState.Connected && AlwaysDownloadUsers && !g.HasAllMembers)
                {
                    Task _ = g.DownloadUsersAsync();
                }
                return Task.Delay(0);
            };

            _largeGuilds = new ConcurrentQueue<ulong>();
        }
        private static API.DiscordSocketApiClient CreateApiClient(DiscordSocketConfig config)
            => new API.DiscordSocketApiClient(config.RestClientProvider, config.WebSocketProvider, DiscordRestConfig.UserAgent);

        /// <inheritdoc />
        internal override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    StopAsync().GetAwaiter().GetResult();
                    ApiClient?.Dispose();
                    _stateLock?.Dispose();
                }
                _isDisposed = true;
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        internal override async Task OnLoginAsync(TokenType tokenType, string token)
        {
            await Rest.OnLoginAsync(tokenType, token);
        }
        /// <inheritdoc />
        internal override async Task OnLogoutAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _applicationInfo = null;
            _voiceRegions = null;
            await Rest.OnLogoutAsync();
        }

        /// <inheritdoc />
        public override async Task StartAsync()
            => await _connection.StartAsync().ConfigureAwait(false);

        public async Task StartAsync(UserStatus status, string name = null, string streamUrl = null, ActivityType type = ActivityType.Playing)
        {
            Status = status;
            if (status == UserStatus.AFK)
                _statusSince = DateTimeOffset.UtcNow;
            else
                _statusSince = null;
            if (name != null)
            {
                if (!string.IsNullOrEmpty(streamUrl))
                    Activity = new StreamingGame(name, streamUrl);
                else
                    Activity = new Game(name, type);
            }
            await StartAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async Task StopAsync()
            => await _connection.StopAsync().ConfigureAwait(false);

        private async Task OnConnectingAsync()
        {
            bool locked = false;
            if (_shardedClient != null && _sessionId == null)
            {
                await _shardedClient.AcquireIdentifyLockAsync(ShardId, _connection.CancelToken).ConfigureAwait(false);
                locked = true;
            }

            try
            {
                await _gatewayLogger.DebugAsync("Connecting ApiClient").ConfigureAwait(false);
                await ApiClient.ConnectAsync().ConfigureAwait(false);

                if (_sessionId != null)
                {
                    await _gatewayLogger.DebugAsync("Resuming").ConfigureAwait(false);
                    await ApiClient.SendResumeAsync(_sessionId, _lastSeq).ConfigureAwait(false);
                }
                else
                {
                    await _gatewayLogger.DebugAsync("Identifying").ConfigureAwait(false);
                    if (Activity is RichGame)
                        throw new NotSupportedException("Outgoing Rich Presences are not supported via WebSocket.");

                    if (Activity == null)
                    {
                        await ApiClient.SendIdentifyAsync(shardID: ShardId, totalShards: TotalShards, gatewayIntents: _gatewayIntents).ConfigureAwait(false);
                    }
                    else
                    {
                        await ApiClient.SendIdentifyAsync(shardID: ShardId, totalShards: TotalShards, gatewayIntents: _gatewayIntents, presence: new StatusUpdateParams
                        {
                            Game = Activity == null ? null : new GameModel
                            {
                                Name = Activity.Name,
                                Type = Activity.Type,
                                StreamUrl = Activity is StreamingGame sg ? sg.Url : null,
                            },
                            Status = Status,
                            IsAFK = Status == UserStatus.AFK,
                            IdleSince = _statusSince != null ? _statusSince.Value.ToUnixTimeMilliseconds() : (long?)null
                        }).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (locked)
                    _shardedClient.ReleaseIdentifyLock();
            }
            //Wait for READY
            await _connection.WaitAsync().ConfigureAwait(false);
            //await SendStatusAsync().ConfigureAwait(false);
        }


        private async Task OnDisconnectingAsync(Exception ex)
        {

            await _gatewayLogger.DebugAsync("Disconnecting ApiClient").ConfigureAwait(false);
            await ApiClient.DisconnectAsync(ex).ConfigureAwait(false);

            //Wait for tasks to complete
            await _gatewayLogger.DebugAsync("Waiting for heartbeater").ConfigureAwait(false);
            Task heartbeatTask = _heartbeatTask;
            if (heartbeatTask != null)
                await heartbeatTask.ConfigureAwait(false);
            _heartbeatTask = null;

            while (_heartbeatTimes.TryDequeue(out _)) { }
            _lastMessageTime = 0;

            await _gatewayLogger.DebugAsync("Waiting for guild downloader").ConfigureAwait(false);
            Task guildDownloadTask = _guildDownloadTask;
            if (guildDownloadTask != null)
                await guildDownloadTask.ConfigureAwait(false);
            _guildDownloadTask = null;

            //Clear large guild queue
            await _gatewayLogger.DebugAsync("Clearing large guild queue").ConfigureAwait(false);
            while (_largeGuilds.TryDequeue(out _)) { }

            //Raise virtual GUILD_UNAVAILABLEs
            await _gatewayLogger.DebugAsync("Raising virtual GuildUnavailables").ConfigureAwait(false);
            foreach (SocketGuild guild in State.Guilds)
            {
                if (guild.IsAvailable)
                    await GuildUnavailableAsync(guild).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public override async Task<RestApplication> GetApplicationInfoAsync(RequestOptions options = null)
            => _applicationInfo ?? (_applicationInfo = await ClientHelper.GetApplicationInfoAsync(this, options ?? RequestOptions.Default).ConfigureAwait(false));

        /// <inheritdoc />
        public override SocketGuild GetGuild(ulong id)
            => State.GetGuild(id);

        /// <inheritdoc />
        public override SocketChannel GetChannel(ulong id)
            => State.GetChannel(id);

        /// <summary>
        ///     Gets a generic channel from the cache or does a rest request if unavailable.
        /// </summary>
        /// <example>
        ///     <code language="cs" title="Example method">
        ///     var channel = await _client.GetChannelAsync(381889909113225237);
        ///     if (channel != null &amp;&amp; channel is IMessageChannel msgChannel)
        ///     {
        ///         await msgChannel.SendMessageAsync($"{msgChannel} is created at {msgChannel.CreatedAt}");
        ///     }
        ///     </code>
        /// </example>
        /// <param name="id">The snowflake identifier of the channel (e.g. `381889909113225237`).</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>
        ///     A task that represents the asynchronous get operation. The task result contains the channel associated
        ///     with the snowflake identifier; <c>null</c> when the channel cannot be found.
        /// </returns>
        public async ValueTask<IChannel> GetChannelAsync(ulong id, RequestOptions options = null)
            => GetChannel(id) ?? (IChannel)await ClientHelper.GetChannelAsync(this, id, options).ConfigureAwait(false);
        /// <summary>
        ///     Gets a user from the cache or does a rest request if unavailable.
        /// </summary>
        /// <example>
        ///     <code language="cs" title="Example method">
        ///     var user = await _client.GetUserAsync(168693960628371456);
        ///     if (user != null)
        ///         Console.WriteLine($"{user} is created at {user.CreatedAt}.";
        ///     </code>
        /// </example>
        /// <param name="id">The snowflake identifier of the user (e.g. `168693960628371456`).</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>
        ///     A task that represents the asynchronous get operation. The task result contains the user associated with
        ///     the snowflake identifier; <c>null</c> if the user is not found.
        /// </returns>
        public async ValueTask<IUser> GetUserAsync(ulong id, RequestOptions options = null)
            => await ClientHelper.GetUserAsync(this, id, options).ConfigureAwait(false);

        /// <summary>
        ///     Clears all cached channels from the client.
        /// </summary>
        public void PurgeChannelCache() => State.PurgeAllChannels();
        /// <summary>
        ///     Clears cached DM channels from the client.
        /// </summary>
        public void PurgeDMChannelCache() => State.PurgeDMChannels();

        /// <inheritdoc />
        public override SocketUser GetUser(ulong id)
            => State.GetUser(id);
        /// <inheritdoc />
        public override SocketUser GetUser(string username, string discriminator)
            => State.Users.FirstOrDefault(x => x.Discriminator == discriminator && x.Username == username);
        /// <summary>
        ///     Clears cached users from the client.
        /// </summary>
        public void PurgeUserCache() => State.PurgeUsers();
        internal SocketGlobalUser GetOrCreateUser(ClientState state, Discord.API.UserJson model)
        {
            return state.GetOrAddUser(model.Id, x => SocketGlobalUser.Create(this, state, model));
        }
        internal SocketUser GetOrCreateTemporaryUser(ClientState state, Discord.API.UserJson model)
        {
            return state.GetUser(model.Id) ?? (SocketUser)SocketUnknownUser.Create(this, state, model);
        }
        internal SocketGlobalUser GetOrCreateSelfUser(ClientState state, Discord.API.UserJson model)
        {
            return state.GetOrAddUser(model.Id, x =>
            {
                SocketGlobalUser user = SocketGlobalUser.Create(this, state, model);
                user.GlobalUser.AddRef();
                user.Presence = new SocketPresence(UserStatus.Online, null, null);
                return user;
            });
        }
        internal void RemoveUser(ulong id)
            => State.RemoveUser(id);

        /// <inheritdoc />
        public override async ValueTask<IReadOnlyCollection<RestVoiceRegion>> GetVoiceRegionsAsync(RequestOptions options = null)
        {
            if (_parentClient == null)
            {
                if (_voiceRegions == null)
                {
                    options = RequestOptions.CreateOrClone(options);
                    options.IgnoreState = true;
                    var voiceRegions = await ApiClient.GetVoiceRegionsAsync(options).ConfigureAwait(false);
                    _voiceRegions = voiceRegions.Select(x => RestVoiceRegion.Create(this, x)).ToImmutableDictionary(x => x.Id);
                }
                return _voiceRegions.ToReadOnlyCollection();
            }
            return await _parentClient.GetVoiceRegionsAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async ValueTask<RestVoiceRegion> GetVoiceRegionAsync(string id, RequestOptions options = null)
        {
            if (_parentClient == null)
            {
                if (_voiceRegions == null)
                    await GetVoiceRegionsAsync().ConfigureAwait(false);
                if (_voiceRegions.TryGetValue(id, out RestVoiceRegion region))
                    return region;
                return null;
            }
            return await _parentClient.GetVoiceRegionAsync(id, options).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async Task DownloadUsersAsync(IEnumerable<IGuild> guilds)
        {
            if (!BaseConfig.GatewayIntents.HasFlag(GatewayIntents.GuildMembers))
                throw new Exception("You need the Guild Members intent set on the client.");
            if (ConnectionState == ConnectionState.Connected)
            {
                //Race condition leads to guilds being requested twice, probably okay
                await ProcessUserDownloadsAsync(guilds.Select(x => GetGuild(x.Id)).Where(x => x != null)).ConfigureAwait(false);
            }
        }
        private async Task ProcessUserDownloadsAsync(IEnumerable<SocketGuild> guilds)
        {
            ImmutableArray<SocketGuild> cachedGuilds = guilds.ToImmutableArray();

            const short batchSize = 1;
            ulong[] batchIds = new ulong[Math.Min(batchSize, cachedGuilds.Length)];
            Task[] batchTasks = new Task[batchIds.Length];
            int batchCount = (cachedGuilds.Length + (batchSize - 1)) / batchSize;
            for (int i = 0, k = 0; i < batchCount; i++)
            {
                bool isLast = i == batchCount - 1;
                int count = isLast ? (cachedGuilds.Length - (batchCount - 1) * batchSize) : batchSize;

                for (int j = 0; j < count; j++, k++)
                {
                    SocketGuild guild = cachedGuilds[k];
                    batchIds[j] = guild.Id;
                    batchTasks[j] = guild.DownloaderPromise;
                }

                await ApiClient.SendRequestMembersAsync(batchIds).ConfigureAwait(false);

                if (isLast && batchCount > 1)
                    await Task.WhenAll(batchTasks.Take(count)).ConfigureAwait(false);
                else
                    await Task.WhenAll(batchTasks).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        /// <example>
        ///     The following example sets the status of the current user to Do Not Disturb.
        ///     <code language="cs">
        ///     await client.SetStatusAsync(UserStatus.DoNotDisturb);
        ///     </code>
        /// </example>
        public override async Task SetStatusAsync(UserStatus status)
        {
            Status = status;
            if (status == UserStatus.AFK)
                _statusSince = DateTimeOffset.UtcNow;
            else
                _statusSince = null;
            await SendStatusAsync().ConfigureAwait(false);
        }
        /// <inheritdoc />
        /// <example>
        /// <para>
        ///     The following example sets the activity of the current user to the specified game name.
        ///     <code language="cs">
        ///     await client.SetGameAsync("A Strange Game");
        ///     </code>
        /// </para>
        /// <para>
        ///     The following example sets the activity of the current user to a streaming status.
        ///     <code language="cs">
        ///     await client.SetGameAsync("Great Stream 10/10", "https://twitch.tv/MyAmazingStream1337", ActivityType.Streaming);
        ///     </code>
        /// </para>
        /// </example>
        public override async Task SetGameAsync(string name, string streamUrl = null, ActivityType type = ActivityType.Playing)
        {
            if (!string.IsNullOrEmpty(streamUrl))
                Activity = new StreamingGame(name, streamUrl);
            else if (!string.IsNullOrEmpty(name))
                Activity = new Game(name, type);
            else
                Activity = null;
            await SendStatusAsync().ConfigureAwait(false);
        }
        /// <inheritdoc />
        public override async Task SetActivityAsync(IActivity activity)
        {
            Activity = activity;
            await SendStatusAsync().ConfigureAwait(false);
        }

        private async Task SendStatusAsync()
        {
            if (CurrentUser == null)
                return;
            UserStatus status = Status;
            DateTimeOffset? statusSince = _statusSince;
            CurrentUser.Presence = new SocketPresence(status, null, null);

            GameModel gameModel = new GameModel();
            // Discord only accepts rich presence over RPC, don't even bother building a payload
            if (Activity is RichGame)
                throw new NotSupportedException("Outgoing Rich Presences are not supported via WebSocket.");

            if (Activity != null)
            {
                gameModel.Name = Activity.Name;
                gameModel.Type = Activity.Type;
                if (Activity is StreamingGame streamGame)
                    gameModel.StreamUrl = streamGame.Url;
            }

            await ApiClient.SendStatusUpdateAsync(
                status,
                status == UserStatus.AFK,
                statusSince != null ? _statusSince.Value.ToUnixTimeMilliseconds() : (long?)null,
                gameModel).ConfigureAwait(false);
        }

        private async Task ProcessMessageAsync(GatewayOpCode opCode, int? seq, string type, object payload)
        {
            if (seq != null)
                _lastSeq = seq.Value;
            _lastMessageTime = Environment.TickCount;

            try
            {
                switch (opCode)
                {
                    case GatewayOpCode.Hello:
                        {
                            await _gatewayLogger.DebugAsync("Received Hello").ConfigureAwait(false);
                            HelloEvent data = (payload as JToken).ToObject<HelloEvent>(_serializer);

                            _heartbeatTask = RunHeartbeatAsync(data.HeartbeatInterval, _connection.CancelToken);
                        }
                        break;
                    case GatewayOpCode.Heartbeat:
                        {
                            await _gatewayLogger.DebugAsync("Received Heartbeat").ConfigureAwait(false);

                            await ApiClient.SendHeartbeatAsync(_lastSeq).ConfigureAwait(false);
                        }
                        break;
                    case GatewayOpCode.HeartbeatAck:
                        {
                            await _gatewayLogger.DebugAsync("Received HeartbeatAck").ConfigureAwait(false);

                            if (_heartbeatTimes.TryDequeue(out long time))
                            {
                                int latency = (int)(Environment.TickCount - time);
                                int before = Latency;
                                Latency = latency;

                                await TimedInvokeAsync(_latencyUpdatedEvent, nameof(LatencyUpdated), before, latency).ConfigureAwait(false);
                            }
                        }
                        break;
                    case GatewayOpCode.InvalidSession:
                        {
                            await _gatewayLogger.DebugAsync("Received InvalidSession").ConfigureAwait(false);
                            await _gatewayLogger.WarningAsync("Failed to resume previous session").ConfigureAwait(false);

                            _sessionId = null;
                            _lastSeq = 0;

                            await _shardedClient.AcquireIdentifyLockAsync(ShardId, _connection.CancelToken).ConfigureAwait(false);
                            try
                            {
                                await ApiClient.SendIdentifyAsync(shardID: ShardId, totalShards: TotalShards, gatewayIntents: _gatewayIntents).ConfigureAwait(false);
                            }
                            finally
                            {
                                _shardedClient.ReleaseIdentifyLock();
                            }
                        }
                        break;
                    case GatewayOpCode.Reconnect:
                        {
                            await _gatewayLogger.DebugAsync("Received Reconnect").ConfigureAwait(false);
                            _connection.Error(new GatewayReconnectException("Server requested a reconnect"));
                        }
                        break;
                    case GatewayOpCode.Dispatch:
                        switch (type)
                        {
                            //Connection
                            case "READY":
                                {
                                    try
                                    {
                                        await _gatewayLogger.DebugAsync("Received Dispatch (READY)").ConfigureAwait(false);

                                        ReadyEvent data = (payload as JToken).ToObject<ReadyEvent>(_serializer);
                                        ClientState state = new ClientState(data.Guilds.Length, data.PrivateChannels.Length);

                                        SocketSelfUser currentUser = SocketSelfUser.Create(this, state, data.User);
                                        ApiClient.CurrentUserId = currentUser.Id;
                                        Rest.CurrentUser = RestSelfUser.Create(this, data.User);
                                        int unavailableGuilds = 0;
                                        for (int i = 0; i < data.Guilds.Length; i++)
                                        {
                                            ExtendedGuild model = data.Guilds[i];
                                            SocketGuild guild = AddGuild(model, state);
                                            if (!guild.IsAvailable)
                                                unavailableGuilds++;
                                            else
                                                await GuildAvailableAsync(guild).ConfigureAwait(false);
                                        }
                                        for (int i = 0; i < data.PrivateChannels.Length; i++)
                                            AddPrivateChannel(data.PrivateChannels[i], state);

                                        _sessionId = data.SessionId;
                                        _unavailableGuildCount = unavailableGuilds;
                                        CurrentUser = currentUser;
                                        if (Rest.CurrentUser == null)
                                            Rest.CurrentUser = RestSelfUser.Create(this, data.User);
                                        State = state;
                                    }
                                    catch (Exception ex)
                                    {
                                        _connection.CriticalError(new Exception("Processing READY failed", ex));
                                        return;
                                    }

                                    _lastGuildAvailableTime = Environment.TickCount;
                                    _guildDownloadTask = WaitForGuildsAsync(_connection.CancelToken, _gatewayLogger)
                                        .ContinueWith(async x =>
                                        {
                                            if (x.IsFaulted)
                                            {
                                                _connection.Error(x.Exception);
                                                return;
                                            }
                                            else if (_connection.CancelToken.IsCancellationRequested)
                                                return;

                                            if (BaseConfig.AlwaysDownloadUsers)
                                                _ = DownloadUsersAsync(Guilds.Where(x => x.IsAvailable && !x.HasAllMembers));

                                            await TimedInvokeAsync(_readyEvent, nameof(Ready)).ConfigureAwait(false);
                                            await _gatewayLogger.InfoAsync("Ready").ConfigureAwait(false);
                                        });
                                    _ = _connection.CompleteAsync();
                                }
                                break;
                            case "RESUMED":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (RESUMED)").ConfigureAwait(false);

                                    _ = _connection.CompleteAsync();

                                    //Notify the client that these guilds are available again
                                    foreach (SocketGuild guild in State.Guilds)
                                    {
                                        if (guild.IsAvailable)
                                            await GuildAvailableAsync(guild).ConfigureAwait(false);
                                    }

                                    await _gatewayLogger.InfoAsync("Resumed previous session").ConfigureAwait(false);
                                }
                                break;

                            //Guilds
                            case "GUILD_CREATE":
                                {
                                    ExtendedGuild data = (payload as JToken).ToObject<ExtendedGuild>(_serializer);

                                    if (data.Unavailable == false)
                                    {
                                        type = "GUILD_AVAILABLE";
                                        _lastGuildAvailableTime = Environment.TickCount;
                                        await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_AVAILABLE)").ConfigureAwait(false);

                                        SocketGuild guild = State.GetGuild(data.Id);
                                        if (guild != null)
                                        {
                                            guild.Update(State, data);

                                            if (_unavailableGuildCount != 0)
                                                _unavailableGuildCount--;
                                            await GuildAvailableAsync(guild).ConfigureAwait(false);
                                            if (base.BaseConfig.Debug.WarnRolesLimit && guild.Roles.Count() > 250)
                                                await _gatewayLogger.WarningAsync($"Max roles limit over 250 in {guild.Name} | {guild.Id}").ConfigureAwait(false);

                                            if (guild.DownloadedMemberCount >= guild.MemberCount && !guild.DownloaderPromise.IsCompleted)
                                            {
                                                guild.CompleteDownloadUsers();
                                                await TimedInvokeAsync(_guildMembersDownloadedEvent, nameof(GuildMembersDownloaded), guild).ConfigureAwait(false);
                                            }
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_CREATE)").ConfigureAwait(false);

                                        SocketGuild guild = AddGuild(data, State);
                                        if (guild != null)
                                        {
                                            await TimedInvokeAsync(_joinedGuildEvent, nameof(JoinedGuild), guild).ConfigureAwait(false);
                                            await GuildAvailableAsync(guild).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                }
                                break;
                            case "GUILD_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_UPDATE)").ConfigureAwait(false);

                                    GuildJson data = (payload as JToken).ToObject<API.GuildJson>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.Id);
                                    if (guild != null)
                                    {
                                        SocketGuild before = guild.Clone();
                                        guild.Update(State, data);
                                        await TimedInvokeAsync(_guildUpdatedEvent, nameof(GuildUpdated), before, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_EMOJIS_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_EMOJIS_UPDATE)").ConfigureAwait(false);

                                    GuildEmojiUpdateEvent data = (payload as JToken).ToObject<API.Gateway.GuildEmojiUpdateEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketGuild before = guild.Clone();
                                        guild.Update(State, data);
                                        await TimedInvokeAsync(_guildUpdatedEvent, nameof(GuildUpdated), before, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_SYNC":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_SYNC)").ConfigureAwait(false);
                                    GuildSyncEvent data = (payload as JToken).ToObject<GuildSyncEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.Id);
                                    if (guild != null)
                                    {
                                        SocketGuild before = guild.Clone();
                                        guild.Update(State, data);
                                        //This is treated as an extension of GUILD_AVAILABLE
                                        _unavailableGuildCount--;
                                        _lastGuildAvailableTime = Environment.TickCount;
                                        await GuildAvailableAsync(guild).ConfigureAwait(false);
                                        await TimedInvokeAsync(_guildUpdatedEvent, nameof(GuildUpdated), before, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_DELETE":
                                {
                                    ExtendedGuild data = (payload as JToken).ToObject<ExtendedGuild>(_serializer);
                                    if (data.Unavailable == true)
                                    {
                                        type = "GUILD_UNAVAILABLE";
                                        await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_UNAVAILABLE)").ConfigureAwait(false);

                                        SocketGuild guild = State.GetGuild(data.Id);
                                        if (guild != null)
                                        {
                                            await GuildUnavailableAsync(guild).ConfigureAwait(false);
                                            _unavailableGuildCount++;
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_DELETE)").ConfigureAwait(false);

                                        SocketGuild guild = RemoveGuild(data.Id);
                                        if (guild != null)
                                        {
                                            await GuildUnavailableAsync(guild).ConfigureAwait(false);
                                            await TimedInvokeAsync(_leftGuildEvent, nameof(LeftGuild), guild).ConfigureAwait(false);
                                            (guild as IDisposable).Dispose();
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                }
                                break;

                            //Channels
                            case "CHANNEL_CREATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (CHANNEL_CREATE)").ConfigureAwait(false);

                                    ChannelJson data = (payload as JToken).ToObject<API.ChannelJson>(_serializer);
                                    SocketChannel channel = null;
                                    if (data.GuildId.IsSpecified)
                                    {
                                        SocketGuild guild = State.GetGuild(data.GuildId.Value);
                                        if (guild != null)
                                        {
                                            channel = guild.AddChannel(State, data);

                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.GuildId.Value).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        channel = State.GetChannel(data.Id);
                                        if (channel != null)
                                            return; //Discord may send duplicate CHANNEL_CREATEs for DMs
                                        channel = AddPrivateChannel(data, State) as SocketChannel;
                                    }

                                    if (channel != null)
                                        await TimedInvokeAsync(_channelCreatedEvent, nameof(ChannelCreated), channel).ConfigureAwait(false);
                                }
                                break;
                            case "CHANNEL_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (CHANNEL_UPDATE)").ConfigureAwait(false);

                                    ChannelJson data = (payload as JToken).ToObject<API.ChannelJson>(_serializer);
                                    SocketChannel channel = State.GetChannel(data.Id);
                                    if (channel != null)
                                    {
                                        SocketChannel before = channel.Clone();
                                        channel.Update(State, data);

                                        SocketGuild guild = (channel as SocketGuildChannel)?.Guild;
                                        if (!(guild?.IsSynced ?? true))
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        await TimedInvokeAsync(_channelUpdatedEvent, nameof(ChannelUpdated), before, channel).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownChannelAsync(type, data.Id).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "CHANNEL_DELETE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (CHANNEL_DELETE)").ConfigureAwait(false);

                                    SocketChannel channel = null;
                                    ChannelJson data = (payload as JToken).ToObject<API.ChannelJson>(_serializer);
                                    if (data.GuildId.IsSpecified)
                                    {
                                        SocketGuild guild = State.GetGuild(data.GuildId.Value);
                                        if (guild != null)
                                        {
                                            channel = guild.RemoveChannel(State, data.Id);

                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }
                                        }
                                        else
                                        {
                                            await UnknownGuildAsync(type, data.GuildId.Value).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                        channel = RemovePrivateChannel(data.Id) as SocketChannel;

                                    if (channel != null)
                                        await TimedInvokeAsync(_channelDestroyedEvent, nameof(ChannelDestroyed), channel).ConfigureAwait(false);
                                    else
                                    {
                                        await UnknownChannelAsync(type, data.Id, data.GuildId.GetValueOrDefault(0)).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;

                            //Members
                            case "GUILD_MEMBER_ADD":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_MEMBER_ADD)").ConfigureAwait(false);

                                    GuildMemberAddEvent data = (payload as JToken).ToObject<GuildMemberAddEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketGuildUser user = guild.AddOrUpdateUser(data);
                                        guild.MemberCount++;

                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        await TimedInvokeAsync(_userJoinedEvent, nameof(UserJoined), user).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_MEMBER_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_MEMBER_UPDATE)").ConfigureAwait(false);

                                    GuildMemberUpdateEvent data = (payload as JToken).ToObject<GuildMemberUpdateEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketGuildUser user = guild.GetUser(data.User.Id);

                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        if (user != null)
                                        {
                                            SocketGlobalUser globalBefore = user.GlobalUser.Clone();
                                            if (user.GlobalUser.Update(State, data.User))
                                            {
                                                //Global data was updated, trigger UserUpdated
                                                await TimedInvokeAsync(_userUpdatedEvent, nameof(UserUpdated), globalBefore, user).ConfigureAwait(false);
                                            }

                                            SocketGuildUser before = user.Clone();
                                            user.Update(State, data);
                                            await TimedInvokeAsync(_guildMemberUpdatedEvent, nameof(GuildMemberUpdated), before, user).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            if (!guild.HasAllMembers)
                                                await IncompleteGuildUserAsync(type, data.User.Id, data.GuildId).ConfigureAwait(false);
                                            else
                                                await UnknownGuildUserAsync(type, data.User.Id, data.GuildId).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_MEMBER_REMOVE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_MEMBER_REMOVE)").ConfigureAwait(false);

                                    GuildMemberRemoveEvent data = (payload as JToken).ToObject<GuildMemberRemoveEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketGuildUser user = guild.RemoveUser(data.User.Id);
                                        guild.MemberCount--;

                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        if (user != null)
                                            await TimedInvokeAsync(_userLeftEvent, nameof(UserLeft), user).ConfigureAwait(false);
                                        else
                                        {
                                            if (!guild.HasAllMembers)
                                                await IncompleteGuildUserAsync(type, data.User.Id, data.GuildId).ConfigureAwait(false);
                                            else
                                                await UnknownGuildUserAsync(type, data.User.Id, data.GuildId).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_MEMBERS_CHUNK":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_MEMBERS_CHUNK)").ConfigureAwait(false);
                                    
                                    GuildMembersChunkEvent data = (payload as JToken).ToObject<GuildMembersChunkEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        foreach (GuildMemberJson memberModel in data.Members)
                                            guild.AddOrUpdateUser(memberModel);

                                        if (guild.DownloadedMemberCount >= guild.MemberCount && !guild.DownloaderPromise.IsCompleted)
                                        {
                                            guild.CompleteDownloadUsers();
                                            await TimedInvokeAsync(_guildMembersDownloadedEvent, nameof(GuildMembersDownloaded), guild).ConfigureAwait(false);
                                        }
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "CHANNEL_RECIPIENT_ADD":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (CHANNEL_RECIPIENT_ADD)").ConfigureAwait(false);

                                    RecipientEvent data = (payload as JToken).ToObject<RecipientEvent>(_serializer);
                                    if (State.GetChannel(data.ChannelId) is SocketGroupChannel channel)
                                    {
                                        SocketGroupUser user = channel.GetOrAddUser(data.User);
                                        await TimedInvokeAsync(_recipientAddedEvent, nameof(RecipientAdded), user).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "CHANNEL_RECIPIENT_REMOVE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (CHANNEL_RECIPIENT_REMOVE)").ConfigureAwait(false);

                                    RecipientEvent data = (payload as JToken).ToObject<RecipientEvent>(_serializer);
                                    if (State.GetChannel(data.ChannelId) is SocketGroupChannel channel)
                                    {
                                        SocketGroupUser user = channel.RemoveUser(data.User.Id);
                                        if (user != null)
                                            await TimedInvokeAsync(_recipientRemovedEvent, nameof(RecipientRemoved), user).ConfigureAwait(false);
                                        else
                                        {
                                            await UnknownChannelUserAsync(type, data.User.Id, data.ChannelId).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;

                            //Roles
                            case "GUILD_ROLE_CREATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_ROLE_CREATE)").ConfigureAwait(false);

                                    GuildRoleCreateEvent data = (payload as JToken).ToObject<GuildRoleCreateEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketRole role = guild.AddRole(data.Role);

                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }
                                        await TimedInvokeAsync(_roleCreatedEvent, nameof(RoleCreated), role).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_ROLE_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_ROLE_UPDATE)").ConfigureAwait(false);

                                    GuildRoleUpdateEvent data = (payload as JToken).ToObject<GuildRoleUpdateEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketRole role = guild.GetRole(data.Role.Id);
                                        if (role != null)
                                        {
                                            SocketRole before = role.Clone();
                                            role.Update(State, data.Role);

                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }

                                            await TimedInvokeAsync(_roleUpdatedEvent, nameof(RoleUpdated), before, role).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await UnknownRoleAsync(type, data.Role.Id, guild.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_ROLE_DELETE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_ROLE_DELETE)").ConfigureAwait(false);

                                    GuildRoleDeleteEvent data = (payload as JToken).ToObject<GuildRoleDeleteEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        SocketRole role = guild.RemoveRole(data.RoleId);
                                        if (role != null)
                                        {
                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }

                                            await TimedInvokeAsync(_roleDeletedEvent, nameof(RoleDeleted), role).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await UnknownRoleAsync(type, data.RoleId, guild.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;

                            //Bans
                            case "GUILD_BAN_ADD":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_BAN_ADD)").ConfigureAwait(false);

                                    GuildBanEvent data = (payload as JToken).ToObject<GuildBanEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        SocketUser user = guild.GetUser(data.User.Id);
                                        if (user == null)
                                            user = SocketUnknownUser.Create(this, State, data.User);
                                        await TimedInvokeAsync(_userBannedEvent, nameof(UserBanned), user, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_BAN_REMOVE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_BAN_REMOVE)").ConfigureAwait(false);

                                    GuildBanEvent data = (payload as JToken).ToObject<GuildBanEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        SocketUser user = State.GetUser(data.User.Id);
                                        if (user == null)
                                            user = SocketUnknownUser.Create(this, State, data.User);
                                        await TimedInvokeAsync(_userUnbannedEvent, nameof(UserUnbanned), user, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            case "GUILD_JOIN_REQUEST_DELETE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (GUILD_JOIN_REQUEST_DELETE)").ConfigureAwait(false);
                                    GuildJoinRequestDeleted data = (payload as JToken).ToObject<GuildJoinRequestDeleted>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    if (guild != null)
                                    {
                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        SocketUser user = State.GetUser(data.UserId);
                                        await TimedInvokeAsync(_userJoinRequestDeletedEvent, nameof(UserJoinRequestDeleted), user, guild).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;
                            //Messages
                            case "MESSAGE_CREATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_CREATE)").ConfigureAwait(false);
                                    MessageJson data = (payload as JToken).ToObject<API.MessageJson>(_serializer);

                                        var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                        if (channel == null && data.GuildId.IsSpecified)
                                        {
                                            channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                        }

                                        var guild = (channel as SocketGuildChannel)?.Guild;
                                        if (guild != null && !guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }


                                        if (channel == null)
                                        {
                                            if (!data.GuildId.IsSpecified)  // assume it is a DM
                                            {
                                                channel = CreateDMChannel(data.ChannelId, data.Author.Value, State);
                                            }
                                            else
                                            {
                                                await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                                return;
                                            }
                                        }

                                        SocketUser author;
                                        if (guild != null)
                                        {
                                            if (data.WebhookId.IsSpecified)
                                                author = SocketWebhookUser.Create(guild, State, data.Author.Value, data.WebhookId.Value);
                                            else
                                                author = guild.GetUser(data.Author.Value.Id);
                                        }
                                        else
                                            author = (channel as SocketChannel).GetUser(data.Author.Value.Id);

                                        if (author == null)
                                        {
                                            if (guild != null)
                                            {
                                                if (data.Member.IsSpecified) // member isn't always included, but use it when we can
                                                {
                                                    data.Member.Value.User = data.Author.Value;
                                                    author = guild.AddOrUpdateUser(data.Member.Value);
                                                }
                                                else
                                                    author = guild.AddOrUpdateUser(data.Author.Value); // user has no guild-specific data
                                            }
                                            else if (channel is SocketGroupChannel groupChannel)
                                                author = groupChannel.GetOrAddUser(data.Author.Value);
                                            else
                                            {
                                                await UnknownChannelUserAsync(type, data.Author.Value.Id, channel.Id).ConfigureAwait(false);
                                                return;
                                            }
                                        }

                                        var msg = SocketMessage.Create(this, State, author, channel, data);
                                        SocketChannelHelper.AddMessage(channel, this, msg);
                                        await TimedInvokeAsync(_messageReceivedEvent, nameof(MessageReceived), msg).ConfigureAwait(false);
                                    
                                }
                                break;
                            case "MESSAGE_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_UPDATE)").ConfigureAwait(false);

                                    MessageJson data = (payload as JToken).ToObject<API.MessageJson>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    SocketGuild guild = (channel as SocketGuildChannel)?.Guild;
                                    if (guild != null && !guild.IsSynced)
                                    {
                                        await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                        return;
                                    }

                                    SocketMessage before = null, after = null;
                                    SocketMessage cachedMsg = channel?.GetCachedMessage(data.Id);
                                    bool isCached = cachedMsg != null;
                                    if (isCached)
                                    {
                                        before = cachedMsg.Clone();
                                        cachedMsg.Update(State, data);
                                        after = cachedMsg;
                                    }
                                    else
                                    {
                                        //Edited message isnt in cache, create a detached one
                                        SocketUser author;
                                        if (data.Author.IsSpecified)
                                        {
                                            if (guild != null)
                                            {
                                                if (data.WebhookId.IsSpecified)
                                                    author = SocketWebhookUser.Create(guild, State, data.Author.Value, data.WebhookId.Value);
                                                else
                                                    author = guild.GetUser(data.Author.Value.Id);
                                            }
                                            else
                                                author = (channel as SocketChannel)?.GetUser(data.Author.Value.Id);

                                            if (author == null)
                                            {
                                                if (guild != null)
                                                {
                                                    if (data.Member.IsSpecified) // member isn't always included, but use it when we can
                                                    {
                                                        data.Member.Value.User = data.Author.Value;
                                                        author = guild.AddOrUpdateUser(data.Member.Value);
                                                    }
                                                    else
                                                        author = guild.AddOrUpdateUser(data.Author.Value); // user has no guild-specific data
                                                }
                                                else if (channel is SocketGroupChannel groupChannel)
                                                    author = groupChannel.GetOrAddUser(data.Author.Value);
                                            }
                                        }
                                        else
                                            author = new SocketUnknownUser(this, id: 0);

                                        if (channel == null)
                                        {
                                            if (!data.GuildId.IsSpecified)  // assume it is a DM
                                            {
                                                if (data.Author.IsSpecified)
                                                {
                                                    var dmChannel = CreateDMChannel(data.ChannelId, data.Author.Value, State);
                                                    channel = dmChannel;
                                                    author = dmChannel.Recipient;
                                                }
                                                else
                                                    channel = CreateDMChannel(data.ChannelId, author, State);
                                            }
                                            else
                                            {
                                                await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                                return;
                                            }
                                        }

                                        after = SocketMessage.Create(this, State, author, channel, data);
                                    }
                                    Cacheable<IMessage, ulong> cacheableBefore = new Cacheable<IMessage, ulong>(before, data.Id, isCached, async () => await channel.GetMessageAsync(data.Id).ConfigureAwait(false));

                                    await TimedInvokeAsync(_messageUpdatedEvent, nameof(MessageUpdated), cacheableBefore, after, channel).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_DELETE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_DELETE)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<API.MessageJson>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    var guild = (channel as SocketGuildChannel)?.Guild;
                                    if (!(guild?.IsSynced ?? true))
                                    {
                                        await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                        return;
                                    }

                                    SocketMessage msg = null;
                                    if (channel != null)
                                        msg = SocketChannelHelper.RemoveMessage(channel, this, data.Id);
                                    var cacheableMsg = new Cacheable<IMessage, ulong>(msg, data.Id, msg != null, () => Task.FromResult((IMessage)null));
                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);

                                    await TimedInvokeAsync(_messageDeletedEvent, nameof(MessageDeleted), cacheableMsg, cacheableChannel).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_REACTION_ADD":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_REACTION_ADD)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<API.Gateway.Reaction>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }


                                    var cachedMsg = channel?.GetCachedMessage(data.MessageId) as SocketUserMessage;
                                    bool isMsgCached = cachedMsg != null;
                                    IUser user = null;
                                    if (channel != null)
                                        user = await channel.GetUserAsync(data.UserId, CacheMode.CacheOnly).ConfigureAwait(false);

                                    var optionalMsg = !isMsgCached
                                        ? Optional.Create<SocketUserMessage>()
                                        : Optional.Create(cachedMsg);

                                    if (data.Member.IsSpecified)
                                    {
                                        var guild = (channel as SocketGuildChannel)?.Guild;

                                        if (guild != null)
                                            user = guild.AddOrUpdateUser(data.Member.Value);
                                    }
                                    else
                                        user = GetUser(data.UserId);

                                    var optionalUser = user is null
                                        ? Optional.Create<IUser>()
                                        : Optional.Create(user);

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);
                                    var cacheableMsg = new Cacheable<IUserMessage, ulong>(cachedMsg, data.MessageId, isMsgCached, async () =>
                                    {
                                        var channelObj = await cacheableChannel.GetOrDownloadAsync().ConfigureAwait(false);
                                        return await channelObj.GetMessageAsync(data.MessageId).ConfigureAwait(false) as IUserMessage;
                                    });
                                    var reaction = SocketReaction.Create(data, channel, optionalMsg, optionalUser);

                                    cachedMsg?.AddReaction(reaction);

                                    await TimedInvokeAsync(_reactionAddedEvent, nameof(ReactionAdded), cacheableMsg, cacheableChannel, reaction).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_REACTION_REMOVE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_REACTION_REMOVE)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<API.Gateway.Reaction>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }
                                    var cachedMsg = channel?.GetCachedMessage(data.MessageId) as SocketUserMessage;
                                    bool isMsgCached = cachedMsg != null;
                                    IUser user = null;
                                    if (channel != null)
                                        user = await channel.GetUserAsync(data.UserId, CacheMode.CacheOnly).ConfigureAwait(false);
                                    else if (!data.GuildId.IsSpecified)
                                        user = GetUser(data.UserId);

                                    var optionalMsg = !isMsgCached
                                        ? Optional.Create<SocketUserMessage>()
                                        : Optional.Create(cachedMsg);

                                    var optionalUser = user is null
                                        ? Optional.Create<IUser>()
                                        : Optional.Create(user);

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);
                                    var cacheableMsg = new Cacheable<IUserMessage, ulong>(cachedMsg, data.MessageId, isMsgCached, async () =>
                                    {
                                        var channelObj = await cacheableChannel.GetOrDownloadAsync().ConfigureAwait(false);
                                        return await channelObj.GetMessageAsync(data.MessageId).ConfigureAwait(false) as IUserMessage;
                                    });
                                    var reaction = SocketReaction.Create(data, channel, optionalMsg, optionalUser);

                                    cachedMsg?.RemoveReaction(reaction);

                                    await TimedInvokeAsync(_reactionRemovedEvent, nameof(ReactionRemoved), cacheableMsg, cacheableChannel, reaction).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_REACTION_REMOVE_ALL":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_REACTION_REMOVE_ALL)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<RemoveAllReactionsEvent>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);
                                    var cachedMsg = channel?.GetCachedMessage(data.MessageId) as SocketUserMessage;
                                    bool isMsgCached = cachedMsg != null;
                                    var cacheableMsg = new Cacheable<IUserMessage, ulong>(cachedMsg, data.MessageId, isMsgCached, async () =>
                                    {
                                        var channelObj = await cacheableChannel.GetOrDownloadAsync().ConfigureAwait(false);
                                        return await channelObj.GetMessageAsync(data.MessageId).ConfigureAwait(false) as IUserMessage;
                                    });

                                    cachedMsg?.ClearReactions();

                                    await TimedInvokeAsync(_reactionsClearedEvent, nameof(ReactionsCleared), cacheableMsg, cacheableChannel).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_REACTION_REMOVE_EMOJI":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_REACTION_REMOVE_EMOJI)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<API.Gateway.RemoveAllReactionsForEmoteEvent>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    var cachedMsg = channel?.GetCachedMessage(data.MessageId) as SocketUserMessage;
                                    bool isMsgCached = cachedMsg != null;

                                    var optionalMsg = !isMsgCached
                                        ? Optional.Create<SocketUserMessage>()
                                        : Optional.Create(cachedMsg);

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);
                                    var cacheableMsg = new Cacheable<IUserMessage, ulong>(cachedMsg, data.MessageId, isMsgCached, async () =>
                                    {
                                        var channelObj = await cacheableChannel.GetOrDownloadAsync().ConfigureAwait(false);
                                        return await channelObj.GetMessageAsync(data.MessageId).ConfigureAwait(false) as IUserMessage;
                                    });
                                    var emote = data.Emoji.ToIEmote();

                                    cachedMsg?.RemoveReactionsForEmote(emote);

                                    await TimedInvokeAsync(_reactionsRemovedForEmoteEvent, nameof(ReactionsRemovedForEmote), cacheableMsg, cacheableChannel, emote).ConfigureAwait(false);
                                }
                                break;
                            case "MESSAGE_DELETE_BULK":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (MESSAGE_DELETE_BULK)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<MessageDeleteBulkEvent>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    var guild = (channel as SocketGuildChannel)?.Guild;
                                    if (!(guild?.IsSynced ?? true))
                                    {
                                        await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                        return;
                                    }

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);
                                    var cacheableList = new List<Cacheable<IMessage, ulong>>(data.Ids.Length);
                                    foreach (ulong id in data.Ids)
                                    {
                                        SocketMessage msg = null;
                                        if (channel != null)
                                            msg = SocketChannelHelper.RemoveMessage(channel, this, id);
                                        bool isMsgCached = msg != null;
                                        var cacheableMsg = new Cacheable<IMessage, ulong>(msg, id, isMsgCached, () => Task.FromResult((IMessage)null));
                                        cacheableList.Add(cacheableMsg);
                                    }

                                    await TimedInvokeAsync(_messagesBulkDeletedEvent, nameof(MessagesBulkDeleted), cacheableList, cacheableChannel).ConfigureAwait(false);
                                }
                                break;

                            //Statuses
                            case "PRESENCE_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (PRESENCE_UPDATE)").ConfigureAwait(false);

                                    PresenceJson data = (payload as JToken).ToObject<API.PresenceJson>(_serializer);

                                    if (data.GuildId.IsSpecified)
                                    {
                                        SocketGuild guild = State.GetGuild(data.GuildId.Value);
                                        if (guild == null)
                                        {
                                            await UnknownGuildAsync(type, data.GuildId.Value).ConfigureAwait(false);
                                            return;
                                        }
                                        if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        SocketGuildUser user = guild.GetUser(data.User.Id);
                                        if (user == null)
                                        {
                                            if (data.Status == UserStatus.Offline)
                                            {
                                                return;
                                            }
                                            user = guild.AddOrUpdateUser(data);
                                        }
                                        else
                                        {
                                            SocketGlobalUser globalBefore = user.GlobalUser.Clone();
                                            if (user.GlobalUser.Update(State, data.User))
                                            {
                                                //Global data was updated, trigger UserUpdated
                                                await TimedInvokeAsync(_userUpdatedEvent, nameof(UserUpdated), globalBefore, user).ConfigureAwait(false);
                                            }
                                        }

                                        SocketGuildUser before = user.Clone();
                                        user.Update(State, data, true);
                                        await TimedInvokeAsync(_guildMemberUpdatedEvent, nameof(GuildMemberUpdated), before, user).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        SocketGlobalUser globalUser = State.GetUser(data.User.Id);
                                        if (globalUser == null)
                                        {
                                            await UnknownGlobalUserAsync(type, data.User.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        SocketGlobalUser before = globalUser.Clone();
                                        globalUser.Update(State, data.User);
                                        globalUser.Update(State, data);
                                        await TimedInvokeAsync(_userUpdatedEvent, nameof(UserUpdated), before, globalUser).ConfigureAwait(false);
                                    }
                                }
                                break;
                            case "TYPING_START":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (TYPING_START)").ConfigureAwait(false);

                                    var data = (payload as JToken).ToObject<TypingStartEvent>(_serializer);
                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId), State, data.ChannelId);
                                    }

                                    var guild = (channel as SocketGuildChannel)?.Guild;
                                    if (!(guild?.IsSynced ?? true))
                                    {
                                        await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                        return;
                                    }

                                    var cacheableChannel = new Cacheable<IMessageChannel, ulong>(channel, data.ChannelId, channel != null, async () => await GetChannelAsync(data.ChannelId).ConfigureAwait(false) as IMessageChannel);

                                    var user = (channel as SocketChannel)?.GetUser(data.UserId);
                                    if (user == null)
                                    {
                                        if (guild != null)
                                            user = guild.AddOrUpdateUser(data.Member);
                                    }
                                    var cacheableUser = new Cacheable<IUser, ulong>(user, data.UserId, user != null, async () => await GetUserAsync(data.UserId).ConfigureAwait(false));

                                    await TimedInvokeAsync(_userIsTypingEvent, nameof(UserIsTyping), cacheableUser, cacheableChannel).ConfigureAwait(false);
                                }
                                break;

                            //Users
                            case "USER_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (USER_UPDATE)").ConfigureAwait(false);

                                    UserJson data = (payload as JToken).ToObject<API.UserJson>(_serializer);
                                    if (data.Id == CurrentUser.Id)
                                    {
                                        SocketSelfUser before = CurrentUser.Clone();
                                        CurrentUser.Update(State, data);
                                        await TimedInvokeAsync(_selfUpdatedEvent, nameof(CurrentUserUpdated), before, CurrentUser).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await _gatewayLogger.WarningAsync("Received USER_UPDATE for wrong user.").ConfigureAwait(false);
                                        return;
                                    }
                                }
                                break;

                            //Voice
                            case "VOICE_STATE_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (VOICE_STATE_UPDATE)").ConfigureAwait(false);

                                    VoiceStateJson data = (payload as JToken).ToObject<API.VoiceStateJson>(_serializer);
                                    SocketUser user;
                                    SocketVoiceState before, after;
                                    if (data.GuildId != null)
                                    {
                                        SocketGuild guild = State.GetGuild(data.GuildId.Value);
                                        if (guild == null)
                                        {
                                            await UnknownGuildAsync(type, data.GuildId.Value).ConfigureAwait(false);
                                            return;
                                        }
                                        else if (!guild.IsSynced)
                                        {
                                            await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                            return;
                                        }

                                        if (data.ChannelId != null)
                                        {
                                            before = guild.GetVoiceState(data.UserId)?.Clone() ?? SocketVoiceState.Default;
                                            after = await guild.AddOrUpdateVoiceStateAsync(State, data).ConfigureAwait(false);
                                            /*if (data.UserId == CurrentUser.Id)
                                            {
                                                var _ = guild.FinishJoinAudioChannel().ConfigureAwait(false);
                                            }*/
                                        }
                                        else
                                        {
                                            before = await guild.RemoveVoiceStateAsync(data.UserId).ConfigureAwait(false) ?? SocketVoiceState.Default;
                                            after = SocketVoiceState.Create(null, data);
                                        }

                                        // per g250k, this should always be sent, but apparently not always
                                        user = guild.GetUser(data.UserId)
                                            ?? (data.Member.IsSpecified ? guild.AddOrUpdateUser(data.Member.Value) : null);
                                        if (user == null)
                                        {
                                            await UnknownGuildUserAsync(type, data.UserId, guild.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        SocketGroupChannel groupChannel = GetChannel(data.ChannelId.Value) as SocketGroupChannel;
                                        if (groupChannel == null)
                                        {
                                            await UnknownChannelAsync(type, data.ChannelId.Value).ConfigureAwait(false);
                                            return;
                                        }
                                        if (data.ChannelId != null)
                                        {
                                            before = groupChannel.GetVoiceState(data.UserId)?.Clone() ?? SocketVoiceState.Default;
                                            after = groupChannel.AddOrUpdateVoiceState(State, data);
                                        }
                                        else
                                        {
                                            before = groupChannel.RemoveVoiceState(data.UserId) ?? SocketVoiceState.Default;
                                            after = SocketVoiceState.Create(null, data);
                                        }
                                        user = groupChannel.GetUser(data.UserId);
                                        if (user == null)
                                        {
                                            await UnknownChannelUserAsync(type, data.UserId, groupChannel.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }

                                    await TimedInvokeAsync(_userVoiceStateUpdatedEvent, nameof(UserVoiceStateUpdated), user, before, after).ConfigureAwait(false);
                                }
                                break;
                            case "VOICE_SERVER_UPDATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (VOICE_SERVER_UPDATE)").ConfigureAwait(false);

                                    VoiceServerUpdateEvent data = (payload as JToken).ToObject<VoiceServerUpdateEvent>(_serializer);
                                    SocketGuild guild = State.GetGuild(data.GuildId);
                                    bool isCached = guild != null;
                                    Cacheable<IGuild, ulong> cachedGuild = new Cacheable<IGuild, ulong>(guild, data.GuildId, isCached,
                                        () => Task.FromResult(State.GetGuild(data.GuildId) as IGuild));

                                    SocketVoiceServer voiceServer = new SocketVoiceServer(cachedGuild, data.Endpoint, data.Token);
                                    await TimedInvokeAsync(_voiceServerUpdatedEvent, nameof(UserVoiceStateUpdated), voiceServer).ConfigureAwait(false);

                                    if (isCached)
                                    {
                                        string endpoint = data.Endpoint;

                                        //Only strip out the port if the endpoint contains it
                                        int portBegin = endpoint.LastIndexOf(':');
                                        if (portBegin > 0)
                                            endpoint = endpoint.Substring(0, portBegin);

                                        System.Runtime.CompilerServices.ConfiguredTaskAwaitable _ = guild.FinishConnectAudio(endpoint, data.Token).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await UnknownGuildAsync(type, data.GuildId).ConfigureAwait(false);
                                    }

                                }
                                break;

                            //Ignored (User only)
                            case "CHANNEL_PINS_ACK":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (CHANNEL_PINS_ACK)").ConfigureAwait(false);
                                break;
                            case "CHANNEL_PINS_UPDATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (CHANNEL_PINS_UPDATE)").ConfigureAwait(false);
                                break;
                            case "GUILD_INTEGRATIONS_UPDATE":
                            case "INTEGRATION_UPDATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (GUILD_INTEGRATIONS_UPDATE)").ConfigureAwait(false);
                                break;
                            case "INTEGRATION_CREATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (INTEGRATION_CREATE)").ConfigureAwait(false);
                                break;
                            case "INTEGRATION_DELETE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (INTEGRATION_DELETE)").ConfigureAwait(false);
                                break;
                            case "INVITE_CREATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (INVITE_CREATE)").ConfigureAwait(false);
                                    if (!base.BaseConfig.Debug.Events.DisableInviteCreate)
                                    {
                                        InviteCreateEvent data = (payload as JToken).ToObject<API.Gateway.InviteCreateEvent>(_serializer);

                                        if (State.GetChannel(data.ChannelId) is SocketGuildChannel channel)
                                        {
                                            SocketGuild guild = channel.Guild;
                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }

                                            SocketGuildUser inviter = data.Inviter.IsSpecified
                                                ? (guild.GetUser(data.Inviter.Value.Id) ?? guild.AddOrUpdateUser(data.Inviter.Value))
                                                : null;

                                            SocketUser target = data.TargetUser.IsSpecified
                                                ? (guild.GetUser(data.TargetUser.Value.Id) ?? (SocketUser)SocketUnknownUser.Create(this, State, data.TargetUser.Value))
                                                : null;

                                            SocketInvite invite = SocketInvite.Create(this, guild, channel, inviter, target, data);

                                            await TimedInvokeAsync(_inviteCreatedEvent, nameof(InviteCreated), invite).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                }
                                break;
                            case "INVITE_DELETE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (INVITE_DELETE)").ConfigureAwait(false);
                                    if (!base.BaseConfig.Debug.Events.DisableInviteDelete)
                                    {
                                        InviteDeleteEvent data = (payload as JToken).ToObject<API.Gateway.InviteDeleteEvent>(_serializer);
                                        if (State.GetChannel(data.ChannelId) is SocketGuildChannel channel)
                                        {
                                            SocketGuild guild = channel.Guild;
                                            if (!guild.IsSynced)
                                            {
                                                await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                                return;
                                            }

                                            await TimedInvokeAsync(_inviteDeletedEvent, nameof(InviteDeleted), channel, data.Code).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                            return;
                                        }
                                    }
                                }
                                break;
                            case "MESSAGE_ACK":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (MESSAGE_ACK)").ConfigureAwait(false);
                                break;
                            case "PRESENCES_REPLACE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (PRESENCES_REPLACE)").ConfigureAwait(false);
                                break;
                            case "USER_SETTINGS_UPDATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (USER_SETTINGS_UPDATE)").ConfigureAwait(false);
                                break;
                            case "WEBHOOKS_UPDATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (WEBHOOKS_UPDATE)").ConfigureAwait(false);
                                break;
                            case "GUILD_APPLICATION_COMMAND_COUNTS_UPDATE":
                            case "APPLICATION_COMMAND_UPDATE":
                                await _gatewayLogger.DebugAsync("Ignored Dispatch (GUILD_APPLICATION_COMMAND_COUNTS_UPDATE)").ConfigureAwait(false);
                                break;
                            case "INTERACTION_CREATE":
                                {
                                    await _gatewayLogger.DebugAsync("Received Dispatch (INTERACTION_CREATE)").ConfigureAwait(false);

                                    InteractionCreateJson data = (payload as JToken).ToObject<API.Gateway.InteractionCreateJson>(_serializer);
                                    if (data.Type == InteractionType.Ping)
                                        break;

                                    if (data.Member.IsSpecified)
                                        data.User = data.Member.Value.User;

                                    var channel = GetChannel(data.ChannelId) as ISocketMessageChannel;
                                    if (channel == null && data.GuildId.IsSpecified)
                                    {
                                        channel = MockedThreadChannel.Create(GetGuild(data.GuildId.Value), State, data.ChannelId);
                                    }

                                    var guild = (channel as SocketGuildChannel)?.Guild;
                                    if (guild != null && !guild.IsSynced)
                                    {
                                        await UnsyncedGuildAsync(type, guild.Id).ConfigureAwait(false);
                                        return;
                                    }


                                    if (channel == null)
                                    {
                                        if (!data.GuildId.IsSpecified)  // assume it is a DM
                                        {
                                            channel = CreateDMChannel(data.ChannelId, data.User.Value, State);
                                        }
                                        else
                                        {
                                            await UnknownChannelAsync(type, data.ChannelId).ConfigureAwait(false);
                                            return;
                                        }
                                    }

                                    SocketUser author;
                                    if (guild != null)
                                    {
                                        author = guild.GetUser(data.Member.Value.User.Id);
                                    }
                                    else
                                        author = (channel as SocketChannel).GetUser(data.User.Value.Id);

                                    if (author == null)
                                    {
                                        if (guild != null)
                                        {
                                            if (data.Member.IsSpecified) // member isn't always included, but use it when we can
                                            {
                                                author = guild.AddOrUpdateUser(data.Member.Value);
                                            }
                                            else
                                                author = guild.AddOrUpdateUser(data.User.Value); // user has no guild-specific data
                                        }
                                        else if (channel is SocketGroupChannel groupChannel)
                                            author = groupChannel.GetOrAddUser(data.User.Value);
                                        else
                                        {
                                            await UnknownChannelUserAsync(type, data.User.Value.Id, channel.Id).ConfigureAwait(false);
                                            return;
                                        }
                                    }

                                    Interaction Interaction = new Interaction();
                                    Interaction.Update(this, State, guild, channel, author, data);
                                    await TimedInvokeAsync(_interactionReceivedEvent, nameof(InteractionCreateJson), Interaction);

                                }
                                break;
                            //Others
                            default:
                                await _gatewayLogger.WarningAsync($"Unknown Dispatch ({type})").ConfigureAwait(false);
                                break;
                        }
                        break;
                    default:
                        await _gatewayLogger.WarningAsync($"Unknown OpCode ({opCode})").ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                await _gatewayLogger.ErrorAsync($"Error handling {opCode}{(type != null ? $" ({type})" : "")}", ex).ConfigureAwait(false);
            }
        }

        internal SocketDMChannel CreateDMChannel(ulong channelId, UserJson model, ClientState state)
        {
            return SocketDMChannel.Create(this, state, channelId, model);
        }

        private async Task RunHeartbeatAsync(int intervalMillis, CancellationToken cancelToken)
        {
            try
            {
                await _gatewayLogger.DebugAsync("Heartbeat Started").ConfigureAwait(false);
                while (!cancelToken.IsCancellationRequested)
                {
                    int now = Environment.TickCount;

                    //Did server respond to our last heartbeat, or are we still receiving messages (long load?)
                    if (_heartbeatTimes.Count != 0 && (now - _lastMessageTime) > intervalMillis)
                    {
                        if (ConnectionState == ConnectionState.Connected && (_guildDownloadTask?.IsCompleted ?? true))
                        {
                            _connection.Error(new GatewayReconnectException("Server missed last heartbeat"));
                            return;
                        }
                    }

                    _heartbeatTimes.Enqueue(now);
                    try
                    {
                        await ApiClient.SendHeartbeatAsync(_lastSeq).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _gatewayLogger.WarningAsync("Heartbeat Errored", ex).ConfigureAwait(false);
                    }

                    await Task.Delay(intervalMillis, cancelToken).ConfigureAwait(false);
                }
                await _gatewayLogger.DebugAsync("Heartbeat Stopped").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await _gatewayLogger.DebugAsync("Heartbeat Stopped").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _gatewayLogger.ErrorAsync("Heartbeat Errored", ex).ConfigureAwait(false);
            }
        }

        private async Task WaitForGuildsAsync(CancellationToken cancelToken, Logger logger)
        {
            //Wait for GUILD_AVAILABLEs
            try
            {
                await logger.DebugAsync("GuildDownloader Started").ConfigureAwait(false);
                while ((_unavailableGuildCount != 0) && (Environment.TickCount - _lastGuildAvailableTime < BaseConfig.MaxWaitBetweenGuildAvailablesBeforeReady))
                    await Task.Delay(500, cancelToken).ConfigureAwait(false);
                await logger.DebugAsync("GuildDownloader Stopped").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await logger.DebugAsync("GuildDownloader Stopped").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await logger.ErrorAsync("GuildDownloader Errored", ex).ConfigureAwait(false);
            }
        }

        internal SocketGuild AddGuild(ExtendedGuild model, ClientState state)
        {
            SocketGuild guild = SocketGuild.Create(this, state, model);
            state.AddGuild(guild);
            if (model.Large)
                _largeGuilds.Enqueue(model.Id);
            return guild;
        }
        internal SocketGuild RemoveGuild(ulong id)
            => State.RemoveGuild(id);

        /// <exception cref="InvalidOperationException">Unexpected channel type is created.</exception>
        internal ISocketPrivateChannel AddPrivateChannel(API.ChannelJson model, ClientState state)
        {
            ISocketPrivateChannel channel = SocketChannel.CreatePrivate(this, state, model);
            state.AddChannel(channel as SocketChannel);
            if (channel is SocketDMChannel dm)
                dm.Recipient.GlobalUser.DMChannel = dm;

            return channel;
        }

        internal SocketDMChannel CreateDMChannel(ulong channelId, SocketUser user, ClientState state)
        {
            return new SocketDMChannel(this, channelId, user);
        }

        internal ISocketPrivateChannel RemovePrivateChannel(ulong id)
        {
            ISocketPrivateChannel channel = State.RemoveChannel(id) as ISocketPrivateChannel;
            if (channel != null)
            {
                if (channel is SocketDMChannel dmChannel)
                    dmChannel.Recipient.GlobalUser.DMChannel = null;

                foreach (SocketUser recipient in channel.Recipients)
                    recipient.GlobalUser.RemoveRef(this);
            }
            return channel;
        }

        private async Task GuildAvailableAsync(SocketGuild guild)
        {
            if (!guild.IsConnected)
            {
                guild.IsConnected = true;
                await TimedInvokeAsync(_guildAvailableEvent, nameof(GuildAvailable), guild).ConfigureAwait(false);
            }
        }
        private async Task GuildUnavailableAsync(SocketGuild guild)
        {
            if (guild.IsConnected)
            {
                guild.IsConnected = false;
                await TimedInvokeAsync(_guildUnavailableEvent, nameof(GuildUnavailable), guild).ConfigureAwait(false);
            }
        }

        private async Task TimedInvokeAsync(AsyncEvent<Func<Task>> eventHandler, string name)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, eventHandler.InvokeAsync).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync().ConfigureAwait(false);
            }
        }
        private async Task TimedInvokeAsync<T>(AsyncEvent<Func<T, Task>> eventHandler, string name, T arg)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, () => eventHandler.InvokeAsync(arg)).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync(arg).ConfigureAwait(false);
            }
        }
        private async Task TimedInvokeAsync<T1, T2>(AsyncEvent<Func<T1, T2, Task>> eventHandler, string name, T1 arg1, T2 arg2)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, () => eventHandler.InvokeAsync(arg1, arg2)).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync(arg1, arg2).ConfigureAwait(false);
            }
        }
        private async Task TimedInvokeAsync<T1, T2, T3>(AsyncEvent<Func<T1, T2, T3, Task>> eventHandler, string name, T1 arg1, T2 arg2, T3 arg3)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, () => eventHandler.InvokeAsync(arg1, arg2, arg3)).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync(arg1, arg2, arg3).ConfigureAwait(false);
            }
        }
        private async Task TimedInvokeAsync<T1, T2, T3, T4>(AsyncEvent<Func<T1, T2, T3, T4, Task>> eventHandler, string name, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, () => eventHandler.InvokeAsync(arg1, arg2, arg3, arg4)).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync(arg1, arg2, arg3, arg4).ConfigureAwait(false);
            }
        }
        private async Task TimedInvokeAsync<T1, T2, T3, T4, T5>(AsyncEvent<Func<T1, T2, T3, T4, T5, Task>> eventHandler, string name, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
        {
            if (eventHandler.HasSubscribers)
            {
                if (HandlerTimeout.HasValue)
                    await TimeoutWrap(name, () => eventHandler.InvokeAsync(arg1, arg2, arg3, arg4, arg5)).ConfigureAwait(false);
                else
                    await eventHandler.InvokeAsync(arg1, arg2, arg3, arg4, arg5).ConfigureAwait(false);
            }
        }
        private async Task TimeoutWrap(string name, Func<Task> action)
        {
            try
            {
                Task timeoutTask = Task.Delay(HandlerTimeout.Value);
                Task handlersTask = action();
                if (await Task.WhenAny(timeoutTask, handlersTask).ConfigureAwait(false) == timeoutTask)
                {
                    await _gatewayLogger.WarningAsync($"A {name} handler is blocking the gateway task.").ConfigureAwait(false);
                }
                await handlersTask.ConfigureAwait(false); //Ensure the handler completes
            }
            catch (Exception ex)
            {
                await _gatewayLogger.WarningAsync($"A {name} handler has thrown an unhandled exception.", ex).ConfigureAwait(false);
            }
        }

        private async Task UnknownGlobalUserAsync(string evnt, ulong userId)
        {
            string details = $"{evnt} User={userId}";
            await _gatewayLogger.WarningAsync($"Unknown User ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownChannelUserAsync(string evnt, ulong userId, ulong channelId)
        {
            string details = $"{evnt} User={userId} Channel={channelId}";
            await _gatewayLogger.WarningAsync($"Unknown User ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownGuildUserAsync(string evnt, ulong userId, ulong guildId)
        {
            string details = $"{evnt} User={userId} Guild={guildId}";
            await _gatewayLogger.WarningAsync($"Unknown User ({details}).").ConfigureAwait(false);
        }
        private async Task IncompleteGuildUserAsync(string evnt, ulong userId, ulong guildId)
        {
            string details = $"{evnt} User={userId} Guild={guildId}";
            await _gatewayLogger.DebugAsync($"User has not been downloaded ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownChannelAsync(string evnt, ulong channelId)
        {
            string details = $"{evnt} Channel={channelId}";
            await _gatewayLogger.WarningAsync($"Unknown Channel ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownChannelAsync(string evnt, ulong channelId, ulong guildId)
        {
            if (guildId == 0)
            {
                await UnknownChannelAsync(evnt, channelId).ConfigureAwait(false);
                return;
            }
            string details = $"{evnt} Channel={channelId} Guild={guildId}";
            await _gatewayLogger.WarningAsync($"Unknown Channel ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownRoleAsync(string evnt, ulong roleId, ulong guildId)
        {
            string details = $"{evnt} Role={roleId} Guild={guildId}";
            await _gatewayLogger.WarningAsync($"Unknown Role ({details}).").ConfigureAwait(false);
        }
        private async Task UnknownGuildAsync(string evnt, ulong guildId)
        {
            string details = $"{evnt} Guild={guildId}";
            await _gatewayLogger.WarningAsync($"Unknown Guild ({details}).").ConfigureAwait(false);
        }
        private async Task UnsyncedGuildAsync(string evnt, ulong guildId)
        {
            string details = $"{evnt} Guild={guildId}";
            await _gatewayLogger.DebugAsync($"Unsynced Guild ({details}).").ConfigureAwait(false);
        }

        internal int GetAudioId() => _nextAudioId++;

        //IDiscordClient
        /// <inheritdoc />
        async Task<IApplication> IDiscordClient.GetApplicationInfoAsync(RequestOptions options)
            => await GetApplicationInfoAsync().ConfigureAwait(false);

        /// <inheritdoc />
        async Task<IChannel> IDiscordClient.GetChannelAsync(ulong id, CacheMode mode, RequestOptions options)
    => mode == CacheMode.AllowDownload ? await GetChannelAsync(id, options).ConfigureAwait(false) : GetChannel(id);
        /// <inheritdoc />
        Task<IReadOnlyCollection<IPrivateChannel>> IDiscordClient.GetPrivateChannelsAsync(CacheMode mode, RequestOptions options)
            => Task.FromResult<IReadOnlyCollection<IPrivateChannel>>(PrivateChannels);
        /// <inheritdoc />
        Task<IReadOnlyCollection<IDMChannel>> IDiscordClient.GetDMChannelsAsync(CacheMode mode, RequestOptions options)
            => Task.FromResult<IReadOnlyCollection<IDMChannel>>(DMChannels);
        /// <inheritdoc />
        Task<IReadOnlyCollection<IGroupChannel>> IDiscordClient.GetGroupChannelsAsync(CacheMode mode, RequestOptions options)
            => Task.FromResult<IReadOnlyCollection<IGroupChannel>>(GroupChannels);

        /// <inheritdoc />
        async Task<IReadOnlyCollection<IConnection>> IDiscordClient.GetConnectionsAsync(RequestOptions options)
            => await GetConnectionsAsync().ConfigureAwait(false);

        /// <inheritdoc />
        async Task<IInvite> IDiscordClient.GetInviteAsync(string inviteId, RequestOptions options)
            => await GetInviteAsync(inviteId, options).ConfigureAwait(false);

        /// <inheritdoc />
        Task<IGuild> IDiscordClient.GetGuildAsync(ulong id, CacheMode mode, RequestOptions options)
            => Task.FromResult<IGuild>(GetGuild(id));
        /// <inheritdoc />
        Task<IReadOnlyCollection<IGuild>> IDiscordClient.GetGuildsAsync(CacheMode mode, RequestOptions options)
            => Task.FromResult<IReadOnlyCollection<IGuild>>(Guilds);
        /// <inheritdoc />
        async Task<IGuild> IDiscordClient.CreateGuildAsync(string name, IVoiceRegion region, Stream jpegIcon, RequestOptions options)
            => await CreateGuildAsync(name, region, jpegIcon).ConfigureAwait(false);

        /// <inheritdoc />
        async Task<IUser> IDiscordClient.GetUserAsync(ulong id, CacheMode mode, RequestOptions options)
    => mode == CacheMode.AllowDownload ? await GetUserAsync(id, options).ConfigureAwait(false) : GetUser(id);
        /// <inheritdoc />
        Task<IUser> IDiscordClient.GetUserAsync(string username, string discriminator, RequestOptions options)
            => Task.FromResult<IUser>(GetUser(username, discriminator));

        /// <inheritdoc />
        async Task<IReadOnlyCollection<IVoiceRegion>> IDiscordClient.GetVoiceRegionsAsync(RequestOptions options)
             => await GetVoiceRegionsAsync(options).ConfigureAwait(false);
        /// <inheritdoc />
        async Task<IVoiceRegion> IDiscordClient.GetVoiceRegionAsync(string id, RequestOptions options)
    => await GetVoiceRegionAsync(id, options).ConfigureAwait(false);

        /// <inheritdoc />
        async Task IDiscordClient.StartAsync()
            => await StartAsync().ConfigureAwait(false);
        /// <inheritdoc />
        async Task IDiscordClient.StopAsync()
            => await StopAsync().ConfigureAwait(false);
    }
}
