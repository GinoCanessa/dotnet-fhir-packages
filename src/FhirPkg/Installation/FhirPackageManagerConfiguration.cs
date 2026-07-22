// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Registry;
using FhirPkg.Utilities;

namespace FhirPkg.Installation;

/// <summary>
/// Validated, immutable-by-ownership manager configuration.
/// </summary>
internal sealed record FhirPackageManagerConfiguration
{
    internal required FhirPackageManagerOptions Options { get; init; }

    internal required PackageInstallLimits InstallLimits { get; init; }

    internal required PackageFixupPolicy FixupPolicy { get; init; }

    internal static FhirPackageManagerConfiguration Create(
        FhirPackageManagerOptions configuredOptions,
        PackageInstallLimits? resolvedInstallLimits = null)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);

        Validate(configuredOptions);

        PackageInstallLimits limits = resolvedInstallLimits is null
            ? PackageInstallLimits.ResolveManager(configuredOptions.InstallLimits)
            : CopyLimits(resolvedInstallLimits);
        PackageFixupPolicy fixupPolicy =
            PackageFixupPolicy.Create(configuredOptions.VersionFixups);

        FhirPackageManagerOptions snapshot = new()
        {
            InstallLimits = CopyLimits(limits),
            CorruptCacheBehavior = configuredOptions.CorruptCacheBehavior,
            CachePath = configuredOptions.CachePath,
            Registries = configuredOptions.Registries.Select(CloneEndpoint).ToList(),
            IncludeCiBuilds = configuredOptions.IncludeCiBuilds,
            IncludeHl7WebsiteFallback = configuredOptions.IncludeHl7WebsiteFallback,
            HttpTimeout = configuredOptions.HttpTimeout,
            MaxRedirects = configuredOptions.MaxRedirects,
            VerifyChecksums = configuredOptions.VerifyChecksums,
            MaxParallelRegistryQueries = configuredOptions.MaxParallelRegistryQueries,
            ResourceCacheSize = configuredOptions.ResourceCacheSize,
            ResourceCacheSafeMode = configuredOptions.ResourceCacheSafeMode,
            VersionFixups = new Dictionary<string, string>(
                configuredOptions.VersionFixups,
                StringComparer.OrdinalIgnoreCase),
        };

        return new FhirPackageManagerConfiguration
        {
            Options = snapshot,
            InstallLimits = limits,
            FixupPolicy = fixupPolicy,
        };
    }

    private static void Validate(FhirPackageManagerOptions options)
    {
        if (!Enum.IsDefined(options.CorruptCacheBehavior))
        {
            throw InvalidPolicy("CorruptCacheBehavior is not a supported value.");
        }

        if (!Enum.IsDefined(options.ResourceCacheSafeMode))
        {
            throw InvalidPolicy("ResourceCacheSafeMode is not a supported value.");
        }

        if (options.MaxParallelRegistryQueries < 1)
        {
            throw InvalidPolicy("MaxParallelRegistryQueries must be at least 1.");
        }

        if (options.ResourceCacheSize < 0)
        {
            throw InvalidPolicy("ResourceCacheSize must not be negative.");
        }

        if (options.MaxRedirects < 1)
        {
            throw InvalidPolicy("MaxRedirects must be at least 1.");
        }

        if (options.HttpTimeout <= TimeSpan.Zero
            || options.HttpTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw InvalidPolicy(
                "HttpTimeout must be finite, positive, and no greater than Int32.MaxValue milliseconds.");
        }

        if (options.InstallLimits is null)
        {
            throw InvalidPolicy("InstallLimits must not be null.");
        }

        if (options.Registries is null)
        {
            throw InvalidPolicy("Registries must not be null.");
        }

        if (options.VersionFixups is null)
        {
            throw InvalidPolicy("VersionFixups must not be null.");
        }

        foreach (RegistryEndpoint endpoint in options.Registries)
        {
            ValidateEndpoint(endpoint);
        }
    }

    private static void ValidateEndpoint(RegistryEndpoint endpoint)
    {
        if (endpoint is null)
        {
            throw InvalidPolicy("Registry endpoints must not contain null values.");
        }

        if (!Enum.IsDefined(endpoint.Type))
        {
            throw InvalidPolicy(
                $"Registry endpoint '{endpoint.Url}' has an unsupported registry type.");
        }

        if (!TryGetHttpOrigin(endpoint.Url, out _))
        {
            throw InvalidPolicy(
                $"Registry endpoint URL '{endpoint.Url}' must be an absolute HTTP or HTTPS URL.");
        }

        if (endpoint.TrustedHeaderOrigins is null)
        {
            throw InvalidPolicy(
                $"Registry endpoint '{endpoint.Url}' must not have a null trusted-origin collection.");
        }

        foreach (string trustedOrigin in endpoint.TrustedHeaderOrigins)
        {
            if (!TryGetHttpOrigin(trustedOrigin, out Uri? uri)
                || uri is null
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw InvalidPolicy(
                    $"Trusted header origin '{trustedOrigin}' must contain only an absolute HTTP or HTTPS origin.");
            }
        }
    }

    private static bool TryGetHttpOrigin(string value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return true;
        }

        uri = null;
        return false;
    }

    private static RegistryEndpoint CloneEndpoint(RegistryEndpoint endpoint) =>
        endpoint with
        {
            CustomHeaders = endpoint.CustomHeaders?.ToArray(),
            TrustedHeaderOrigins = endpoint.TrustedHeaderOrigins.ToArray(),
        };

    private static PackageInstallLimits CopyLimits(PackageInstallLimits source)
    {
        source.Validate();

        return new PackageInstallLimits
        {
            MaxCompressedBytes = source.MaxCompressedBytes,
            MaxExpandedBytes = source.MaxExpandedBytes,
            MaxEntryBytes = source.MaxEntryBytes,
            MaxArchiveEntries = source.MaxArchiveEntries,
            MaxArchivePathLength = source.MaxArchivePathLength,
            MaxArchiveDepth = source.MaxArchiveDepth,
        };
    }

    private static PackageInstallException InvalidPolicy(string message) =>
        new(
            PackageInstallErrorCode.InvalidPolicy,
            PackageInstallStage.PolicyValidation,
            message);
}
