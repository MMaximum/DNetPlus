﻿#pragma warning disable CS1591
using Newtonsoft.Json;
using System.Runtime.CompilerServices;

namespace Discord.API
{
    internal class RoleJson
    {
        [JsonProperty("id")]
        public ulong Id { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("color")]
        public uint Color { get; set; }
        [JsonProperty("hoist")]
        public bool Hoist { get; set; }
        [JsonProperty("mentionable")]
        public bool Mentionable { get; set; }
        [JsonProperty("position")]
        public int Position { get; set; }
        [JsonProperty("permissions"), Int53]
        public string Permissions { get; set; }
        [JsonProperty("managed")]
        public bool Managed { get; set; }
        [JsonProperty("tags")]
        public Optional<RoleTagsJson> Tags { get; set; }
    }
}
