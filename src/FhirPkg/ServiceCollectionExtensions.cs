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
        var optionsBuilder = services.AddOptions<FhirPackageManagerOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Register the options instance for direct injection
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<FhirPackageManagerOptions>>();
            return opts.Value;
        });

        // Register HttpClient via the typed HttpClient factory
        services.AddHttpClient("FhirPackages", (sp, client) =>
        {
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            client.Timeout = options.HttpTimeout;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            return new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = options.MaxRedirects
            };
        });

        // Register IPackageCache as DiskPackageCache
        services.TryAddSingleton<IPackageCache>(sp =>
        {
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            return new DiskPackageCache(options.CachePath);
        });

        // Register IRegistryClient as a composite RedundantRegistryClient
        services.TryAddSingleton<IRegistryClient>(sp =>
        {
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("FhirPackages");
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return BuildRegistryClient(options, httpClient, loggerFactory);
        });

        // Register IVersionResolver as VersionResolver
        services.TryAddSingleton<IVersionResolver>(sp =>
        {
            var registryClient = sp.GetRequiredService<IRegistryClient>();
            var logger = sp.GetRequiredService<ILogger<VersionResolver>>();
            return new VersionResolver(registryClient, logger);
        });

        // Register IDependencyResolver as DependencyResolver
        services.TryAddSingleton<IDependencyResolver>(sp =>
        {
            var registryClient = sp.GetRequiredService<IRegistryClient>();
            var versionResolver = sp.GetRequiredService<IVersionResolver>();
            var cache = sp.GetRequiredService<IPackageCache>();
            var logger = sp.GetRequiredService<ILogger<DependencyResolver>>();
            return new DependencyResolver(registryClient, versionResolver, cache, logger);
        });

        // Register IPackageIndexer as PackageIndexer
        services.TryAddSingleton<IPackageIndexer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PackageIndexer>>();
            return new PackageIndexer(logger);
        });

        // Register optional MemoryResourceCache
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            return options.ResourceCacheSize > 0
                ? new MemoryResourceCache(options.ResourceCacheSize, options.ResourceCacheSafeMode)
                : null;
        });

        // Register IFhirPackageManager as FhirPackageManager (DI constructor overload)
        services.TryAddSingleton<IFhirPackageManager>(sp =>
        {
            var cache = sp.GetRequiredService<IPackageCache>();
            var registryClient = sp.GetRequiredService<IRegistryClient>();
            var versionResolver = sp.GetRequiredService<IVersionResolver>();
            var dependencyResolver = sp.GetRequiredService<IDependencyResolver>();
            var packageIndexer = sp.GetRequiredService<IPackageIndexer>();
            var options = sp.GetRequiredService<FhirPackageManagerOptions>();
            var logger = sp.GetRequiredService<ILogger<FhirPackageManager>>();
            var memoryCache = sp.GetService<MemoryResourceCache>();

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

    /// <summary>
    /// Builds the composite registry client chain from the provided options.
    /// </summary>
    private static IRegistryClient BuildRegistryClient(
        FhirPackageManagerOptions options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        var clients = new List<IRegistryClient>();

        if (options.Registries.Count > 0)
        {
            foreach (var endpoint in options.Registries)
            {
                clients.Add(CreateClientForEndpoint(endpoint, httpClient, loggerFactory));
            }
        }
        else
        {
            clients.Add(new FhirNpmRegistryClient(httpClient, RegistryEndpoint.FhirPrimary, loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
            clients.Add(new FhirNpmRegistryClient(httpClient, RegistryEndpoint.FhirSecondary, loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
        }

        if (options.IncludeCiBuilds)
        {
            clients.Add(new FhirCiBuildClient(httpClient, RegistryEndpoint.FhirCiBuild, loggerFactory.CreateLogger<FhirCiBuildClient>()));
        }

        if (options.IncludeHl7WebsiteFallback)
        {
            clients.Add(new Hl7WebsiteClient(httpClient, RegistryEndpoint.Hl7Website, loggerFactory.CreateLogger<Hl7WebsiteClient>()));
        }

        return new RedundantRegistryClient(clients, loggerFactory.CreateLogger<RedundantRegistryClient>());
    }

    /// <summary>
    /// Creates the appropriate registry client implementation for a given endpoint type.
    /// </summary>
    private static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        return endpoint.Type switch
        {
            RegistryType.FhirNpm => new FhirNpmRegistryClient(httpClient, endpoint, loggerFactory.CreateLogger<FhirNpmRegistryClient>()),
            RegistryType.FhirCiBuild => new FhirCiBuildClient(httpClient, endpoint, loggerFactory.CreateLogger<FhirCiBuildClient>()),
            RegistryType.FhirHttp => new Hl7WebsiteClient(httpClient, endpoint, loggerFactory.CreateLogger<Hl7WebsiteClient>()),
            RegistryType.Npm => new NpmRegistryClient(httpClient, endpoint, loggerFactory.CreateLogger<NpmRegistryClient>()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(endpoint), endpoint.Type, $"Unsupported registry type: {endpoint.Type}.")
        };
    }
}
