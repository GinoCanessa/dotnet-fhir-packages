// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Models;

/// <summary>
/// Represents the fully-resolved closure of all transitive dependencies for a set of root packages.
/// </summary>
public record PackageClosure
{
    /// <summary>
    /// The timestamp at which this closure was computed.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// The set of successfully resolved packages, keyed by package identifier.
    /// </summary>
    public required IReadOnlyDictionary<string, PackageReference> Resolved { get; init; }

    /// <summary>
    /// Packages that could not be resolved, keyed by package identifier with the reason as value.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Missing { get; init; }

    /// <summary>
    /// Indicates whether all requested dependencies were successfully resolved.
    /// </summary>
    public bool IsComplete => Missing.Count == 0;
}
