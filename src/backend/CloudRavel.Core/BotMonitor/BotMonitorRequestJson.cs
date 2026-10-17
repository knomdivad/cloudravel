using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudRavel.Core.BotMonitor;

/// <summary>JSON options for BotMonitor HTTP request bodies (registry, heartbeat payloads).</summary>
public static class BotMonitorRequestJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new FlexibleEnumConverterFactory());
        return options;
    }
}

public sealed class FlexibleEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(FlexibleEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class FlexibleEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var n))
                {
                    var numeric = (TEnum)Enum.ToObject(typeof(TEnum), n);
                    if (Enum.IsDefined(typeof(TEnum), numeric))
                        return numeric;
                }
                break;
            case JsonTokenType.String:
                var raw = reader.GetString();
                if (raw != null
                    && !raw.Contains(',', StringComparison.Ordinal)
                    && !IsPureIntegerString(raw)
                    && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(typeof(TEnum), parsed))
                    return parsed;
                break;
        }

        throw new JsonException($"Unable to convert JSON value to {typeof(TEnum).Name}.");
    }

    private static bool IsPureIntegerString(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length > 0
               && (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                   || long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
