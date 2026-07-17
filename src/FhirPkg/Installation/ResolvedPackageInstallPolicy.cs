// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Installation;

internal sealed record ResolvedPackageInstallPolicy
{
    internal required PackageInstallLimits Limits { get; init; }

    internal required CorruptCacheBehavior CorruptCacheBehavior { get; init; }

    internal required bool VerifyChecksums { get; init; }

    internal required bool IncludeDependencies { get; init; }

    internal required bool OverwriteExisting { get; init; }

    internal required FhirRelease? PreferredFhirRelease { get; init; }

    internal required bool AllowPreRelease { get; init; }

    internal required IProgress<PackageProgress>? Progress { get; init; }

    internal static ResolvedPackageInstallPolicy Resolve(
        FhirPackageManagerOptions managerOptions,
        PackageInstallLimits managerLimits,
        InstallOptions? installOptions)
    {
        ArgumentNullException.ThrowIfNull(managerOptions);
        ArgumentNullException.ThrowIfNull(managerLimits);

        if (!Enum.IsDefined(managerOptions.CorruptCacheBehavior))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "CorruptCacheBehavior is not a supported value.");
        }

        InstallOptions effectiveOptions = installOptions ?? new InstallOptions();
        PackageInstallLimits limits = PackageInstallLimits.ResolvePerCall(
            managerLimits,
            effectiveOptions.InstallLimits);

        return new ResolvedPackageInstallPolicy
        {
            Limits = limits,
            CorruptCacheBehavior = managerOptions.CorruptCacheBehavior,
            VerifyChecksums = managerOptions.VerifyChecksums,
            IncludeDependencies = effectiveOptions.IncludeDependencies,
            OverwriteExisting = effectiveOptions.OverwriteExisting,
            PreferredFhirRelease = effectiveOptions.PreferredFhirRelease,
            AllowPreRelease = effectiveOptions.AllowPreRelease,
            Progress = effectiveOptions.Progress
        };
    }

    internal ResolvedPackageInstallPolicy WithoutDependencies() =>
        this with { IncludeDependencies = false };
}
