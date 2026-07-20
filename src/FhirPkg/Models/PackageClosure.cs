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
    /// <remarks>
    /// This compatibility projection combines the structured entries in
    /// <see cref="Failures"/> by package identifier.
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Missing { get; init; }

    /// <summary>
    /// Structured failures that prevented the active dependency graph from being complete.
    /// </summary>
    public IReadOnlyList<DependencyResolutionFailure> Failures { get; init; } = [];

    /// <summary>
    /// Active package installation references in deterministic dependency-first
    /// order. Mutable CI references preserve their requested alias while
    /// <see cref="Resolved"/> retains the selected exact manifest identity.
    /// </summary>
    public IReadOnlyList<PackageReference> InstallOrder { get; init; } = [];

    /// <summary>
    /// Alias dependencies that must be installed before their exact manifest
    /// identity and transitive dependencies can be resolved.
    /// </summary>
    public IReadOnlyList<PackageReference> BootstrapInstallOrder { get; init; } =
        [];

    /// <summary>
    /// Indicates whether <see cref="InstallOrder"/> is the complete
    /// authoritative set, including the valid case where it is empty because
    /// every resolved dependency is already installed.
    /// </summary>
    public bool InstallOrderIsComplete { get; init; }

    /// <summary>
    /// Indicates whether all requested dependencies were successfully resolved.
    /// </summary>
    public bool IsComplete => Missing.Count == 0 && Failures.Count == 0;
}
