using System.Text.Json.Serialization;

namespace ComicManagement.Api.Serialization;

public sealed class StringOnlyEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public StringOnlyEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
