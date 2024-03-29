using Discord.API;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace Discord.Rest
{
    internal static class EntityExtensions
    {
        public static IEmote ToIEmote(this API.EmojiJson model)
        {
            if (model.Id.HasValue)
                return model.ToEntity();
            return new Emoji(model.Name);
        }

        public static API.EmojiJson ToModel(this IEmote emote)
        {
            if (emote is GuildEmote ge)
            {
                return new EmojiJson
                {
                    Id = ge.Id
                };
            }
            return new EmojiJson
            {
                Name = emote.Name
            };
        }

        public static GuildEmote ToEntity(this API.EmojiJson model)
            => new GuildEmote(model.Id.Value,
                model.Name,
                model.Animated.GetValueOrDefault(),
                model.Managed,
                model.RequireColons,
                ImmutableArray.Create(model.Roles),
                model.User.IsSpecified ? model.User.Value.Id : (ulong?)null);


        public static RoleTags ToEntity(this API.RoleTagsJson model)
        {
            return new RoleTags(
                model.BotId.IsSpecified ? model.BotId.Value : 0,
                model.IntegrationId.IsSpecified ? model.IntegrationId.Value : 0,
                model.IsPremiumSubscriber.IsSpecified ? true : false);
        }

        public static Embed ToEntity(this API.EmbedJson model)
        {
            return new Embed(model.Type, model.Title, model.Description, model.Url, model.Timestamp,
                model.Color.HasValue ? new Color(model.Color.Value) : (Color?)null,
                model.Image.IsSpecified ? model.Image.Value.ToEntity() : (EmbedImage?)null,
                model.Video.IsSpecified ? model.Video.Value.ToEntity() : (EmbedVideo?)null,
                model.Author.IsSpecified ? model.Author.Value.ToEntity() : (EmbedAuthor?)null,
                model.Footer.IsSpecified ? model.Footer.Value.ToEntity() : (EmbedFooter?)null,
                model.Provider.IsSpecified ? model.Provider.Value.ToEntity() : (EmbedProvider?)null,
                model.Thumbnail.IsSpecified ? model.Thumbnail.Value.ToEntity() : (EmbedThumbnail?)null,
                model.Fields.IsSpecified ? model.Fields.Value.Select(x => x.ToEntity()).ToImmutableArray() : ImmutableArray.Create<EmbedField>());
        }
        public static API.EmbedJson ToModel(this Embed entity, BaseDiscordClient client)
        {
            if (entity == null) return null;
            API.EmbedJson model = new API.EmbedJson
            {
                Type = entity.Type,
                Title = entity.Title,
                Description = entity.Description,
                Url = entity.Url,
                Timestamp = entity.Timestamp,
                Color = entity.Color?.RawValue
            };
            if (entity.Color.HasValue)
                model.Color = entity.Color.Value.RawValue;
            else
            {
                if (client != null && client.BaseConfig.Color.IsSpecified)
                    model.Color = client.BaseConfig.Color.Value.RawValue;
            }
            if (entity.Author != null)
                model.Author = entity.Author.Value.ToModel();
            model.Fields = entity.Fields.Select(x => x.ToModel()).ToArray();
            if (entity.Footer != null)
                model.Footer = entity.Footer.Value.ToModel();
            if (entity.Image != null)
                model.Image = entity.Image.Value.ToModel();
            if (entity.Provider != null)
                model.Provider = entity.Provider.Value.ToModel();
            if (entity.Thumbnail != null)
                model.Thumbnail = entity.Thumbnail.Value.ToModel();
            if (entity.Video != null)
                model.Video = entity.Video.Value.ToModel();
            return model;
        }

        public static MessageSticker ToEntity(this StickerJson model)
        {
            return new MessageSticker
            {
                Id = model.Id,
                Description = model.Description,
                Asset = model.Asset,
                Name = model.Name,
                PackId = model.PackId,
                Tag = model.Tag,
                Type = model.Type,
                PreviewAsset = model.PreviewAsset
            };
        }

        public static InteractionRow ToEntity(this InteractionComponent_Json model)
        {
            return new InteractionRow
            {
                Buttons = model.Components.Value.Select(x => new InteractionButton((ComponentButtonType)x.Type, x.Label.GetValueOrDefault(), x.Id.GetValueOrDefault())
                {
                    Disabled = x.Disabled.GetValueOrDefault(),
                    Emoji = x.Emoji.GetValueOrDefault()?.ToEntity(),
                    Url = x.Url.GetValueOrDefault()
                }).ToArray()
            };
        }

        public static API.AllowedMentions ToModel(this AllowedMentions entity)
        {
            return new API.AllowedMentions()
            {
                Parse = entity.AllowedTypes?.EnumerateMentionTypes().ToArray(),
                Roles = entity.RoleIds?.ToArray(),
                Users = entity.UserIds?.ToArray(),
            };
        }

        public static MessageReferenceParams ToModel(this MessageReferenceParams entity)
        {
            return new MessageReferenceParams
            {
                ChannelId = entity.ChannelId,
                MessageId = entity.MessageId
            };
        }

        public static InteractionComponent_Json ToModel(this InteractionRow row)
        {
            if (row.Dropdown == null)
            {
                return new InteractionComponent_Json
                {
                    Type = ComponentType.ActionRow,
                    Components = row.Buttons != null ? row.Buttons.Select(x => x.ToModel()).ToArray() : Optional.Create<InteractionComponent_Json[]>(),
                };
            }
            else
            {
                return new InteractionComponent_Json
                {
                    Type = ComponentType.ActionRow,
                    Components = new InteractionComponent_Json[]
                    {
                        new InteractionComponent_Json
                        {
                            Id = "test",
                            Type = ComponentType.Dropdown,
                            MaxValues = row.Dropdown.MaxValues,
                            MinValues = row.Dropdown.MinValues,
                            Placeholder = row.Dropdown.Placeholder,
                            SelectOptions = row.Dropdown.Options.Select(x => x.ToModel()).ToArray()
                        }
                    }
                    
                };
            }
        }

        public static InteractionSelectJson ToModel(this InteractionOption option)
        {
            return new InteractionSelectJson
            {
                Default = option.Default,
                Description = option.Description,
                Emoji = option.Emoji?.ToModel(),
                Label = option.Label,
                Value = option.Value
            };
        }

        public static InteractionComponent_Json ToModel(this InteractionButton component)
        {
            return new InteractionComponent_Json
            {
                Id = component.Id,
                Disabled = component.Disabled,
                Emoji = component.Emoji?.ToModel(),
                Label = component.Label,
                Style = component.Style,
                Type = ComponentType.Button,
                Url = component.Url
            };
        }

        public static IEnumerable<string> EnumerateMentionTypes(this AllowedMentionTypes mentionTypes)
        {
            if (mentionTypes.HasFlag(AllowedMentionTypes.Everyone))
                yield return "everyone";
            if (mentionTypes.HasFlag(AllowedMentionTypes.Roles))
                yield return "roles";
            if (mentionTypes.HasFlag(AllowedMentionTypes.Users))
                yield return "users";
        }
        public static EmbedAuthor ToEntity(this API.EmbedAuthorJson model)
        {
            return new EmbedAuthor(model.Name, model.Url, model.IconUrl, model.ProxyIconUrl);
        }
        public static API.EmbedAuthorJson ToModel(this EmbedAuthor entity)
        {
            return new API.EmbedAuthorJson { Name = entity.Name, Url = entity.Url, IconUrl = entity.IconUrl };
        }
        public static EmbedField ToEntity(this API.EmbedFieldJson model)
        {
            return new EmbedField(model.Name, model.Value, model.Inline);
        }
        public static API.EmbedFieldJson ToModel(this EmbedField entity)
        {
            return new API.EmbedFieldJson { Name = entity.Name, Value = entity.Value, Inline = entity.Inline };
        }
        public static EmbedFooter ToEntity(this API.EmbedFooterJson model)
        {
            return new EmbedFooter(model.Text, model.IconUrl, model.ProxyIconUrl);
        }
        public static API.EmbedFooterJson ToModel(this EmbedFooter entity)
        {
            return new API.EmbedFooterJson { Text = entity.Text, IconUrl = entity.IconUrl };
        }
        public static EmbedImage ToEntity(this API.EmbedImageJson model)
        {
            return new EmbedImage(model.Url, model.ProxyUrl,
                  model.Height.IsSpecified ? model.Height.Value : (int?)null,
                  model.Width.IsSpecified ? model.Width.Value : (int?)null);
        }
        public static API.EmbedImageJson ToModel(this EmbedImage entity)
        {
            return new API.EmbedImageJson { Url = entity.Url };
        }
        public static EmbedProvider ToEntity(this API.EmbedProviderJson model)
        {
            return new EmbedProvider(model.Name, model.Url);
        }
        public static API.EmbedProviderJson ToModel(this EmbedProvider entity)
        {
            return new API.EmbedProviderJson { Name = entity.Name, Url = entity.Url };
        }
        public static EmbedThumbnail ToEntity(this API.EmbedThumbnailJson model)
        {
            return new EmbedThumbnail(model.Url, model.ProxyUrl,
                  model.Height.IsSpecified ? model.Height.Value : (int?)null,
                  model.Width.IsSpecified ? model.Width.Value : (int?)null);
        }
        public static API.EmbedThumbnailJson ToModel(this EmbedThumbnail entity)
        {
            return new API.EmbedThumbnailJson { Url = entity.Url };
        }
        public static EmbedVideo ToEntity(this API.EmbedVideoJson model)
        {
            return new EmbedVideo(model.Url,
                  model.Height.IsSpecified ? model.Height.Value : (int?)null,
                  model.Width.IsSpecified ? model.Width.Value : (int?)null);
        }
        public static API.EmbedVideoJson ToModel(this EmbedVideo entity)
        {
            return new API.EmbedVideoJson { Url = entity.Url };
        }

        public static API.Image ToModel(this Image entity)
        {
            return new API.Image(entity.Stream);
        }

        public static Overwrite ToEntity(this API.OverwriteJson model)
        {
            return new Overwrite(model.TargetId, model.TargetType, new OverwritePermissions(model.Allow, model.Deny));
        }
    }
}
