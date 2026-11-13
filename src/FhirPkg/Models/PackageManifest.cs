// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.IO;

namespace FhirPkg.Models;

/// <summary>
/// Represents NPM distribution metadata.
/// </summary>
/// <param name="ShaSum">The SHA-1 hash of the package tarball.</param>
/// <param name="TarballUrl">The URL to download the package tarball.</param>
public record NpmDistribution(
    [property: JsonPropertyName("shasum")] string? ShaSum,
    [property: JsonPropertyName("tarball")] string? TarballUrl)
{
    /// <summary>
    /// The Subresource Integrity value supplied by the registry, when available.
    /// </summary>
    [JsonPropertyName("integrity")]
    public string? Integrity { get; init; }
}

/// <summary>
/// Represents NPM repository metadata.
/// </summary>
/// <param name="Type">The repository type (e.g. "git").</param>
/// <param name="Url">The repository URL.</param>
/// <param name="Directory">An optional directory within the repository.</param>
public record NpmRepository(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("directory")] string? Directory);

/// <summary>
/// Represents the contents of a FHIR package.json manifest file.
/// Supports both standard NPM fields and FHIR-specific extensions.
/// </summary>
public record PackageManifest
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // ── Required fields ──────────────────────────────────────────────────

    /// <summary>The package identifier (e.g. "hl7.fhir.r4.core").</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The package version (e.g. "4.0.1").</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    // ── NPM-standard fields ─────────────────────────────────────────────

    /// <summary>A human-readable description of the package.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The SPDX license identifier (e.g. "CC0-1.0").</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// The package author: a verbatim string, null, or an object projected to
    /// <c>name &lt;email&gt; (url)</c> by a property-scoped JSON converter.
    /// Optional string/null components omit missing, null, and exactly empty values;
    /// nonempty values are not trimmed and are joined with single separator spaces.
    /// An object with no usable components projects to null.
    /// Unknown object members are ignored; other author shapes and malformed recognized
    /// members throw <see cref="JsonException"/>. Member casing follows serializer options,
    /// and the last recognized occurrence wins after every occurrence is validated.
    /// Object projection is lossy: model serialization emits a string (or normal null),
    /// not the original object. Imported manifest bytes are preserved independently.
    /// </summary>
    [JsonPropertyName("author")]
    [JsonConverter(typeof(PackageAuthorJsonConverter))]
    public string? Author { get; init; }

    /// <summary>The package homepage URL.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    /// <summary>Package dependencies as a map of package names to version constraints.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }

    /// <summary>Development-only dependencies.</summary>
    [JsonPropertyName("devDependencies")]
    public IReadOnlyDictionary<string, string>? DevDependencies { get; init; }

    /// <summary>Keywords for package discovery.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>Source code repository metadata.</summary>
    [JsonPropertyName("repository")]
    public NpmRepository? Repository { get; init; }

    /// <summary>Distribution metadata (tarball URL and checksum).</summary>
    [JsonPropertyName("dist")]
    public NpmDistribution? Distribution { get; init; }

    /// <summary>Distribution tags (e.g. "latest" → "4.0.1").</summary>
    [JsonPropertyName("dist-tags")]
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }

    // ── FHIR-specific fields ────────────────────────────────────────────

    /// <summary>The canonical URL for this implementation guide or specification.</summary>
    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

    /// <summary>The FHIR version(s) this package is compatible with (e.g. ["4.0.1"]).</summary>
    [JsonPropertyName("fhirVersions")]
    public IReadOnlyList<string>? FhirVersions { get; init; }

    /// <summary>The package type descriptor (e.g. "fhir.core", "ig").</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The publication date of this package version.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    /// <summary>The human-readable title of this package.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The jurisdiction code for this package.</summary>
    [JsonPropertyName("jurisdiction")]
    public string? Jurisdiction { get; init; }

    // ── Computed properties ──────────────────────────────────────────────

    private Version? _parsedVersion;

    /// <summary>
    /// Lazily parses the <see cref="Version"/> string into a <see cref="System.Version"/> instance.
    /// Returns <c>null</c> if the version string cannot be parsed.
    /// </summary>
    [JsonIgnore]
    public Version? SemVer => _parsedVersion ??= System.Version.TryParse(
        StripPreReleaseSuffix(Version), out Version? v) ? v : null;

    /// <summary>
    /// Infers the <see cref="FhirRelease"/> from <see cref="FhirVersions"/> or,
    /// as a fallback, from the package's dependency list.
    /// </summary>
    [JsonIgnore]
    public FhirRelease? InferredFhirRelease => InferRelease();

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Deserializes a <see cref="PackageManifest"/> from a JSON string.
    /// </summary>
    /// <param name="json">The JSON content of a package.json file.</param>
    /// <returns>A deserialized <see cref="PackageManifest"/>.</returns>
    /// <exception cref="JsonException">Thrown when the JSON is malformed or missing required fields.</exception>
    public static PackageManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<PackageManifest>(json, s_serializerOptions)
            ?? throw new JsonException("Deserialization returned null.");
    }

    /// <summary>
    /// Deserializes a <see cref="PackageManifest"/> from a UTF-8 JSON stream.
    /// </summary>
    /// <param name="stream">A stream containing the JSON content.</param>
    /// <returns>A deserialized <see cref="PackageManifest"/>.</returns>
    /// <exception cref="JsonException">Thrown when the JSON is malformed or missing required fields.</exception>
    public static PackageManifest Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return DeserializeCore(FileSystemContext.ProtectRead(stream, context: null));
    }

    internal static PackageManifest Deserialize(Stream stream, FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return DeserializeCore(context.ProtectRead(stream));
    }

    private static PackageManifest DeserializeCore(Stream stream) =>
        JsonSerializer.Deserialize<PackageManifest>(stream, s_serializerOptions)
            ?? throw new JsonException("Deserialization returned null.");

    /// <summary>
    /// Serializes this manifest to a JSON string.
    /// </summary>
    /// <returns>A formatted JSON string representing this manifest.</returns>
    public string Serialize() => JsonSerializer.Serialize(this, s_serializerOptions);

    // ── Private helpers ─────────────────────────────────────────────────

    private FhirRelease? InferRelease()
    {
        // Try fhirVersions first
        if (FhirVersions is { Count: > 0 })
        {
            FhirRelease? release = FhirReleaseMapping.FromVersionString(FhirVersions[0]);
            if (release is not null) return release;
        }

        // Fall back to dependencies
        if (Dependencies is not null)
        {
            foreach ((string? name, string _) in Dependencies)
            {
                FhirRelease? release = FhirReleaseMapping.FromPackageName(name);
                if (release is not null) return release;
            }
        }

        return null;
    }

    private static string StripPreReleaseSuffix(string version)
    {
        int dashIndex = version.IndexOf('-');
        return dashIndex >= 0 ? version[..dashIndex] : version;
    }
}

internal sealed class PackageAuthorJsonConverter : JsonConverter<string>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Package author must be a string, object, or null.");
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        StringComparison comparison = options.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? name = null;
        string? email = null;
        string? url = null;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            bool isName = property.Name.Equals("name", comparison);
            bool isEmail = property.Name.Equals("email", comparison);
            bool isUrl = property.Name.Equals("url", comparison);
            if (!isName && !isEmail && !isUrl)
            {
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonException($"Package author member '{property.Name}' must be a string or null.");
            }

            string? value = property.Value.GetString();
            if (isName)
            {
                name = value;
            }
            else if (isEmail)
            {
                email = value;
            }
            else
            {
                url = value;
            }
        }

        List<string> components = [];
        if (!string.IsNullOrEmpty(name))
        {
            components.Add(name);
        }

        if (!string.IsNullOrEmpty(email))
        {
            components.Add($"<{email}>");
        }

        if (!string.IsNullOrEmpty(url))
        {
            components.Add($"({url})");
        }

        return components.Count == 0 ? null : string.Join(" ", components);
    }

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
