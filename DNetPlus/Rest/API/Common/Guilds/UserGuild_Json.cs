﻿#pragma warning disable CS1591
using Newtonsoft.Json;

namespace Discord.API
{
    internal class UserGuildJson
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("icon")]
        public string Icon { get; set; }
        [JsonProperty("owner")]
        public bool Owner { get; set; }
        [JsonProperty("permissions"), Int53]
        public string Permissions { get; set; }
    }
}
