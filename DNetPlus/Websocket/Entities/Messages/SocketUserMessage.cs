using Discord.API;
using Discord.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Tasks;
using Model = Discord.API.MessageJson;

namespace Discord.WebSocket
{
    /// <summary>
    ///     Represents a WebSocket-based message sent by a user.
    /// </summary>
    [DebuggerDisplay(@"{DebuggerDisplay,nq}")]
    public class SocketUserMessage : SocketMessage, IUserMessage
    {
        private bool _isMentioningEveryone, _isTTS, _isPinned, _isSuppressed;
        private long? _editedTimestampTicks;
        private ImmutableArray<Attachment> _attachments = ImmutableArray.Create<Attachment>();
        private ImmutableArray<Embed> _embeds = ImmutableArray.Create<Embed>();
        private ImmutableArray<MessageSticker> _stickers = ImmutableArray.Create<MessageSticker>();
        private ImmutableArray<ITag> _tags = ImmutableArray.Create<ITag>();
        
        /// <inheritdoc />
        public override bool IsTTS => _isTTS;
        /// <inheritdoc />
        public override bool IsPinned => _isPinned;
        /// <inheritdoc />
        public override bool IsSuppressed => _isSuppressed;
        /// <inheritdoc />
        public override DateTimeOffset? EditedTimestamp => DateTimeUtils.FromTicks(_editedTimestampTicks);
        /// <inheritdoc />
        public override IReadOnlyCollection<Attachment> Attachments => _attachments;
        /// <inheritdoc />
        public override IReadOnlyCollection<Embed> Embeds => _embeds;
        /// <inheritdoc />
        public override IReadOnlyCollection<ITag> Tags => _tags;

        public override IReadOnlyCollection<MessageSticker> Stickers => _stickers;
        /// <inheritdoc />
        public override IReadOnlyCollection<SocketGuildChannel> MentionedChannels => MessageHelper.FilterTagsByValue<SocketGuildChannel>(TagType.ChannelMention, _tags);
        /// <inheritdoc />
        public override IReadOnlyCollection<SocketRole> MentionedRoles => MessageHelper.FilterTagsByValue<SocketRole>(TagType.RoleMention, _tags);
        /// <inheritdoc />
        public override IReadOnlyCollection<SocketUser> MentionedUsers => MessageHelper.FilterTagsByValue<SocketUser>(TagType.UserMention, _tags);

        internal SocketUserMessage(DiscordSocketClient discord, ulong id, ISocketMessageChannel channel, SocketUser author, MessageSource source, string content = "")
            : base(discord, id, channel, author, source, content)
        {
        }
        internal new static SocketUserMessage Create(DiscordSocketClient discord, ClientState state, SocketUser author, ISocketMessageChannel channel, Model model)
        {
            
            SocketUserMessage entity = new SocketUserMessage(discord, model.Id, channel, author, MessageHelper.GetSource(model));
            entity.Update(state, model);
            return entity;
        }

        internal override void Update(ClientState state, Model model)
        {
            base.Update(state, model);

            if (model.IsTextToSpeech.IsSpecified)
                _isTTS = model.IsTextToSpeech.Value;
            if (model.Pinned.IsSpecified)
                _isPinned = model.Pinned.Value;
            if (model.EditedTimestamp.IsSpecified)
                _editedTimestampTicks = model.EditedTimestamp.Value?.UtcTicks;
            if (model.MentionEveryone.IsSpecified)
                _isMentioningEveryone = model.MentionEveryone.Value;
            if (model.Flags.IsSpecified)
            {
                _isSuppressed = model.Flags.Value.HasFlag(MessageFlags.SuppressEmbeds);
            }

            if (model.Attachments.IsSpecified)
            {
                API.AttachmentJson[] value = model.Attachments.Value;
                if (value.Length > 0)
                {
                    ImmutableArray<Attachment>.Builder attachments = ImmutableArray.CreateBuilder<Attachment>(value.Length);
                    for (int i = 0; i < value.Length; i++)
                        attachments.Add(Attachment.Create(value[i]));
                    _attachments = attachments.ToImmutable();
                }
                else
                    _attachments = ImmutableArray.Create<Attachment>();
            }

            if (model.Embeds.IsSpecified)
            {
                API.EmbedJson[] value = model.Embeds.Value;
                if (value.Length > 0)
                {
                    ImmutableArray<Embed>.Builder embeds = ImmutableArray.CreateBuilder<Embed>(value.Length);
                    for (int i = 0; i < value.Length; i++)
                        embeds.Add(value[i].ToEntity());
                    _embeds = embeds.ToImmutable();
                }
                else
                    _embeds = ImmutableArray.Create<Embed>();
            }

            if (model.Stickers.IsSpecified)
            {
                StickerJson[] value = model.Stickers.Value;
                if (value.Length > 0)
                {
                    ImmutableArray<MessageSticker>.Builder stickers = ImmutableArray.CreateBuilder<MessageSticker>(value.Length);
                    for (int i = 0; i < value.Length; i++)
                        stickers.Add(value[i].ToEntity());
                    _stickers = stickers.ToImmutable();
                }
                else
                    _stickers = ImmutableArray.Create<MessageSticker>();
            }

            IReadOnlyCollection<IUser> mentions = ImmutableArray.Create<SocketUnknownUser>(); //Is passed to ParseTags to get real mention collection
            if (model.UserMentions.IsSpecified)
            {
                EntityOrId<UserJson>[] value = model.UserMentions.Value;
                if (value.Length > 0)
                {
                    ImmutableArray<SocketUnknownUser>.Builder newMentions = ImmutableArray.CreateBuilder<SocketUnknownUser>(value.Length);
                    for (int i = 0; i < value.Length; i++)
                    {
                        EntityOrId<UserJson> val = value[i];
                        if (val.Object != null)
                            newMentions.Add(SocketUnknownUser.Create(Discord, state, val.Object));
                    }
                    mentions = newMentions.ToImmutable();
                }
            }

            if (model.Content.IsSpecified)
            {
                string text = model.Content.Value;
                SocketGuild guild = (Channel as SocketGuildChannel)?.Guild;
                _tags = MessageHelper.ParseTags(text, Channel, guild, mentions);
                model.Content = text;
            }
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">Only the author of a message may modify the message.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Message content is too long, length must be less or equal to <see cref="DiscordConfig.MaxMessageSize"/>.</exception>
        public Task ModifyAsync(Action<MessageProperties> func, RequestOptions options = null)
            => MessageHelper.ModifyAsync(this, Discord, func, options);

        /// <inheritdoc />
        public Task PinAsync(RequestOptions options = null)
            => MessageHelper.PinAsync(this, Discord, options);
        /// <inheritdoc />
        public Task UnpinAsync(RequestOptions options = null)
            => MessageHelper.UnpinAsync(this, Discord, options);

        public string Resolve(int startIndex, TagHandling userHandling = TagHandling.Name, TagHandling channelHandling = TagHandling.Name,
            TagHandling roleHandling = TagHandling.Name, TagHandling everyoneHandling = TagHandling.Ignore, TagHandling emojiHandling = TagHandling.Name)
            => MentionUtils.Resolve(this, startIndex, userHandling, channelHandling, roleHandling, everyoneHandling, emojiHandling);
        /// <inheritdoc />
        public string Resolve(TagHandling userHandling = TagHandling.Name, TagHandling channelHandling = TagHandling.Name,
            TagHandling roleHandling = TagHandling.Name, TagHandling everyoneHandling = TagHandling.Ignore, TagHandling emojiHandling = TagHandling.Name)
            => MentionUtils.Resolve(this, 0, userHandling, channelHandling, roleHandling, everyoneHandling, emojiHandling);

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">This operation may only be called on a <see cref="SocketNewsChannel"/> channel.</exception>
        public async Task CrosspostAsync(RequestOptions options = null)
        {
            if (!(Channel is INewsChannel))
            {
                throw new InvalidOperationException("Publishing (crossposting) is only valid in news channels.");
            }

            await MessageHelper.CrosspostAsync(this, Discord, options);
        }

        private string DebuggerDisplay => $"{Author}: {Content} ({Id}{(Attachments.Count > 0 ? $", {Attachments.Count} Attachments" : "")})";
        internal new SocketUserMessage Clone() => MemberwiseClone() as SocketUserMessage;
    }
}
