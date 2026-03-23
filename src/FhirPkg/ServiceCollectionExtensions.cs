// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
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

        // Register and configure options
        OptionsBuilder<FhirPackageManagerOptions> optionsBuilder = services.AddOptions<FhirPackageManagerOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Register the options instance for direct injection
        services.TryAddSingleton(sp =>
        {
            IOptions<FhirPackageManagerOptions> opts = sp.GetRequiredService<IOptions<FhirPackageManagerOptions>>();
            return opts.Value;
        });

        // Register HttpClient via the typed HttpClient factory
        services.AddHttpClient("FhirPackages", (sp, client) =>
        {
            FhirPackageManagerOptions options = sp.GetRequiredService<FhirPackageManagerOptions>();
            client.Timeout = options.HttpTimeout;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            FhirPackageManagerOptions options = sp.GetRequiredService<FhirPackageManagerOptions>();
            return new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = options.MaxRedirects
            };
        });

        // Register IPackageCache as DiskPackageCache
        services.TryAddSingleton<IPackageCache>(sp =>
        {
            FhirPackageManagerOptions options = sp.GetRequiredService<FhirPackageManagerOptions>();
            ILogger<DiskPackageCache> logger = sp.GetRequiredService<ILogger<DiskPackageCache>>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new DiskPackageCache(options.CachePath, logger, timeProvider);
        });

        // Register IRegistryClient as a composite RedundantRegistryClient
        services.TryAddSingleton<IRegistryClient>(sp =>
        {
            FhirPackageManagerOptions options = sp.GetRequiredService<FhirPackageManagerOptions>();
            IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient("FhirPackages");
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;

            return RegistryClientFactory.BuildRegistryClient(options, httpClient, loggerFactory, timeProvider);
        });

        // Register IVersionResolver as VersionResolver
        services.TryAddSingleton<IVersionResolver>(sp =>
        {
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            ILogger<VersionResolver> logger = sp.GetRequiredService<ILogger<VersionResolver>>();
            return new VersionResolver(registryClient, logger);
        });

        // Register IDependencyResolver as DependencyResolver
        services.TryAddSingleton<IDependencyResolver>(sp =>
        {
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            IVersionResolver versionResolver = sp.GetRequiredService<IVersionResolver>();
            IPackageCache cache = sp.GetRequiredService<IPackageCache>();
            ILogger<DependencyResolver> logger = sp.GetRequiredService<ILogger<DependencyResolver>>();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new DependencyResolver(registryClient, versionResolver, cache, logger, timeProvider);
        });

        // Register IPackageIndexer as PackageIndexer
        services.TryAddSingleton<IPackageIndexer>(sp =>
        {
            ILogger<PackageIndexer> logger = sp.GetRequiredService<ILogger<PackageIndexer>>();
            return new PackageIndexer(logger);
        });

        // MemoryResourceCache is created directly in the FhirPackageManager factory
        // when ResourceCacheSize > 0 (see above), avoiding a fragile null-returning factory.

        // Register IFhirPackageManager as FhirPackageManager (DI constructor overload)
        services.TryAddSingleton<IFhirPackageManager>(sp =>
        {
            IPackageCache cache = sp.GetRequiredService<IPackageCache>();
            IRegistryClient registryClient = sp.GetRequiredService<IRegistryClient>();
            IVersionResolver versionResolver = sp.GetRequiredService<IVersionResolver>();
            IDependencyResolver dependencyResolver = sp.GetRequiredService<IDependencyResolver>();
            IPackageIndexer packageIndexer = sp.GetRequiredService<IPackageIndexer>();
            FhirPackageManagerOptions options = sp.GetRequiredService<FhirPackageManagerOptions>();
            ILogger<FhirPackageManager> logger = sp.GetRequiredService<ILogger<FhirPackageManager>>();

            // Only create in-memory resource cache when configured with a positive cache size
            MemoryResourceCache? memoryCache = options.ResourceCacheSize > 0
                ? new MemoryResourceCache(options.ResourceCacheSize, options.ResourceCacheSafeMode)
                : null;

            return new FhirPackageManager(
                cache,
                registryClient,
                versionResolver,
                dependencyResolver,
                packageIndexer,
                options,
                logger,
                memoryCache);
        });

        return services;
    }
}
