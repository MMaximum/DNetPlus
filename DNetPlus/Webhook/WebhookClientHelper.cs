using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord.API.Rest;
using Discord.Rest;
using ImageModel = Discord.API.Image;
using WebhookModel = Discord.API.WebhookJson;

namespace Discord.Webhook
{
    internal static class WebhookClientHelper
    {
        /// <exception cref="InvalidOperationException">Could not find a webhook with the supplied credentials.</exception>
        public static async Task<RestInternalWebhook> GetWebhookAsync(DiscordWebhookClient client, ulong webhookId)
        {
            WebhookModel model = await client.ApiClient.GetWebhookAsync(webhookId).ConfigureAwait(false);
            if (model == null)
                throw new InvalidOperationException("Could not find a webhook with the supplied credentials.");
            return RestInternalWebhook.Create(client, model);
        }
        public static async Task<ulong> SendMessageAsync(DiscordWebhookClient client, 
            string text, bool isTTS, IEnumerable<Embed> embeds, string username, string avatarUrl, RequestOptions options, AllowedMentions allowedMentions, InteractionRow[] components)
        {
            CreateWebhookMessageParams args = new CreateWebhookMessageParams(text) { 
                IsTTS = isTTS
            };
            if (embeds != null)
                args.Embeds = embeds.Select(x => x.ToModel()).ToArray();
            if (username != null)
                args.Username = username;
            if (avatarUrl != null)
                args.AvatarUrl = avatarUrl;
            if (allowedMentions != null)
                args.AllowedMentions = allowedMentions.ToModel();
            if (components != null)
                args.Components = components.Select(x => x.ToModel()).ToArray();

            API.MessageJson model = await client.ApiClient.CreateWebhookMessageAsync(client.Webhook.Id, args, options: options).ConfigureAwait(false);
            return model.Id;
        }
        public static async Task<ulong> SendFileAsync(DiscordWebhookClient client, string filePath, string text, bool isTTS, 
            IEnumerable<Embed> embeds, string username, string avatarUrl, RequestOptions options, bool isSpoiler, AllowedMentions allowedMentions, InteractionRow[] components)
        {
            string filename = Path.GetFileName(filePath);
            using (FileStream file = File.OpenRead(filePath))
                return await SendFileAsync(client, file, filename, text, isTTS, embeds, username, avatarUrl, options, isSpoiler, allowedMentions, components).ConfigureAwait(false);
        }
        public static async Task<ulong> SendFileAsync(DiscordWebhookClient client, Stream stream, string filename, string text, bool isTTS,
            IEnumerable<Embed> embeds, string username, string avatarUrl, RequestOptions options, bool isSpoiler, AllowedMentions allowedMentions, InteractionRow[] components)
        {
            UploadWebhookFileParams args = new UploadWebhookFileParams(stream) { Filename = filename, Content = text, IsTTS = isTTS, IsSpoiler = isSpoiler };
            if (username != null)
                args.Username = username;
            if (avatarUrl != null)
                args.AvatarUrl = avatarUrl;
            if (embeds != null)
                args.Embeds = embeds.Select(x => x.ToModel()).ToArray();
            if (allowedMentions != null)
                args.AllowedMentions = allowedMentions.ToModel();
            if (components != null)
                args.Components = components.Select(x => x.ToModel()).ToArray();

            API.MessageJson msg = await client.ApiClient.UploadWebhookFileAsync(client.Webhook.Id, args, options).ConfigureAwait(false);
            return msg.Id;
        }

        public static async Task<WebhookModel> ModifyAsync(DiscordWebhookClient client,
            Action<WebhookProperties> func, RequestOptions options)
        {
            WebhookProperties args = new WebhookProperties();
            func(args);
            ModifyWebhookParams apiArgs = new ModifyWebhookParams
            {
                Avatar = args.Image.IsSpecified ? args.Image.Value?.ToModel() : Optional.Create<ImageModel?>(),
                Name = args.Name
            };

            if (!apiArgs.Avatar.IsSpecified && client.Webhook.AvatarId != null)
                apiArgs.Avatar = new ImageModel(client.Webhook.AvatarId);

            return await client.ApiClient.ModifyWebhookAsync(client.Webhook.Id, apiArgs, options).ConfigureAwait(false);
        }

        public static async Task DeleteAsync(DiscordWebhookClient client, RequestOptions options)
        {
            await client.ApiClient.DeleteWebhookAsync(client.Webhook.Id, options).ConfigureAwait(false);
        }
    }
}
