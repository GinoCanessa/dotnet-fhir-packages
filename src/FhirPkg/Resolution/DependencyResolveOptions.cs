// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Utilities;

namespace FhirPkg.Resolution;

/// <summary>
/// Options controlling recursive dependency resolution behavior.
/// </summary>
public class DependencyResolveOptions
{
    /// <summary>
    /// Strategy for resolving version conflicts when multiple dependency paths
    /// require different versions of the same package.
    /// Default: <see cref="ConflictResolutionStrategy.HighestWins"/>.
    /// </summary>
    public ConflictResolutionStrategy ConflictStrategy { get; set; } = ConflictResolutionStrategy.HighestWins;

    /// <summary>
    /// Maximum root-relative depth for transitive dependency resolution.
    /// Direct dependencies are depth zero, so a value of zero allows direct
    /// dependencies while reporting their children as depth-limit failures.
    /// Negative values are rejected.
    /// Default: 20.
    /// </summary>
    public int MaxDepth { get; set; } = 20;

    /// <summary>
    /// Whether to include pre-release versions when resolving version specifiers.
    /// Default: <c>true</c>.
    /// </summary>
    public bool AllowPreRelease { get; set; } = true;

    /// <summary>
    /// FHIR release to prefer when resolving packages with version-specific variants.
    /// When <c>null</c>, any FHIR release is accepted.
    /// </summary>
    public FhirRelease? PreferredFhirRelease { get; set; }

    internal PackageFixupPolicy? FixupPolicy { get; set; }

    internal PackageReference? RootReference { get; set; }

    internal bool InstallCachedPackages { get; set; }

    internal bool PreferCachedAliases { get; set; }
}
