using Discord.API;
using Discord.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Model = Discord.API.MessageJson;

namespace Discord.WebSocket
{
    /// <summary>
    ///     Represents a WebSocket-based message.
    /// </summary>
    public abstract class SocketMessage : SocketEntity<ulong>, IMessage
    {
        private long _timestampTicks;
        private readonly List<SocketReaction> _reactions = new List<SocketReaction>();

        /// <summary>
        ///     Gets the author of this message.
        /// </summary>
        /// <returns>
        ///     A WebSocket-based user object.
        /// </returns>
        public SocketUser Author { get; }
        /// <summary>
        ///     Gets the source channel of the message.
        /// </summary>
        /// <returns>
        ///     A WebSocket-based message channel.
        /// </returns>
        public ISocketMessageChannel Channel { get; }
        /// <inheritdoc />
        public MessageSource Source { get; }

        /// <inheritdoc />
        public string Content { get; private set; }

        /// <inheritdoc />
        public DateTimeOffset CreatedAt => SnowflakeUtils.FromSnowflake(Id);
        /// <inheritdoc />
        public virtual bool IsTTS => false;
        /// <inheritdoc />
        public virtual bool IsPinned => false;
        /// <inheritdoc />
        public virtual bool IsSuppressed => false;
        /// <inheritdoc />
        public virtual DateTimeOffset? EditedTimestamp => null;

        /// <inheritdoc />
        public MessageActivity Activity { get; private set; }

        /// <inheritdoc />
        public MessageApplication Application { get; private set; }

        /// <inheritdoc />
        public MessageReference Reference { get; private set; }

        public MessageFlags? Flags { get; private set; }

        public MessageType Type { get; private set; }

        /// <summary>
        ///     Returns all attachments included in this message.
        /// </summary>
        /// <returns>
        ///     Collection of attachments.
        /// </returns>
        public virtual IReadOnlyCollection<Attachment> Attachments => ImmutableArray.Create<Attachment>();
        /// <summary>
        ///     Returns all embeds included in this message.
        /// </summary>
        /// <returns>
        ///     Collection of embed objects.
        /// </returns>
        public virtual IReadOnlyCollection<Embed> Embeds => ImmutableArray.Create<Embed>();
        /// <summary>
        ///     Returns the channels mentioned in this message.
        /// </summary>
        /// <returns>
        ///     Collection of WebSocket-based guild channels.
        /// </returns>
        public virtual IReadOnlyCollection<SocketGuildChannel> MentionedChannels => ImmutableArray.Create<SocketGuildChannel>();
        /// <summary>
        ///     Returns the roles mentioned in this message.
        /// </summary>
        /// <returns>
        ///     Collection of WebSocket-based roles.
        /// </returns>
        public virtual IReadOnlyCollection<SocketRole> MentionedRoles => ImmutableArray.Create<SocketRole>();
        /// <summary>
        ///     Returns the users mentioned in this message.
        /// </summary>
        /// <returns>
        ///     Collection of WebSocket-based users.
        /// </returns>
        public virtual IReadOnlyCollection<SocketUser> MentionedUsers => ImmutableArray.Create<SocketUser>();
        /// <inheritdoc />
        public virtual IReadOnlyCollection<ITag> Tags => ImmutableArray.Create<ITag>();
        /// <inheritdoc />
        public IReadOnlyDictionary<IEmote, ReactionMetadata> Reactions => _reactions.GroupBy(r => r.Emote).ToDictionary(x => x.Key, x => new ReactionMetadata { ReactionCount = x.Count(), IsMe = x.Any(y => y.UserId == Discord.CurrentUser.Id) });

        public virtual IReadOnlyCollection<MessageSticker> Stickers => ImmutableArray.Create<MessageSticker>();

        public virtual IReadOnlyCollection<InteractionRow> Components => ImmutableArray.Create<InteractionRow>();

        /// <inheritdoc />
        public DateTimeOffset Timestamp => DateTimeUtils.FromTicks(_timestampTicks);

        internal SocketMessage(DiscordSocketClient discord, ulong id, ISocketMessageChannel channel, SocketUser author, MessageSource source, string content = "")
            : base(discord, id)
        {
            if (content != "")
            Content = content;
            Channel = channel;
            Author = author;
            Source = source;
        }
        internal static SocketMessage Create(DiscordSocketClient discord, ClientState state, SocketUser author, ISocketMessageChannel channel, Model model)
        {
            switch (model.Type)
            {
                case MessageType.Default:
                case MessageType.Reply:
                    return SocketUserMessage.Create(discord, state, author, channel, model);
            }
            return SocketSystemMessage.Create(discord, state, author, channel, model);
        }

        internal virtual void Update(ClientState state, Model model)
        {
            Type = model.Type;
            if (model.Timestamp.IsSpecified)
                _timestampTicks = model.Timestamp.Value.UtcTicks;

            if (model.Content.IsSpecified)
                Content = model.Content.Value;

            if (model.Application.IsSpecified)
            {
                // create a new Application from the API model
                Application = new MessageApplication()
                {
                    Id = model.Application.Value.Id,
                    CoverImage = model.Application.Value.CoverImage,
                    Description = model.Application.Value.Description,
                    Icon = model.Application.Value.Icon,
                    Name = model.Application.Value.Name
                };
            }

            if (model.Activity.IsSpecified)
            {
                // create a new Activity from the API model
                Activity = new MessageActivity()
                {
                    Type = model.Activity.Value.Type.Value,
                    PartyId = model.Activity.Value.PartyId.GetValueOrDefault()
                };
            }

            if (model.Reference.IsSpecified)
            {
                // Creates a new Reference from the API model
                Reference = new MessageReference
                {
                    GuildId = model.Reference.Value.GuildId,
                    ChannelId = model.Reference.Value.ChannelId,
                    MessageId = model.Reference.Value.MessageId
                };
            }

            if (model.Flags.IsSpecified)
                Flags = model.Flags.Value;
        }

        /// <inheritdoc />
        public Task DeleteAsync(RequestOptions options = null)
            => MessageHelper.DeleteAsync(this, Discord, options);

        /// <summary>
        ///     Gets the content of the message.
        /// </summary>
        /// <returns>
        ///     Content of the message.
        /// </returns>
        public override string ToString() => Content;
        internal SocketMessage Clone() => MemberwiseClone() as SocketMessage;

        //IMessage
        /// <inheritdoc />
        IUser IMessage.Author => Author;
        /// <inheritdoc />
        IMessageChannel IMessage.Channel => Channel;
        /// <inheritdoc />
        IReadOnlyCollection<IAttachment> IMessage.Attachments => Attachments;
        /// <inheritdoc />
        IReadOnlyCollection<IEmbed> IMessage.Embeds => Embeds;

        IReadOnlyCollection<ISticker> IMessage.Stickers => Stickers;

        IReadOnlyCollection<InteractionRow> IMessage.Components => Components;
        /// <inheritdoc />
        IReadOnlyCollection<ulong> IMessage.MentionedChannelIds => MentionedChannels.Select(x => x.Id).ToImmutableArray();
        /// <inheritdoc />
        IReadOnlyCollection<ulong> IMessage.MentionedRoleIds => MentionedRoles.Select(x => x.Id).ToImmutableArray();
        /// <inheritdoc />
        IReadOnlyCollection<ulong> IMessage.MentionedUserIds => MentionedUsers.Select(x => x.Id).ToImmutableArray();

        internal void AddReaction(SocketReaction reaction)
        {
            _reactions.Add(reaction);
        }
        internal void RemoveReaction(SocketReaction reaction)
        {
            if (_reactions.Contains(reaction))
                _reactions.Remove(reaction);
        }
        internal void ClearReactions()
        {
            _reactions.Clear();
        }
        internal void RemoveReactionsForEmote(IEmote emote)
        {
            _reactions.RemoveAll(x => x.Emote.Equals(emote));
        }

        /// <inheritdoc />
        public Task AddReactionAsync(IEmote emote, RequestOptions options = null)
            => MessageHelper.AddReactionAsync(this.Channel.Id, this.Id, emote, Discord, options);
        /// <inheritdoc />
        public Task RemoveReactionAsync(IEmote emote, IUser user, RequestOptions options = null)
            => MessageHelper.RemoveReactionAsync(this.Channel.Id, this.Id, user.Id, emote, Discord, options);
        /// <inheritdoc />
        public Task RemoveReactionAsync(IEmote emote, ulong userId, RequestOptions options = null)
            => MessageHelper.RemoveReactionAsync(this.Channel.Id, this.Id, userId, emote, Discord, options);
        /// <inheritdoc />
        public Task RemoveAllReactionsAsync(RequestOptions options = null)
            => MessageHelper.RemoveAllReactionsAsync(this.Channel.Id, this.Id, Discord, options);
        /// <inheritdoc />
        public Task RemoveAllReactionsForEmoteAsync(IEmote emote, RequestOptions options = null)
            => MessageHelper.RemoveAllReactionsForEmoteAsync(this.Channel.Id, this.Id, emote, Discord, options);
        /// <inheritdoc />
        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetReactionUsersAsync(IEmote emote, int limit, RequestOptions options = null)
            => MessageHelper.GetReactionUsersAsync(this, emote, limit, Discord, options);
    }
}
