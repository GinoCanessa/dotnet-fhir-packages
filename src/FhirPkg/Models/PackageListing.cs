// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Registry;

namespace FhirPkg.Models;

/// <summary>
/// Represents the full listing for a package from a registry, including all published versions.
/// </summary>
public record PackageListing
{
    /// <summary>The package identifier.</summary>
    [JsonPropertyName("name")]
    public required string PackageId { get; init; }

    /// <summary>A human-readable description of the package.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Distribution tags (e.g. "latest" → "4.0.1").</summary>
    [JsonPropertyName("dist-tags")]
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }

    /// <summary>All published versions, keyed by version string.</summary>
    [JsonPropertyName("versions")]
    public required IReadOnlyDictionary<string, PackageVersionInfo> Versions { get; init; }

    /// <summary>
    /// Gets credential-free registry-origin provenance when the listing represents one source.
    /// </summary>
    [JsonIgnore]
    public RegistryEndpoint? SourceRegistry { get; init; }

    /// <summary>
    /// Gets whether every eligible registry was queried successfully.
    /// </summary>
    [JsonIgnore]
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// Gets sanitized failures from registry queries that prevented a complete listing.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<RegistryAttemptFailure> QueryFailures { get; init; } = [];

    /// <summary>
    /// Gets complete, source-specific version records in configured source-priority order.
    /// </summary>
    /// <remarks>
    /// Each candidate is kept intact so artifact, integrity, dependency, and FHIR metadata
    /// are never combined across registries.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<PackageVersionInfo> VersionCandidates { get; init; } = [];

    /// <summary>
    /// Returns the version tagged as "latest", or falls back to the highest version key
    /// using semantic version comparison. Returns <c>null</c> if there are no versions.
    /// </summary>
    [JsonIgnore]
    public string? LatestVersion
    {
        get
        {
            if (DistTags is not null && DistTags.TryGetValue("latest", out string? tagged))
                return tagged;

            // Parse version keys and pick the highest using semantic version ordering
            return Versions.Keys
                .Select(k => FhirSemVer.TryParse(k, out FhirSemVer? v) ? v : null)
                .Where(v => v is not null)
                .Max()
                ?.ToString();
        }
    }
}

/// <summary>
/// Metadata for a single published version of a package.
/// </summary>
[JsonConverter(typeof(PackageVersionInfoJsonConverter))]
public record PackageVersionInfo
{
    /// <summary>The package name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The version string.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>A human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The FHIR version this package version targets (e.g. "4.0.1").</summary>
    [JsonPropertyName("fhirVersion")]
    public string? FhirVersion { get; init; }

    /// <summary>
    /// All explicitly declared FHIR versions from either the singular or plural registry field.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? FhirVersions { get; init; }

    [JsonIgnore]
    internal bool HasExplicitFhirVersionMetadata { get; init; }

    /// <summary>Distribution metadata (tarball URL and checksum).</summary>
    [JsonPropertyName("dist")]
    public NpmDistribution? Distribution { get; init; }

    /// <summary>The canonical URL for this implementation guide version.</summary>
    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

    /// <summary>The package kind (e.g. "fhir.core", "ig").</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The publication date.</summary>
    [JsonPropertyName("date")]
    public string? PublicationDate { get; init; }

    /// <summary>The number of FHIR resources in this package version.</summary>
    [JsonPropertyName("count")]
    public int? ResourceCount { get; init; }

    /// <summary>The SPDX license identifier.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>A URL for more information about this package version.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Dependencies of this package version.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }

    /// <summary>
    /// Gets credential-free registry-origin provenance for this complete version record.
    /// </summary>
    [JsonIgnore]
    public RegistryEndpoint? SourceRegistry { get; init; }

    [JsonIgnore]
    internal IRegistryClient? SourceClient { get; init; }

    /// <summary>Gets whether this version was declared as latest by its source registry.</summary>
    [JsonIgnore]
    public bool IsSourceLatest { get; init; }
}

internal sealed class PackageVersionInfoJsonConverter : JsonConverter<PackageVersionInfo>
{
    public override PackageVersionInfo Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        List<string> fhirVersions = [];
        bool hasExplicitFhirVersionMetadata =
            TryGetProperty(root, "fhirVersion", out _)
            || TryGetProperty(root, "fhirVersions", out _);
        ReadStringValues(root, "fhirVersion", fhirVersions);
        ReadStringValues(root, "fhirVersions", fhirVersions);
        IReadOnlyList<string>? explicitVersions = fhirVersions.Count == 0
            ? null
            : fhirVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new PackageVersionInfo
        {
            Name = GetRequiredString(root, "name"),
            Version = GetRequiredString(root, "version"),
            Description = GetString(root, "description"),
            FhirVersion = explicitVersions?.FirstOrDefault(),
            FhirVersions = explicitVersions,
            HasExplicitFhirVersionMetadata = hasExplicitFhirVersionMetadata,
            Distribution = Deserialize<NpmDistribution>(root, "dist", options),
            Canonical = GetString(root, "canonical"),
            Kind = GetString(root, "kind"),
            PublicationDate = GetString(root, "date"),
            ResourceCount = GetInt32(root, "count", options),
            License = GetString(root, "license"),
            Url = GetString(root, "url"),
            Dependencies = Deserialize<IReadOnlyDictionary<string, string>>(
                root,
                "dependencies",
                options),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        PackageVersionInfo value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("version", value.Version);
        WriteString(writer, "description", value.Description);

        IReadOnlyList<string>? fhirVersions = value.FhirVersions;
        bool hasExplicitFhirVersionMetadata =
            value.HasExplicitFhirVersionMetadata
            || value.FhirVersion is not null
            || fhirVersions is not null;
        if (hasExplicitFhirVersionMetadata && fhirVersions is not { Count: > 0 }
            && value.FhirVersion is null)
        {
            writer.WritePropertyName("fhirVersions");
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
        else if (fhirVersions is { Count: > 1 })
        {
            writer.WritePropertyName("fhirVersion");
            JsonSerializer.Serialize(writer, fhirVersions, options);
        }
        else
        {
            WriteString(writer, "fhirVersion", fhirVersions?.FirstOrDefault() ?? value.FhirVersion);
        }

        WriteValue(writer, "dist", value.Distribution, options);
        WriteString(writer, "canonical", value.Canonical);
        WriteString(writer, "kind", value.Kind);
        WriteString(writer, "date", value.PublicationDate);
        if (value.ResourceCount is int resourceCount)
        {
            writer.WriteNumber("count", resourceCount);
        }

        WriteString(writer, "license", value.License);
        WriteString(writer, "url", value.Url);
        WriteValue(writer, "dependencies", value.Dependencies, options);
        writer.WriteEndObject();
    }

    private static string GetRequiredString(JsonElement root, string propertyName) =>
        GetString(root, propertyName)
        ?? throw new JsonException($"Required property '{propertyName}' was missing.");

    private static string? GetString(JsonElement root, string propertyName) =>
        TryGetProperty(root, propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions options)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.Deserialize<int?>(options);
    }

    private static T? Deserialize<T>(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions options)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return value.Deserialize<T>(options);
    }

    private static void ReadStringValues(
        JsonElement root,
        string propertyName,
        List<string> destination)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String
            && value.GetString() is string scalar
            && !string.IsNullOrWhiteSpace(scalar))
        {
            destination.Add(scalar);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is string entry
                && !string.IsNullOrWhiteSpace(entry))
            {
                destination.Add(entry);
            }
        }
    }

    private static bool TryGetProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteValue<T>(
        Utf8JsonWriter writer,
        string propertyName,
        T? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>
/// A catalog entry representing a package in a search result or registry listing.
/// This is a simplified view compared to <see cref="PackageVersionInfo"/>.
/// </summary>
public record CatalogEntry
{
    /// <summary>The package name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>A human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The FHIR version this entry targets.</summary>
    [JsonPropertyName("fhirVersion")]
    public string? FhirVersion { get; init; }

    /// <summary>The version string.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The canonical URL.</summary>
    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

    /// <summary>The package kind (e.g. "fhir.core", "ig").</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The publication date.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    /// <summary>A URL for more information.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>The number of FHIR resources in this package.</summary>
    [JsonPropertyName("count")]
    public int? ResourceCount { get; init; }
}
