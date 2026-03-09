// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace FhirPkg.Models;

/// <summary>
/// Criteria for searching a FHIR package registry.
/// All properties are optional filters; unset properties are not included in the query.
/// </summary>
public record PackageSearchCriteria
{
    /// <summary>Filter by package name (partial match).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Filter by the package canonical URL.</summary>
    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

    /// <summary>Filter by the canonical URL of a package (alternative field name used by some registries).</summary>
    [JsonPropertyName("pkgcanonical")]
    public string? PackageCanonical { get; init; }

    /// <summary>Filter by FHIR version (e.g. "4.0.1" or "R4").</summary>
    [JsonPropertyName("fhirversion")]
    public string? FhirVersion { get; init; }

    /// <summary>Filter to packages that depend on a specific package.</summary>
    [JsonPropertyName("dependency")]
    public string? Dependency { get; init; }

    /// <summary>Sort order for results (e.g. "date", "name", "relevance").</summary>
    [JsonPropertyName("sort")]
    public string? Sort { get; init; }
}

/// <summary>
/// Reports progress during a package installation operation.
/// </summary>
public record PackageProgress
{
    /// <summary>The identifier of the package being installed.</summary>
    public required string PackageId { get; init; }

    /// <summary>The current phase of the installation process.</summary>
    public required PackageProgressPhase Phase { get; init; }

    /// <summary>The completion percentage (0–100), if deterministic progress is available.</summary>
    public double? PercentComplete { get; init; }

    /// <summary>The number of bytes downloaded so far.</summary>
    public long? BytesDownloaded { get; init; }

    /// <summary>The total expected download size in bytes, if known.</summary>
    public long? TotalBytes { get; init; }
}
