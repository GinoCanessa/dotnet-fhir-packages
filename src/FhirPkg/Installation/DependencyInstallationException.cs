// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;

namespace FhirPkg.Installation;

/// <summary>
/// Represents an aggregate dependency-stage failure after the root package was committed.
/// Cancellation is never wrapped in this exception.
/// </summary>
public sealed class DependencyInstallationException : PackageInstallException
{
    /// <summary>
    /// Creates an aggregate dependency installation failure.
    /// </summary>
    /// <param name="rootPackage">The root package already committed to the cache.</param>
    /// <param name="dependencyFailures">Failed dependency installation results.</param>
    /// <param name="resolutionFailures">Structured closure failures, when present.</param>
    public DependencyInstallationException(
        PackageRecord rootPackage,
        IEnumerable<PackageInstallResult> dependencyFailures,
        IEnumerable<DependencyResolutionFailure>? resolutionFailures = null)
        : base(
            PackageInstallErrorCode.DependencyInstallationFailed,
            PackageInstallStage.DependencyInstallation,
            CreateMessage(
                dependencyFailures,
                out IReadOnlyList<PackageInstallResult> failureSnapshots),
            rootPackage?.Reference.FhirDirective
                ?? throw new ArgumentNullException(nameof(rootPackage)))
    {
        RootPackage = rootPackage;
        DependencyFailures = failureSnapshots;
        DependencyResolutionFailures = Array.AsReadOnly(
            resolutionFailures?.ToArray()
                ?? []);
    }

    /// <summary>Gets the root package that was committed before dependency installation failed.</summary>
    public PackageRecord RootPackage { get; }

    /// <summary>Gets failed dependency installation results in deterministic attempt order.</summary>
    public IReadOnlyList<PackageInstallResult> DependencyFailures { get; }

    /// <summary>Gets structured dependency-closure failures.</summary>
    public IReadOnlyList<DependencyResolutionFailure> DependencyResolutionFailures { get; }

    private static string CreateMessage(
        IEnumerable<PackageInstallResult> dependencyFailures,
        out IReadOnlyList<PackageInstallResult> snapshots)
    {
        ArgumentNullException.ThrowIfNull(dependencyFailures);

        PackageInstallResult[] captured = dependencyFailures.ToArray();
        if (captured.Length == 0)
        {
            throw new ArgumentException(
                "At least one dependency failure is required.",
                nameof(dependencyFailures));
        }

        snapshots = Array.AsReadOnly(captured);
        return captured.Length == 1
            ? "One requested dependency could not be installed."
            : $"{captured.Length} requested dependencies could not be installed.";
    }
}
