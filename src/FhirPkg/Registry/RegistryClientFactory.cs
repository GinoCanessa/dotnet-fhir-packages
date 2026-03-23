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
        TimeProvider? timeProvider = null)
    {
        List<IRegistryClient> clients = new List<IRegistryClient>();

        if (options.Registries.Count > 0)
        {
            foreach (RegistryEndpoint endpoint in options.Registries)
            {
                clients.Add(CreateClientForEndpoint(endpoint, httpClient, loggerFactory, timeProvider));
            }
        }
        else
        {
            clients.Add(new FhirNpmRegistryClient(httpClient, RegistryEndpoint.FhirPrimary, loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
            clients.Add(new FhirNpmRegistryClient(httpClient, RegistryEndpoint.FhirSecondary, loggerFactory.CreateLogger<FhirNpmRegistryClient>()));
        }

        if (options.IncludeCiBuilds)
        {
            clients.Add(new FhirCiBuildClient(httpClient, RegistryEndpoint.FhirCiBuild, loggerFactory.CreateLogger<FhirCiBuildClient>(), timeProvider));
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
        TimeProvider? timeProvider = null)
    {
        return endpoint.Type switch
        {
            RegistryType.FhirNpm => new FhirNpmRegistryClient(httpClient, endpoint, loggerFactory.CreateLogger<FhirNpmRegistryClient>()),
            RegistryType.FhirCiBuild => new FhirCiBuildClient(httpClient, endpoint, loggerFactory.CreateLogger<FhirCiBuildClient>(), timeProvider),
            RegistryType.FhirHttp => new Hl7WebsiteClient(httpClient, endpoint, loggerFactory.CreateLogger<Hl7WebsiteClient>()),
            RegistryType.Npm => new NpmRegistryClient(httpClient, endpoint, loggerFactory.CreateLogger<NpmRegistryClient>()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(endpoint), endpoint.Type, $"Unsupported registry type: {endpoint.Type}.")
        };
    }
}
