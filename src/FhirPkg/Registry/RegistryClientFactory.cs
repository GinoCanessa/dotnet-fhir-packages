// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Shared factory for building the composite registry client chain from options.
/// Used by both <see cref="FhirPackageManager"/> (standalone) and
/// <c>ServiceCollectionExtensions</c> (DI) to eliminate duplicated logic.
/// </summary>
public static class RegistryClientFactory
{
    /// <summary>
    /// Builds the composite registry client chain from the provided options.
    /// </summary>
    /// <param name="options">The package manager options containing registry configuration.</param>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="loggerFactory">The logger factory for creating typed loggers.</param>
    /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>A composite <see cref="IRegistryClient"/> wrapping all configured registries.</returns>
    public static IRegistryClient BuildRegistryClient(
        FhirPackageManagerOptions options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null) =>
        BuildRegistryClient(
            options,
            RegistryHttpTransport.CreateUnverified(httpClient),
            loggerFactory,
            timeProvider);

    /// <summary>
    /// Builds the composite registry client chain from the provided options and
    /// explicit transport guarantees.
    /// </summary>
    public static IRegistryClient BuildRegistryClient(
        FhirPackageManagerOptions options,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        List<IRegistryClient> clients = new List<IRegistryClient>();

        if (options.Registries.Count > 0)
        {
            foreach (RegistryEndpoint endpoint in options.Registries)
            {
                clients.Add(CreateClientForEndpoint(
                    endpoint,
                    transport,
                    loggerFactory,
                    options.InstallLimits,
                    timeProvider));
            }
        }
        else
        {
            clients.Add(new FhirNpmRegistryClient(
                transport,
                RegistryEndpoint.FhirPrimary,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
            clients.Add(new FhirNpmRegistryClient(
                transport,
                RegistryEndpoint.FhirSecondary,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
        }

        if (options.IncludeCiBuilds)
        {
            clients.Add(new FhirCiBuildClient(
                transport,
                RegistryEndpoint.FhirCiBuild,
                loggerFactory.CreateLogger<FhirCiBuildClient>(),
                timeProvider));
        }

        if (options.IncludeHl7WebsiteFallback)
        {
            clients.Add(new Hl7WebsiteClient(
                transport,
                RegistryEndpoint.Hl7Website,
                loggerFactory.CreateLogger<Hl7WebsiteClient>()));
        }

        return new RedundantRegistryClient(
            clients,
            options.MaxParallelRegistryQueries,
            loggerFactory.CreateLogger<RedundantRegistryClient>());
    }

    /// <summary>
    /// Creates the appropriate registry client implementation for a given endpoint type.
    /// </summary>
    /// <param name="endpoint">The registry endpoint configuration.</param>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="loggerFactory">The logger factory for creating typed loggers.</param>
    /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>An <see cref="IRegistryClient"/> appropriate for the endpoint type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the endpoint type is not recognized.</exception>
    public static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null) =>
        CreateClientForEndpoint(
            endpoint,
            RegistryHttpTransport.CreateUnverified(httpClient),
            loggerFactory,
            timeProvider);

    /// <summary>
    /// Creates the appropriate registry client implementation for a given endpoint
    /// using explicit transport guarantees.
    /// </summary>
    public static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
        => CreateClientForEndpointCore(
            endpoint,
            transport,
            loggerFactory,
            installLimits: null,
            timeProvider);

    internal static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        PackageInstallLimits installLimits,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(installLimits);

        return CreateClientForEndpointCore(
            endpoint,
            transport,
            loggerFactory,
            installLimits,
            timeProvider);
    }

    private static IRegistryClient CreateClientForEndpointCore(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        PackageInstallLimits? installLimits,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return endpoint.Type switch
        {
            RegistryType.FhirNpm => new FhirNpmRegistryClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>()),
            RegistryType.FhirCiBuild => new FhirCiBuildClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<FhirCiBuildClient>(),
                timeProvider),
            RegistryType.FhirHttp => new Hl7WebsiteClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<Hl7WebsiteClient>()),
            RegistryType.Npm => new NpmRegistryClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<NpmRegistryClient>(),
                installLimits ?? PackageInstallLimits.FromEnvironment()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(endpoint), endpoint.Type, $"Unsupported registry type: {endpoint.Type}.")
        };
    }
}
