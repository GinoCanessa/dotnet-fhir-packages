// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Installation;

internal enum PackageInstallSourceKind
{
    Directive
}

internal enum PackageInstallFreshness
{
    Immutable,
    RefreshableAlias,
    LocalAuthoritative
}

internal enum PackageIdentityExpectationKind
{
    Exact,
    Alias
}

internal sealed record PackageInstallSource
{
    internal required PackageInstallSourceKind Kind { get; init; }

    internal required ResolvedDirective ResolvedDirective { get; init; }

    internal static PackageInstallSource FromDirective(ResolvedDirective resolvedDirective) =>
        new PackageInstallSource
        {
            Kind = PackageInstallSourceKind.Directive,
            ResolvedDirective = resolvedDirective
        };
}

internal sealed record PackageIdentityExpectation
{
    internal required PackageIdentityExpectationKind Kind { get; init; }

    internal required PackageReference Reference { get; init; }

    internal PackageReference? ExpectedManifestReference { get; init; }
}

internal sealed record PackageInstallRequest
{
    internal required string Directive { get; init; }

    internal required PackageCacheKey CacheKey { get; init; }

    internal required PackageInstallSource Source { get; init; }

    internal required PackageIdentityExpectation IdentityExpectation { get; init; }

    internal required PackageInstallFreshness Freshness { get; init; }

    internal required ResolvedPackageInstallPolicy Policy { get; init; }
}
