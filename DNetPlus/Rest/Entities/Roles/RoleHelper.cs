﻿using System;
using System.Threading.Tasks;
using Model = Discord.API.RoleJson;
using BulkParams = Discord.API.Rest.ModifyGuildRolesParams;

namespace Discord.Rest
{
    internal static class RoleHelper
    {
        //General
        public static async Task DeleteAsync(IRole role, BaseDiscordClient client,
            RequestOptions options)
        {
            await client.ApiClient.DeleteGuildRoleAsync(role.Guild.Id, role.Id, options).ConfigureAwait(false);
        }
        public static async Task<Model> ModifyAsync(IRole role, BaseDiscordClient client, 
            Action<RoleProperties> func, RequestOptions options)
        {
            RoleProperties args = new RoleProperties();
            func(args);
            API.Rest.ModifyGuildRoleParams apiArgs = new API.Rest.ModifyGuildRoleParams
            {
                Color = args.Color.IsSpecified ? args.Color.Value.RawValue : Optional.Create<uint>(),
                Hoist = args.Hoist,
                Mentionable = args.Mentionable,
                Name = args.Name,
                Permissions = args.Permissions.IsSpecified ? args.Permissions.Value.RawValue.ToString() : Optional.Create<string>()
            };
            Model model = await client.ApiClient.ModifyGuildRoleAsync(role.Guild.Id, role.Id, apiArgs, options).ConfigureAwait(false);

            if (args.Position.IsSpecified)
            {
                BulkParams[] bulkArgs = new[] { new BulkParams(role.Id, args.Position.Value) };
                await client.ApiClient.ModifyGuildRolesAsync(role.Guild.Id, bulkArgs, options).ConfigureAwait(false);
                model.Position = args.Position.Value;
            }
            return model;
        }
    }
}
