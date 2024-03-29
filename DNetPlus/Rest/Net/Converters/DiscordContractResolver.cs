using Discord.API;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Discord.Net.Converters
{
    public class DiscordContractResolver : DefaultContractResolver
    {
        private static readonly TypeInfo _ienumerable = typeof(IEnumerable<ulong[]>).GetTypeInfo();
        private static readonly MethodInfo _shouldSerialize = typeof(DiscordContractResolver).GetTypeInfo().GetDeclaredMethod("ShouldSerialize");    
        
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            if (property.Ignored)
                return property;

            if (member is PropertyInfo propInfo)
            {
                JsonConverter converter = GetConverter(property, propInfo, propInfo.PropertyType, 0);
                if (converter != null)
                {
                    property.Converter = converter;
                }
            }
            else
                throw new InvalidOperationException($"{member.DeclaringType.FullName}.{member.Name} is not a property.");
            return property;
        }

        private static JsonConverter GetConverter(JsonProperty property, PropertyInfo propInfo, Type type, int depth)
        {
            if (type.IsArray)
                return MakeGenericConverter(property, propInfo, typeof(ArrayConverter<>), type.GetElementType(), depth);
            if (type.IsConstructedGenericType)
            {
                Type genericType = type.GetGenericTypeDefinition();
                if (depth == 0 && genericType == typeof(Optional<>))
                {
                    Type typeInput = propInfo.DeclaringType;
                    Type innerTypeOutput = type.GenericTypeArguments[0];

                    Type getter = typeof(Func<,>).MakeGenericType(typeInput, type);
                    Delegate getterDelegate = propInfo.GetMethod.CreateDelegate(getter);
                    MethodInfo shouldSerialize = _shouldSerialize.MakeGenericMethod(typeInput, innerTypeOutput);
                    Func<object, Delegate, bool> shouldSerializeDelegate = (Func<object, Delegate, bool>)shouldSerialize.CreateDelegate(typeof(Func<object, Delegate, bool>));
                    property.ShouldSerialize = x => shouldSerializeDelegate(x, getterDelegate);

                    return MakeGenericConverter(property, propInfo, typeof(OptionalConverter<>), innerTypeOutput, depth);
                }
                else if (genericType == typeof(Nullable<>))
                    return MakeGenericConverter(property, propInfo, typeof(NullableConverter<>), type.GenericTypeArguments[0], depth);
                else if (genericType == typeof(EntityOrId<>))
                    return MakeGenericConverter(property, propInfo, typeof(UInt64EntityOrIdConverter<>), type.GenericTypeArguments[0], depth);
            }

            //Primitives
            bool hasInt53 = propInfo.GetCustomAttribute<Int53Attribute>() != null;
            if (!hasInt53)
            {
                if (type == typeof(ulong))
                    return UInt64Converter.Instance;
            }
            bool hasUnixStamp = propInfo.GetCustomAttribute<UnixTimestampAttribute>() != null;
            if (hasUnixStamp)
            {
                if (type == typeof(DateTimeOffset))
                    return UnixTimestampConverter.Instance;
            }

            //Enums
            if (type == typeof(UserStatus))
                return UserStatusConverter.Instance;
            if (type == typeof(EmbedType))
                return EmbedTypeConverter.Instance;

            //Special
            if (type == typeof(API.Image))
                return ImageConverter.Instance;

            //Entities
            TypeInfo typeInfo = type.GetTypeInfo();
            if (typeInfo.ImplementedInterfaces.Any(x => x == typeof(IEntity<ulong>)))
                return UInt64EntityConverter.Instance;
            if (typeInfo.ImplementedInterfaces.Any(x => x == typeof(IEntity<string>)))
                return StringEntityConverter.Instance;

            return null;
        }

        private static bool ShouldSerialize<TOwner, TValue>(object owner, Delegate getter)
        {
            return (getter as Func<TOwner, Optional<TValue>>)((TOwner)owner).IsSpecified;
        }

        private static JsonConverter MakeGenericConverter(JsonProperty property, PropertyInfo propInfo, Type converterType, Type innerType, int depth)
        {
            TypeInfo genericType = converterType.MakeGenericType(innerType).GetTypeInfo();
            JsonConverter innerConverter = GetConverter(property, propInfo, innerType, depth + 1);
            return genericType.DeclaredConstructors.First().Invoke(new object[] { innerConverter }) as JsonConverter;
        }
    }
}
