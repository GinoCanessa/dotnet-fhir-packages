// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.IO;
using FhirPkg.Registry;
using FhirPkg.Resolution;
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

    internal required FileSystemContext FileSystemContext { get; init; }

    internal required bool HasExplicitFileSystemRetry { get; init; }

    internal static FhirPackageManagerConfiguration Create(
        FhirPackageManagerOptions configuredOptions,
        PackageInstallLimits? resolvedInstallLimits = null)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);

        return CreateResolved(
            configuredOptions,
            new FileSystemContext(configuredOptions.FileSystemRetry),
            resolvedInstallLimits);
    }

    // Used by composition roots that have already resolved the invocation policy.
    // In particular, never infer explicitness from the populated options snapshot.
    internal static FhirPackageManagerConfiguration CreateResolved(
        FhirPackageManagerOptions configuredOptions,
        FileSystemContext context,
        PackageInstallLimits? resolvedInstallLimits = null)
    {
        ArgumentNullException.ThrowIfNull(configuredOptions);
        ArgumentNullException.ThrowIfNull(context);

        Validate(configuredOptions);

        PackageInstallLimits limits = resolvedInstallLimits is null
            ? PackageInstallLimits.ResolveManager(configuredOptions.InstallLimits)
            : CopyLimits(resolvedInstallLimits);
        PackageFixupPolicy fixupPolicy =
            PackageFixupPolicy.Create(configuredOptions.VersionFixups);

        return new FhirPackageManagerConfiguration
        {
            Options = CopyOptions(configuredOptions, limits, context),
            InstallLimits = limits,
            FixupPolicy = fixupPolicy,
            FileSystemContext = context,
            HasExplicitFileSystemRetry = context.HasExplicitValues,
        };
    }

    internal FhirPackageManagerOptions CreateOptionsSnapshot() =>
        CopyOptions(Options, InstallLimits, FileSystemContext);

    internal void ValidateFileSystemCollaborators(
        IEnumerable<object> collaborators,
        ISet<object> visited)
    {
        if (!HasExplicitFileSystemRetry)
            return;

        foreach (object collaborator in collaborators)
        {
            ValidateFileSystemCollaborator(collaborator, visited);
        }
    }

    private void ValidateFileSystemCollaborator(
        object collaborator,
        ISet<object> visited)
    {
        if (!visited.Add(collaborator))
            return;

        // Only known built-in ownership is traversable. External implementations,
        // including clients derived from RegistryClientBase, remain opaque.
        switch (collaborator)
        {
            case DiskPackageCache cache when cache.GetType() == typeof(DiskPackageCache):
                ValidateContext(cache.FileSystemContext, nameof(DiskPackageCache));
                break;
            case PackageIndexer indexer when indexer.GetType() == typeof(PackageIndexer):
                ValidateContext(indexer.FileSystemContext, nameof(PackageIndexer));
                break;
            case RedundantRegistryClient composite:
                ValidateContext(composite.FileSystemContext, nameof(RedundantRegistryClient));
                foreach (RegistryClientBase client in composite.BuiltInClients)
                {
                    ValidateFileSystemCollaborator(client, visited);
                }
                break;
            case FhirNpmRegistryClient or NpmRegistryClient or FhirCiBuildClient or Hl7WebsiteClient:
                RegistryClientBase registry = (RegistryClientBase)collaborator;
                ValidateContext(registry.FileSystemContext, registry.GetType().Name);
                break;
            case DependencyResolver resolver when resolver.GetType() == typeof(DependencyResolver):
                ValidateFileSystemCollaborators(resolver.BuiltInCollaborators, visited);
                break;
            case VersionResolver resolver when resolver.GetType() == typeof(VersionResolver):
                ValidateFileSystemCollaborator(resolver.RegistryClient, visited);
                break;
        }
    }

    private void ValidateContext(FileSystemContext context, string component)
    {
        if (context.Policy != FileSystemContext.Policy)
        {
            throw InvalidPolicy(
                $"The injected built-in {component} has filesystem retry settings that do not match the explicitly configured manager.");
        }
    }

    private static FhirPackageManagerOptions CopyOptions(
        FhirPackageManagerOptions source,
        PackageInstallLimits limits,
        FileSystemContext context) =>
        new()
        {
            FileSystemRetry = context.ToOptionsSnapshot(),
            InstallLimits = CopyLimits(limits),
            CorruptCacheBehavior = source.CorruptCacheBehavior,
            Unmodified = source.Unmodified,
            CachePath = source.CachePath,
            Registries = source.Registries.Select(CloneEndpoint).ToList(),
            IncludeCiBuilds = source.IncludeCiBuilds,
            IncludeHl7WebsiteFallback = source.IncludeHl7WebsiteFallback,
            HttpTimeout = source.HttpTimeout,
            MaxRedirects = source.MaxRedirects,
            VerifyChecksums = source.VerifyChecksums,
            MaxParallelRegistryQueries = source.MaxParallelRegistryQueries,
            ResourceCacheSize = source.ResourceCacheSize,
            ResourceCacheSafeMode = source.ResourceCacheSafeMode,
            VersionFixups = new Dictionary<string, string>(
                source.VersionFixups,
                StringComparer.OrdinalIgnoreCase),
        };

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

        if (options.FileSystemRetry is null)
        {
            throw InvalidPolicy("FileSystemRetry must not be null.");
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
