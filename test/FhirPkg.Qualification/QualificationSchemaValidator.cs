// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirPkg.Qualification;

internal static class QualificationSchemaPaths
{
    internal static string Corpus => Path.Combine(
        AppContext.BaseDirectory,
        "Schemas",
        "qualification-corpus.schema.json");

    internal static string Report => Path.Combine(
        AppContext.BaseDirectory,
        "Schemas",
        "qualification-report.schema.json");
}

internal static partial class QualificationSchemaValidator
{
    private const string SupportedDialect =
        "https://json-schema.org/draft/2020-12/schema";

    internal static async Task ValidateFileAsync(
        string instancePath,
        string schemaPath,
        CancellationToken cancellationToken)
    {
        string instanceJson = await File.ReadAllTextAsync(
                instancePath,
                cancellationToken)
            .ConfigureAwait(false);
        string schemaJson = await File.ReadAllTextAsync(
                schemaPath,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateDocuments(instanceJson, schemaJson);
    }

    internal static void ValidateJson(
        string instanceJson,
        string schemaPath)
    {
        string schemaJson = File.ReadAllText(schemaPath);
        ValidateDocuments(instanceJson, schemaJson);
    }

    private static void ValidateDocuments(
        string instanceJson,
        string schemaJson)
    {
        using JsonDocument schema =
            JsonDocument.Parse(schemaJson);
        using JsonDocument instance =
            JsonDocument.Parse(instanceJson);
        ValidateSchemaDocument(schema.RootElement);
        ValidateElement(
            instance.RootElement,
            schema.RootElement,
            schema.RootElement,
            "$");
    }

    private static void ValidateSchemaDocument(
        JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty(
                "$schema",
                out JsonElement dialect)
            || !string.Equals(
                dialect.GetString(),
                SupportedDialect,
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                "Qualification JSON schema does not use the supported draft.");
        }
    }

    private static void ValidateElement(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path)
    {
        if (schema.TryGetProperty(
                "$ref",
                out JsonElement reference))
        {
            JsonElement referenced = ResolveReference(
                rootSchema,
                reference.GetString());
            ValidateElement(
                instance,
                referenced,
                rootSchema,
                path);
            return;
        }

        ValidateType(instance, schema, path);
        ValidateConst(instance, schema, path);
        ValidateEnum(instance, schema, path);
        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(
                    instance,
                    schema,
                    rootSchema,
                    path);
                break;
            case JsonValueKind.Array:
                ValidateArray(
                    instance,
                    schema,
                    rootSchema,
                    path);
                break;
            case JsonValueKind.String:
                ValidateString(instance, schema, path);
                break;
            case JsonValueKind.Number:
                ValidateNumber(instance, schema, path);
                break;
        }
    }

    private static void ValidateType(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        if (!schema.TryGetProperty(
                "type",
                out JsonElement type))
        {
            return;
        }

        bool matches = type.ValueKind switch
        {
            JsonValueKind.String =>
                MatchesType(instance, type.GetString()),
            JsonValueKind.Array => type
                .EnumerateArray()
                .Any(candidate => MatchesType(
                    instance,
                    candidate.GetString())),
            _ => false
        };
        if (!matches)
        {
            throw new QualificationInvariantException(
                $"JSON value at '{path}' does not match its schema type.");
        }
    }

    private static bool MatchesType(
        JsonElement instance,
        string? type) =>
        type switch
        {
            "array" => instance.ValueKind
                == JsonValueKind.Array,
            "boolean" => instance.ValueKind is
                JsonValueKind.True or JsonValueKind.False,
            "integer" => instance.ValueKind
                    == JsonValueKind.Number
                && instance.TryGetInt64(out long _),
            "null" => instance.ValueKind
                == JsonValueKind.Null,
            "number" => instance.ValueKind
                == JsonValueKind.Number,
            "object" => instance.ValueKind
                == JsonValueKind.Object,
            "string" => instance.ValueKind
                == JsonValueKind.String,
            _ => false
        };

    private static void ValidateConst(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        if (schema.TryGetProperty(
                "const",
                out JsonElement expected)
            && !JsonEquals(instance, expected))
        {
            throw new QualificationInvariantException(
                $"JSON value at '{path}' does not match its required constant.");
        }
    }

    private static void ValidateEnum(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        if (schema.TryGetProperty(
                "enum",
                out JsonElement values)
            && !values.EnumerateArray().Any(
                value => JsonEquals(instance, value)))
        {
            throw new QualificationInvariantException(
                $"JSON value at '{path}' is not an allowed value.");
        }
    }

    private static bool JsonEquals(
        JsonElement left,
        JsonElement right) =>
        left.ValueKind == right.ValueKind
        && string.Equals(
            left.GetRawText(),
            right.GetRawText(),
            StringComparison.Ordinal);

    private static void ValidateObject(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path)
    {
        if (schema.TryGetProperty(
                "required",
                out JsonElement required))
        {
            foreach (JsonElement requiredName in
                required.EnumerateArray())
            {
                string name = requiredName.GetString()
                    ?? string.Empty;
                if (!instance.TryGetProperty(
                        name,
                        out JsonElement _))
                {
                    throw new QualificationInvariantException(
                        $"JSON object at '{path}' is missing required property '{name}'.");
                }
            }
        }

        bool rejectAdditional =
            schema.TryGetProperty(
                "additionalProperties",
                out JsonElement additional)
            && additional.ValueKind
                == JsonValueKind.False;
        schema.TryGetProperty(
            "properties",
            out JsonElement properties);
        foreach (JsonProperty property in
            instance.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(
                    property.Name,
                    out JsonElement propertySchema))
            {
                ValidateElement(
                    property.Value,
                    propertySchema,
                    rootSchema,
                    $"{path}.{property.Name}");
                continue;
            }

            if (rejectAdditional)
            {
                throw new QualificationInvariantException(
                    $"JSON object at '{path}' contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static void ValidateArray(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path)
    {
        int count = instance.GetArrayLength();
        if (schema.TryGetProperty(
                "minItems",
                out JsonElement minimum)
            && count < minimum.GetInt32())
        {
            throw new QualificationInvariantException(
                $"JSON array at '{path}' has too few items.");
        }

        if (schema.TryGetProperty(
                "maxItems",
                out JsonElement maximum)
            && count > maximum.GetInt32())
        {
            throw new QualificationInvariantException(
                $"JSON array at '{path}' has too many items.");
        }

        if (schema.TryGetProperty(
                "uniqueItems",
                out JsonElement unique)
            && unique.ValueKind == JsonValueKind.True)
        {
            HashSet<string> values =
                new(StringComparer.Ordinal);
            foreach (JsonElement value in
                instance.EnumerateArray())
            {
                if (!values.Add(value.GetRawText()))
                {
                    throw new QualificationInvariantException(
                        $"JSON array at '{path}' contains duplicate items.");
                }
            }
        }

        if (!schema.TryGetProperty(
                "items",
                out JsonElement itemSchema))
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in
            instance.EnumerateArray())
        {
            ValidateElement(
                item,
                itemSchema,
                rootSchema,
                $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]");
            index++;
        }
    }

    private static void ValidateString(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        string value = instance.GetString()
            ?? string.Empty;
        if (schema.TryGetProperty(
                "minLength",
                out JsonElement minimum)
            && value.Length < minimum.GetInt32())
        {
            throw new QualificationInvariantException(
                $"JSON string at '{path}' is too short.");
        }

        if (schema.TryGetProperty(
                "pattern",
                out JsonElement pattern)
            && !Regex.IsMatch(
                value,
                pattern.GetString() ?? string.Empty,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
        {
            throw new QualificationInvariantException(
                $"JSON string at '{path}' does not match its required pattern.");
        }

        if (!schema.TryGetProperty(
                "format",
                out JsonElement format))
        {
            return;
        }

        bool valid = format.GetString() switch
        {
            "date-time" => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset _),
            "uri" => Uri.TryCreate(
                value,
                UriKind.Absolute,
                out Uri? _),
            _ => true
        };
        if (!valid)
        {
            throw new QualificationInvariantException(
                $"JSON string at '{path}' does not match its required format.");
        }
    }

    private static void ValidateNumber(
        JsonElement instance,
        JsonElement schema,
        string path)
    {
        if (schema.TryGetProperty(
                "minimum",
                out JsonElement minimum)
            && instance.GetDecimal()
                < minimum.GetDecimal())
        {
            throw new QualificationInvariantException(
                $"JSON number at '{path}' is below its minimum.");
        }
    }

    private static JsonElement ResolveReference(
        JsonElement rootSchema,
        string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith(
                "#/",
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                "Qualification JSON schema contains an unsupported reference.");
        }

        JsonElement current = rootSchema;
        foreach (string encodedSegment in reference[2..]
            .Split('/'))
        {
            string segment = encodedSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(
                    segment,
                    out current))
            {
                throw new QualificationInvariantException(
                    $"Qualification JSON schema reference '{reference}' was not found.");
            }
        }

        return current;
    }
}
