// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FhirPkg.Qualification;

internal static class QualificationCorpusHash
{
    internal const string Algorithm =
        "canonical-json-sha256-v1";

    private static readonly JsonDocumentOptions s_documentOptions =
        new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        };

    private static readonly JsonWriterOptions s_writerOptions =
        new()
        {
            Indented = false,
            SkipValidation = false
        };

    internal static async Task<string> ComputeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] json = await File.ReadAllBytesAsync(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Compute(json);
    }

    internal static string Compute(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Compute(Encoding.UTF8.GetBytes(json));
    }

    internal static string Compute(
        ReadOnlyMemory<byte> json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json, s_documentOptions);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(
            buffer,
            s_writerOptions))
        {
            WriteCanonical(
                writer,
                document.RootElement,
                "$");
            writer.Flush();
        }

        byte[] hash = SHA256.HashData(
            buffer.WrittenSpan);
        return Convert.ToHexString(hash)
            .ToLowerInvariant();
    }

    internal static void VerifyCanonicalizationRegression()
    {
        const string compact =
            """{"name":"example","items":[1,{"enabled":true}]}""";
        const string indentedLf =
            """
            {
              "name": "example",
              "items": [
                1,
                {
                  "enabled": true
                }
              ]
            }
            """;
        string indentedCrLf = indentedLf.Replace(
            "\n",
            "\r\n",
            StringComparison.Ordinal);
        string compactHash = Compute(compact);
        if (!string.Equals(
                compactHash,
                Compute(indentedLf),
                StringComparison.Ordinal)
            || !string.Equals(
                compactHash,
                Compute(indentedCrLf),
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                "Canonical corpus hashing changed across equivalent whitespace or line endings.");
        }

        const string semanticChange =
            """{"name":"example","items":[1,{"enabled":false}]}""";
        if (string.Equals(
                compactHash,
                Compute(semanticChange),
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                "Canonical corpus hashing ignored a semantic JSON change.");
        }

        const string reordered =
            """{"items":[1,{"enabled":true}],"name":"example"}""";
        if (string.Equals(
                compactHash,
                Compute(reordered),
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                "Canonical corpus hashing did not preserve property order.");
        }
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                HashSet<string> propertyNames =
                    new(StringComparer.Ordinal);
                foreach (JsonProperty property in
                    element.EnumerateObject())
                {
                    if (!propertyNames.Add(property.Name))
                    {
                        throw new JsonException(
                            $"Duplicate property '{property.Name}' at '{path}'.");
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(
                        writer,
                        property.Value,
                        $"{path}.{property.Name}");
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                int index = 0;
                foreach (JsonElement item in
                    element.EnumerateArray())
                {
                    WriteCanonical(
                        writer,
                        item,
                        $"{path}[{index}]");
                    index++;
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    element.GetRawText(),
                    skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(
                    $"Unsupported JSON token at '{path}'.");
        }
    }
}
