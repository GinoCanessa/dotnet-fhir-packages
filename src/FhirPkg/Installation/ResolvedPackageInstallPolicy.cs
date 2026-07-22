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

    internal required ConflictResolutionStrategy ConflictStrategy { get; init; }

    internal required int MaxDepth { get; init; }

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
        ConflictResolutionStrategy conflictStrategy =
            effectiveOptions is RestoreOptions restoreOptions
                ? restoreOptions.ConflictStrategy
                : ConflictResolutionStrategy.HighestWins;
        int maxDepth = effectiveOptions is RestoreOptions depthOptions
            ? depthOptions.MaxDepth
            : 20;
        if (!Enum.IsDefined(conflictStrategy))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "ConflictStrategy is not a supported value.");
        }
        if (maxDepth < 0)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "MaxDepth cannot be negative.");
        }

        if (effectiveOptions.PreferredFhirRelease is FhirRelease preferredFhirRelease
            && !Enum.IsDefined(preferredFhirRelease))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "PreferredFhirRelease is not a supported value.");
        }

        PackageInstallLimits limits = PackageInstallLimits.ResolvePerCall(
            managerLimits,
            effectiveOptions.InstallLimits);
        CorruptCacheBehavior corruptCacheBehavior =
            managerOptions.CorruptCacheBehavior;
        if (effectiveOptions is PackageSourceInstallOptions sourceOptions
            && sourceOptions.CorruptCacheBehavior
                is CorruptCacheBehavior sourceBehavior)
        {
            if (!Enum.IsDefined(sourceBehavior))
            {
                throw new PackageInstallException(
                    PackageInstallErrorCode.InvalidPolicy,
                    PackageInstallStage.PolicyValidation,
                    "CorruptCacheBehavior is not a supported value.");
            }

            corruptCacheBehavior = sourceBehavior;
        }

        return new ResolvedPackageInstallPolicy
        {
            Limits = limits,
            CorruptCacheBehavior = corruptCacheBehavior,
            VerifyChecksums = managerOptions.VerifyChecksums,
            IncludeDependencies = effectiveOptions.IncludeDependencies,
            OverwriteExisting = effectiveOptions.OverwriteExisting,
            PreferredFhirRelease = effectiveOptions.PreferredFhirRelease,
            AllowPreRelease = effectiveOptions.AllowPreRelease,
            ConflictStrategy = conflictStrategy,
            MaxDepth = maxDepth,
            Progress = effectiveOptions.Progress
        };
    }

    internal ResolvedPackageInstallPolicy WithoutDependencies() =>
        this with { IncludeDependencies = false };
}
