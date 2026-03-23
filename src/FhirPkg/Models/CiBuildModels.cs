// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace FhirPkg.Models;

/// <summary>
/// Represents a record from the FHIR CI build system (build.fhir.org), describing a single
/// implementation guide build.
/// </summary>
public record CiBuildRecord
{
    /// <summary>The URL of the published CI build output.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>The display name of the implementation guide.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The title of the implementation guide.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The FHIR package identifier for this build.</summary>
    [JsonPropertyName("package-id")]
    public required string PackageId { get; init; }

    /// <summary>The implementation guide version from the build.</summary>
    [JsonPropertyName("ig-ver")]
    public string? IgVersion { get; init; }

    /// <summary>The build date as a raw string from the CI system.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    /// <summary>The build date in ISO 8601 format, if available.</summary>
    [JsonPropertyName("dateISO8601")]
    public string? DateISO8601 { get; init; }

    /// <summary>
    /// The repository path in the format "Org/RepoName/Branch/qa.json" or
    /// "Org/RepoName/branches/Branch/qa.json".
    /// </summary>
    [JsonPropertyName("repo")]
    public required string Repo { get; init; }

    /// <summary>The FHIR version targeted by this build.</summary>
    [JsonPropertyName("fhir-version")]
    public string? FhirVersion { get; init; }

    /// <summary>The number of errors reported during the build.</summary>
    [JsonPropertyName("errors")]
    public int? Errors { get; init; }

    /// <summary>The number of warnings reported during the build.</summary>
    [JsonPropertyName("warnings")]
    public int? Warnings { get; init; }

    /// <summary>
    /// Parses the <see cref="Repo"/> field to extract the organization, repository name, and branch.
    /// </summary>
    /// <returns>
    /// A tuple of (Org, RepoName, Branch) extracted from the repo path, or <c>null</c> if
    /// the repo string could not be parsed.
    /// </returns>
    /// <remarks>
    /// Supported formats:
    /// <list type="bullet">
    ///   <item><description>"HL7/US-Core/main/qa.json" → ("HL7", "US-Core", "main")</description></item>
    ///   <item><description>"HL7/US-Core/branches/R5/qa.json" → ("HL7", "US-Core", "R5")</description></item>
    /// </list>
    /// </remarks>
    public (string Org, string RepoName, string Branch)? ParseRepo()
    {
        if (string.IsNullOrWhiteSpace(Repo))
            return null;

        string[] segments = Repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            return null;

        string org = segments[0];
        string repoName = segments[1];

        // Format: "Org/Repo/branches/BranchName/qa.json"
        if (segments.Length >= 4 &&
            segments[2].Equals("branches", StringComparison.OrdinalIgnoreCase))
        {
            return (org, repoName, segments[3]);
        }

        // Format: "Org/Repo/Branch/qa.json"
        return (org, repoName, segments[2]);
    }
}

/// <summary>
/// Represents a minimal package manifest from a FHIR CI build (typically found in
/// the output/package/package.json of a CI build).
/// </summary>
public record CiBuildManifest
{
    /// <summary>The package name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The package version (often includes "current" or "current$branch").</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The build date as a raw string.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    /// <summary>The FHIR version(s) this build targets.</summary>
    [JsonPropertyName("fhirVersions")]
    public IReadOnlyList<string>? FhirVersions { get; init; }

    /// <summary>The jurisdiction code for this package.</summary>
    [JsonPropertyName("jurisdiction")]
    public string? Jurisdiction { get; init; }
}
