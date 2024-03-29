﻿using Newtonsoft.Json;

namespace Discord.API.Gateway
{
    internal class Reaction
    {
        [JsonProperty("user_id")]
        public ulong UserId { get; set; }
        [JsonProperty("message_id")]
        public ulong MessageId { get; set; }
        [JsonProperty("channel_id")]
        public ulong ChannelId { get; set; }
        [JsonProperty("guild_id")]
        public Optional<ulong> GuildId { get; set; }
        [JsonProperty("emoji")]
        public EmojiJson Emoji { get; set; }
        [JsonProperty("member")]
        public Optional<GuildMemberJson> Member { get; set; }
    }
}
