// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirPkg;

/// <summary>
/// Extension methods for registering FHIR package management services
/// with the Microsoft.Extensions.DependencyInjection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all FHIR package management services in the dependency injection container.
    /// This includes the package manager, cache, registry clients, version and dependency resolvers,
    /// and the package indexer.
    /// </summary>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configure">
    /// Optional configuration delegate to customize <see cref="FhirPackageManagerOptions"/>.
    /// When <c>null</c>, default options are used.
    /// </param>
    /// <returns>The <paramref name="services"/> collection for chaining.</returns>
    /// <example>
    /// <code>
    /// var services = new ServiceCollection();
    /// services.AddFhirPackageManagement(options =>
    /// {
    ///     options.CachePath = "./my-cache";
    ///     options.IncludeCiBuilds = false;
    /// });
    ///
    /// var provider = services.BuildServiceProvider();
    /// var manager = provider.GetRequiredService&lt;IFhirPackageManager&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddFhirPackageManagement(
        this IServiceCollection services,
        Action<FhirPackageManagerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        bool hasPreRegisteredOptions = services.Any(descriptor =>
            descriptor.ServiceType == typeof(FhirPackageManagerOptions));

        // Register and configure options
        OptionsBuilder<FhirPackageManagerOptions> optionsBuilder = services.AddOptions<FhirPackageManagerOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(sp =>
        {
            FhirPackageManagerOptions configuredOptions = hasPreRegisteredOptions
                ? sp.GetRequiredService<FhirPackageManagerOptions>()
                : sp.GetRequiredService<IOptions<FhirPackageManagerOptions>>().Value;
            return FhirPackageManagerConfiguration.Create(configuredOptions);
        });

        if (!hasPreRegisteredOptions)
        {
            services.TryAddSingleton(sp =>
            {
                FhirPackageManagerConfiguration configuration =
                    sp.GetRequiredService<FhirPackageManagerConfiguration>();
                return configuration.CreateOptionsSnapshot();
            });
        }

        // Register HttpClient via the typed HttpClient factory
        services.AddHttpClient("FhirPackages", (sp, client) =>
        {
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            FhirPackageManagerOptions options =
                sp.GetRequiredService<FhirPackageManagerConfiguration>().Options;
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                MaxAutomaticRedirections = options.MaxRedirects
            };
        });

        // Register IPackageCache as DiskPackageCache
        services.TryAddSingleton<IPackageCache>(sp =>
        {
            FhirPackageManagerConfiguration configuration =
                sp.GetRequiredService<FhirPackageManagerConfiguration>();
            FhirPackageManagerOptions options = configuration.Options;
            ILogger<DiskPackageCache> logger = sp.GetRequiredService<ILogger<DiskPackageCache>>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            PackageInstallLimits installLimits = configuration.InstallLimits;
            return new DiskPackageCache(
                options.CachePath,
                logger,
                timeProvider,
                installLimits,
                configuration.FileSystemContext);
        });
        services.TryAddSingleton<IHardenedPackageCache>(sp =>
            sp.GetRequiredService<IPackageCache>()
                as IHardenedPackageCache
            ?? throw new PackageInstallException(
                PackageInstallErrorCode.UnsupportedCacheCapability,
                PackageInstallStage.PolicyValidation,
                "The configured package cache does not support hardened installation."));

        // Register IRegistryClient as a composite RedundantRegistryClient
        services.TryAddSingleton<IRegistryClient>(sp =>
        {
            FhirPackageManagerConfiguration configuration =
                sp.GetRequiredService<FhirPackageManagerConfiguration>();
            FhirPackageManagerOptions options = configuration.Options;
            IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient("FhirPackages");
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            RegistryHttpTransport transport =
                RegistryHttpTransport.CreateRedirectControlled(
                    httpClient,
                    options.HttpTimeout,
                    options.MaxRedirects);

            return RegistryClientFactory.BuildRegistryClient(
                options,
                transport,
                loggerFactory,
                timeProvider,
                configuration.FileSystemContext);
        });

        // Register IVersionResolver as VersionResolver
        services.TryAddSingleton<IVersionResolver>(sp =>
        {
            _ = sp.GetRequiredService<FhirPackageManagerConfiguration>();
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            ILogger<VersionResolver> logger = sp.GetRequiredService<ILogger<VersionResolver>>();
            return new VersionResolver(registryClient, logger);
        });

        // Register IDependencyResolver as DependencyResolver
        services.TryAddSingleton<IDependencyResolver>(sp =>
        {
            FhirPackageManagerConfiguration configuration =
                sp.GetRequiredService<FhirPackageManagerConfiguration>();
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            IVersionResolver versionResolver = sp.GetRequiredService<IVersionResolver>();
            IPackageCache cache = sp.GetRequiredService<IPackageCache>();
            ILogger<DependencyResolver> logger = sp.GetRequiredService<ILogger<DependencyResolver>>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            PackageFixupPolicy fixupPolicy = configuration.FixupPolicy;
            return new DependencyResolver(
                registryClient,
                versionResolver,
                cache,
                logger,
                fixupPolicy,
                timeProvider);
        });

        // Register IPackageIndexer as PackageIndexer
        services.TryAddSingleton<IPackageIndexer>(sp =>
        {
            FhirPackageManagerConfiguration configuration =
                sp.GetRequiredService<FhirPackageManagerConfiguration>();
            ILogger<PackageIndexer> logger = sp.GetRequiredService<ILogger<PackageIndexer>>();
            return new PackageIndexer(logger, configuration.FileSystemContext);
        });

        // MemoryResourceCache is created directly in the FhirPackageManager factory
        // when ResourceCacheSize > 0 (see above), avoiding a fragile null-returning factory.

        // Register IFhirPackageManager as FhirPackageManager (DI constructor overload)
        services.TryAddSingleton<IFhirPackageManager>(sp =>
        {
            FhirPackageManagerConfiguration configuration =
                sp.GetRequiredService<FhirPackageManagerConfiguration>();
            HashSet<object> visited = new(ReferenceEqualityComparer.Instance);

            // Validate filesystem-free collaborators before the dependency resolver can create its cache.
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            configuration.ValidateFileSystemCollaborators([registryClient], visited);
            IVersionResolver versionResolver = sp.GetRequiredService<IVersionResolver>();
            configuration.ValidateFileSystemCollaborators([versionResolver], visited);
            IPackageIndexer packageIndexer = sp.GetRequiredService<IPackageIndexer>();
            configuration.ValidateFileSystemCollaborators([packageIndexer], visited);

            IDependencyResolver dependencyResolver = sp.GetRequiredService<IDependencyResolver>();
            configuration.ValidateFileSystemCollaborators([dependencyResolver], visited);
            IPackageCache cache = sp.GetRequiredService<IPackageCache>();
            configuration.ValidateFileSystemCollaborators([cache], visited);

            FhirPackageManagerOptions options = configuration.Options;
            ILogger<FhirPackageManager> logger = sp.GetRequiredService<ILogger<FhirPackageManager>>();
            IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient("FhirPackages");

            // Only create in-memory resource cache when configured with a positive cache size
            MemoryResourceCache? memoryCache = options.ResourceCacheSize > 0
                ? new MemoryResourceCache(options.ResourceCacheSize, options.ResourceCacheSafeMode)
                : null;

            return FhirPackageManager.CreateWithConfiguration(
                cache,
                registryClient,
                versionResolver,
                dependencyResolver,
                packageIndexer,
                configuration,
                logger,
                memoryCache,
                httpClient,
                sp.GetRequiredService<ILoggerFactory>(),
                redirectsControlled: true,
                visited);
        });

        services.TryAddSingleton<IHardenedFhirPackageManager>(sp =>
            sp.GetRequiredService<IFhirPackageManager>()
                as IHardenedFhirPackageManager
            ?? throw new PackageInstallException(
                PackageInstallErrorCode.UnsupportedManagerCapability,
                PackageInstallStage.PolicyValidation,
                "The configured package manager does not support hardened installation sources."));

        services.TryAddSingleton<IFhirPackageResourceManager>(sp =>
            sp.GetRequiredService<IFhirPackageManager>()
                as IFhirPackageResourceManager
            ?? throw new InvalidOperationException(
                "The configured package manager does not support resource indexing."));

        return services;
    }
}
