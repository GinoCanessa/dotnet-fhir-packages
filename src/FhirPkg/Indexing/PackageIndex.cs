// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FhirPkg.Indexing;

/// <summary>
/// Index of all resources in a package. Corresponds to the <c>.index.json</c> file
/// that ships inside FHIR packages (index version 2).
/// </summary>
public record PackageIndex
{
    /// <summary>Index schema version.</summary>
    [JsonPropertyName("index-version")]
    public int IndexVersion { get; init; } = 2;

    /// <summary>Date the index was generated.</summary>
    [JsonPropertyName("date")]
    public DateTime? Date { get; init; }

    /// <summary>List of indexed resource entries.</summary>
    [JsonPropertyName("files")]
    public required IReadOnlyList<ResourceIndexEntry> Files { get; init; }
}

/// <summary>
/// A single resource entry in a package index.
/// Includes common FHIR resource metadata and StructureDefinition-specific fields.
/// </summary>
public record ResourceIndexEntry
{
    /// <summary>File name relative to the package directory.</summary>
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    /// <summary>FHIR resource type (e.g. "Patient", "StructureDefinition").</summary>
    [JsonPropertyName("resourceType")]
    public required string ResourceType { get; init; }

    /// <summary>Resource logical id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Canonical URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>Resource version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Resource name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Human-readable title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    // ── StructureDefinition-specific fields ──────────────────────────────

    /// <summary>
    /// The kind of structure (e.g. "resource", "complex-type", "primitive-type", "logical").
    /// Only populated for StructureDefinition resources.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? SdKind { get; init; }

    /// <summary>
    /// The derivation type (e.g. "specialization", "constraint").
    /// Only populated for StructureDefinition resources.
    /// </summary>
    [JsonPropertyName("derivation")]
    public string? SdDerivation { get; init; }

    /// <summary>
    /// The constrained type (e.g. "Patient", "Extension").
    /// Only populated for StructureDefinition resources.
    /// </summary>
    [JsonPropertyName("type")]
    public string? SdType { get; init; }

    /// <summary>
    /// The base definition URL.
    /// Only populated for StructureDefinition resources.
    /// </summary>
    [JsonPropertyName("base")]
    public string? SdBaseDefinition { get; init; }

    /// <summary>
    /// Whether this is an abstract structure definition.
    /// Only populated for StructureDefinition resources.
    /// </summary>
    [JsonPropertyName("abstract")]
    public bool? SdAbstract { get; init; }

    /// <summary>
    /// Classified flavor of the StructureDefinition: "Profile", "Extension", "Logical",
    /// "Type", or "Resource". Computed during indexing.
    /// </summary>
    [JsonPropertyName("sdFlavor")]
    public string? SdFlavor { get; init; }

    /// <summary>
    /// Whether this resource includes a snapshot element (StructureDefinitions only).
    /// </summary>
    [JsonPropertyName("hasSnapshot")]
    public bool? HasSnapshot { get; init; }

    /// <summary>
    /// Whether this resource includes an expansion (ValueSets only).
    /// </summary>
    [JsonPropertyName("hasExpansion")]
    public bool? HasExpansion { get; init; }
}
