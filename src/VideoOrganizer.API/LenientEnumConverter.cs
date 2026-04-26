using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoOrganizer.API;

/// <summary>
/// Factory that creates LenientEnumConverter instances for any enum type.
/// This ensures all enums are handled with case-insensitive parsing.
/// </summary>
public class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

/// <summary>
/// Custom JSON converter for enums that allows case-insensitive parsing
/// and handles numeric values gracefully, including undefined enum values
/// </summary>
public class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Try to read as string first
        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (string.IsNullOrEmpty(stringValue))
            {
                return default;
            }

            // Try case-insensitive parsing
            if (Enum.TryParse<TEnum>(stringValue, ignoreCase: true, out var result))
            {
                return result;
            }

            // Return default if parsing fails
            return default;
        }

        // Try to read as number
        if (reader.TokenType == JsonTokenType.Number)
        {
            var numValue = reader.GetInt32();
            // Allow any numeric value, even if not defined in enum
            if (Enum.IsDefined(typeof(TEnum), numValue))
            {
                return (TEnum)(object)numValue;
            }
            // For undefined values, try to cast anyway (will create the enum with that value)
            try
            {
                return (TEnum)(object)numValue;
            }
            catch
            {
                return default;
            }
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        try
        {
            // Check if the enum value is defined
            if (Enum.IsDefined(typeof(TEnum), value))
            {
                var enumName = value.ToString();
                if (!string.IsNullOrEmpty(enumName))
                {
                    writer.WriteStringValue(ToWireCamelCase(enumName));
                }
                else
                {
                    // If ToString() returns empty, write the numeric value
                    writer.WriteNumberValue(Convert.ToInt32(value));
                }
            }
            else
            {
                // For undefined enum values, write as number
                writer.WriteNumberValue(Convert.ToInt32(value));
            }
        }
        catch
        {
            // Fallback: write as number
            writer.WriteNumberValue(Convert.ToInt32(value));
        }
    }

    // Maps PascalCase enum names to camelCase wire values. Handles leading all-caps
    // prefixes correctly: "HEVC" -> "hevc", "UHD8k" -> "uhd8k", "HD1080p" -> "hd1080p",
    // "XMLParser" -> "xmlParser", "CellPhone" -> "cellPhone".
    public static string ToWireCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0])) return name;

        int n = 0;
        while (n < name.Length && char.IsUpper(name[n])) n++;

        if (n == 1) return char.ToLowerInvariant(name[0]) + name.Substring(1);
        if (n == name.Length) return name.ToLowerInvariant();

        // n >= 2, and name[n] exists.
        // If name[n] is a letter (i.e. lowercase — we already exited the upper run),
        // the last upper in the run starts the next word, so preserve it.
        // Otherwise (digit, underscore, etc.) lowercase the entire upper run.
        var chars = name.ToCharArray();
        int lowerUntil = char.IsLetter(name[n]) ? n - 1 : n;
        for (int i = 0; i < lowerUntil; i++) chars[i] = char.ToLowerInvariant(chars[i]);
        return new string(chars);
    }
}
