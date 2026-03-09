// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Indexing;

/// <summary>
/// A lightweight resource descriptor aggregated across one or more indexed packages.
/// Used as the result type for <see cref="IPackageIndexer"/> search operations.
/// </summary>
public record ResourceInfo
{
    /// <summary>The FHIR resource type (e.g. "StructureDefinition", "ValueSet").</summary>
    public required string ResourceType { get; init; }

    /// <summary>The resource logical id.</summary>
    public string? Id { get; init; }

    /// <summary>The canonical URL, if the resource has one.</summary>
    public string? Url { get; init; }

    /// <summary>The resource name.</summary>
    public string? Name { get; init; }

    /// <summary>The resource business version.</summary>
    public string? Version { get; init; }

    /// <summary>The name of the package that contains this resource.</summary>
    public string? PackageName { get; init; }

    /// <summary>The version of the package that contains this resource.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>The file path of this resource within the package content directory.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// The classified StructureDefinition flavor (e.g. "Profile", "Extension", "Logical",
    /// "Type", "Resource"), or <c>null</c> if the resource is not a StructureDefinition.
    /// </summary>
    public string? SdFlavor { get; init; }
}

/// <summary>
/// Criteria for searching across indexed package resources.
/// All properties are optional; unset properties are not applied as filters.
/// </summary>
public record ResourceSearchCriteria
{
    /// <summary>
    /// A general search key. When set, matches resources by canonical URL or resource type
    /// (compared case-insensitively).
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Filter results to only these resource types.</summary>
    public IReadOnlyList<string>? ResourceTypes { get; init; }

    /// <summary>
    /// Filter StructureDefinition results to only these flavors
    /// (e.g. "Profile", "Extension", "Logical", "Type", "Resource").
    /// </summary>
    public IReadOnlyList<string>? SdFlavors { get; init; }

    /// <summary>
    /// Restrict results to resources from a specific package (format: "name#version" or just "name").
    /// </summary>
    public string? PackageScope { get; init; }

    /// <summary>Maximum number of results to return. <c>null</c> means no limit.</summary>
    public int? Limit { get; init; }
}
