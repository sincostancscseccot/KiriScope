using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Filters.BuiltIn;

/// <summary>A loaded, concrete built-in filter scheme and the JSON file that supplied it.</summary>
public sealed record BuiltInContentFilterScheme(
    ContentFilterSchemeDescriptor Descriptor,
    IContentFilter Filter,
    string SourcePath);

/// <summary>
/// Loads portable built-in filter scheme JSON. A scheme identifies an algorithm, one parameter set,
/// and the evidence source for those parameters; it never identifies a title merely by filename.
/// </summary>
public static class BuiltInContentFilterSchemeLoader
{
    public static BuiltInContentFilterScheme Load(string schemePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemePath);
        var fullPath = Path.GetFullPath(schemePath);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(fullPath));
        }
        catch (JsonException exception)
        {
            throw Failure("FILTER_SCHEME_JSON_INVALID", $"Scheme JSON could not be parsed: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Failure("FILTER_SCHEME_ROOT_INVALID", "A filter scheme JSON document must have an object at its root.");
            }

            var schemeId = ReadRequiredString(root, "id");
            var displayName = ReadRequiredString(root, "displayName");
            var algorithmId = ReadRequiredString(root, "algorithmId");
            var algorithmVersion = ReadRequiredString(root, "algorithmVersion");
            var source = ReadSource(root);
            var parameters = ReadRequiredObject(root, "parameters");
            IContentFilter filter;
            try
            {
                filter = CreateFilter(algorithmId, parameters);
            }
            catch (ArgumentException exception)
            {
                throw Failure("FILTER_SCHEME_PARAMETERS_INVALID", exception.Message);
            }
            if (!string.Equals(filter.Descriptor.Id, algorithmId, StringComparison.Ordinal) ||
                !string.Equals(filter.Descriptor.Version, algorithmVersion, StringComparison.Ordinal))
            {
                throw Failure(
                    "FILTER_SCHEME_ALGORITHM_VERSION_UNSUPPORTED",
                    $"Scheme requests '{algorithmId}' version '{algorithmVersion}', but the available implementation is '{filter.Descriptor.Id}' version '{filter.Descriptor.Version}'.");
            }

            return new BuiltInContentFilterScheme(
                new ContentFilterSchemeDescriptor(schemeId, displayName, algorithmId, algorithmVersion, source),
                filter,
                fullPath);
        }
    }

    private static IContentFilter CreateFilter(string algorithmId, JsonElement parameters)
    {
        if (string.Equals(algorithmId, "builtin.repeating-xor", StringComparison.Ordinal))
        {
            var keyHex = ReadRequiredString(parameters, "keyHex");
            try
            {
                var key = Convert.FromHexString(keyHex);
                return new RepeatingXorContentFilter(key);
            }
            catch (FormatException)
            {
                throw Failure("FILTER_SCHEME_XOR_KEY_INVALID", "The repeating XOR keyHex value must be an even-length hexadecimal string.");
            }
            catch (ArgumentException)
            {
                throw Failure("FILTER_SCHEME_XOR_KEY_INVALID", "The repeating XOR keyHex value must not be empty.");
            }
        }

        if (string.Equals(algorithmId, "builtin.cx-encryption", StringComparison.Ordinal))
        {
            var randomFamilyName = parameters.TryGetProperty("randomFamily", out var randomFamilyValue)
                ? randomFamilyValue.ValueKind == JsonValueKind.String
                    ? randomFamilyValue.GetString()
                    : throw Failure("FILTER_SCHEME_CX_RANDOM_FAMILY_INVALID", "CxEncryption randomFamily must be a string when specified.")
                : null;
            var randomFamily = randomFamilyName switch
            {
                null or "standard" => CxRandomFamily.Standard,
                "nana" => CxRandomFamily.Nana,
                _ => throw Failure("FILTER_SCHEME_CX_RANDOM_FAMILY_INVALID", "CxEncryption randomFamily must be 'standard' or 'nana'."),
            };
            return new CxContentFilter(new CxSchemeConfiguration(
                ReadUInt32(parameters, "mask"),
                ReadUInt32(parameters, "offset"),
                ReadByteArray(parameters, "prologOrder"),
                ReadByteArray(parameters, "oddBranchOrder"),
                ReadByteArray(parameters, "evenBranchOrder"),
                ReadControlBlock(parameters),
                randomFamily,
                ReadOptionalUInt32(parameters, "randomSeed")));
        }

        throw Failure("FILTER_SCHEME_ALGORITHM_UNKNOWN", $"The built-in filter algorithm '{algorithmId}' is not available.");
    }

    private static ContentFilterParameterSource ReadSource(JsonElement root)
    {
        var source = ReadRequiredObject(root, "parameterSource");
        return new ContentFilterParameterSource(
            ReadRequiredString(source, "kind"),
            ReadRequiredString(source, "reference"),
            ReadOptionalString(source, "notes"));
    }

    private static JsonElement ReadRequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw Failure("FILTER_SCHEME_PROPERTY_INVALID", $"Scheme property '{propertyName}' must be an object.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement parent, string propertyName)
    {
        var value = ReadOptionalString(parent, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure("FILTER_SCHEME_PROPERTY_INVALID", $"Scheme property '{propertyName}' must be a non-empty string.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static uint ReadUInt32(JsonElement parent, string propertyName)
    {
        var value = ReadProperty(parent, propertyName);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String && TryParseUInt32(value.GetString(), out numericValue))
        {
            return numericValue;
        }

        throw Failure("FILTER_SCHEME_UINT32_INVALID", $"Scheme property '{propertyName}' must be an unsigned 32-bit integer or a hexadecimal string.");
    }

    private static uint? ReadOptionalUInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out _) ? ReadUInt32(parent, propertyName) : null;

    private static byte[] ReadByteArray(JsonElement parent, string propertyName)
    {
        var value = ReadProperty(parent, propertyName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Failure("FILTER_SCHEME_BYTE_ARRAY_INVALID", $"Scheme property '{propertyName}' must be an array of bytes.");
        }

        var result = new byte[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetByte(out result[index]))
            {
                throw Failure("FILTER_SCHEME_BYTE_ARRAY_INVALID", $"Scheme property '{propertyName}' must contain only byte values.");
            }

            index++;
        }

        return result;
    }

    private static uint[] ReadControlBlock(JsonElement parent)
    {
        if (!parent.TryGetProperty("controlBlock", out var value))
        {
            throw Failure("FILTER_SCHEME_CX_CONTROL_BLOCK_MISSING", "CxEncryption requires a controlBlock array or hexadecimal string.");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new uint[value.GetArrayLength()];
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var numericValue))
                {
                    result[index++] = numericValue;
                    continue;
                }

                if (item.ValueKind == JsonValueKind.String && TryParseUInt32(item.GetString(), out numericValue))
                {
                    result[index++] = numericValue;
                    continue;
                }

                throw Failure("FILTER_SCHEME_CX_CONTROL_BLOCK_INVALID", "CxEncryption controlBlock values must be unsigned 32-bit integers or hexadecimal strings.");
            }

            return result;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var hex = value.GetString();
            try
            {
                var bytes = Convert.FromHexString(hex ?? string.Empty);
                if (bytes.Length != 0x400 * sizeof(uint))
                {
                    throw Failure("FILTER_SCHEME_CX_CONTROL_BLOCK_INVALID", "CxEncryption hexadecimal controlBlock must contain exactly 4096 bytes.");
                }

                var result = new uint[0x400];
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint), sizeof(uint)));
                }

                return result;
            }
            catch (FormatException)
            {
                throw Failure("FILTER_SCHEME_CX_CONTROL_BLOCK_INVALID", "CxEncryption hexadecimal controlBlock must be an even-length hexadecimal string.");
            }
        }

        throw Failure("FILTER_SCHEME_CX_CONTROL_BLOCK_INVALID", "CxEncryption controlBlock must be an array or a hexadecimal string.");
    }

    private static JsonElement ReadProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw Failure("FILTER_SCHEME_PROPERTY_MISSING", $"Scheme property '{propertyName}' is required.");
        }

        return value;
    }

    private static bool TryParseUInt32(string? value, out uint parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = default;
            return false;
        }

        var numberStyles = NumberStyles.Integer;
        var number = value;
        if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            numberStyles = NumberStyles.AllowHexSpecifier;
            number = number[2..];
        }

        return uint.TryParse(number, numberStyles, CultureInfo.InvariantCulture, out parsed);
    }

    private static ContentFilterException Failure(string code, string message) => new(code, message);
}
