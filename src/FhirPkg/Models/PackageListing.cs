// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

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
    /// Returns the version tagged as "latest", or falls back to the highest version key.
    /// Returns <c>null</c> if there are no versions.
    /// </summary>
    [JsonIgnore]
    public string? LatestVersion
    {
        get
        {
            if (DistTags is not null && DistTags.TryGetValue("latest", out var tagged))
                return tagged;

            // Fall back to the last key (registries typically order ascending)
            return Versions.Count > 0 ? Versions.Keys.Last() : null;
        }
    }
}

/// <summary>
/// Metadata for a single published version of a package.
/// </summary>
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
