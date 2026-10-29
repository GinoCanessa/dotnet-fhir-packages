// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="FhirPackageManagerOptions.GitHubToken"/> is set. This
    /// overload can only build an unverified transport, and a credential is refused on
    /// a transport that is not redirect-controlled.
    /// </exception>
    public static IRegistryClient BuildRegistryClient(
        FhirPackageManagerOptions options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.GitHubToken is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(FhirPackageManagerOptions)}.{nameof(FhirPackageManagerOptions.GitHubToken)} requires a redirect-controlled transport; " +
                $"use the {nameof(BuildRegistryClient)} overload that accepts a {nameof(RegistryHttpTransport)}.");
        }

        return BuildRegistryClient(
            options,
            RegistryHttpTransport.CreateUnverified(httpClient),
            loggerFactory,
            timeProvider);
    }

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

        return BuildRegistryClient(
            options,
            transport,
            loggerFactory,
            timeProvider,
            new FileSystemContext(options.FileSystemRetry));
    }

    internal static IRegistryClient BuildRegistryClient(
        FhirPackageManagerOptions options,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider,
        FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(context);

        List<IRegistryClient> clients = [];

        if (options.Registries.Count > 0)
        {
            foreach (RegistryEndpoint endpoint in options.Registries)
            {
                clients.Add(CreateClientForEndpoint(
                    endpoint,
                    transport,
                    loggerFactory,
                    options.InstallLimits,
                    timeProvider,
                    options.GitHubToken,
                    context));
            }
        }
        else
        {
            clients.Add(new FhirNpmRegistryClient(
                transport,
                RegistryEndpoint.FhirPrimary,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>(),
                context));
            clients.Add(new FhirNpmRegistryClient(
                transport,
                RegistryEndpoint.FhirSecondary,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>(),
                context));
        }

        if (options.IncludeCiBuilds)
        {
            clients.Add(new FhirCiBuildClient(
                transport,
                RegistryEndpoint.FhirCiBuild,
                loggerFactory.CreateLogger<FhirCiBuildClient>(),
                timeProvider,
                options.GitHubToken,
                context));
        }

        if (options.IncludeHl7WebsiteFallback)
        {
            clients.Add(new Hl7WebsiteClient(
                transport,
                RegistryEndpoint.Hl7Website,
                loggerFactory.CreateLogger<Hl7WebsiteClient>(),
                context));
        }

        return new RedundantRegistryClient(
            clients,
            options.MaxParallelRegistryQueries,
            loggerFactory.CreateLogger<RedundantRegistryClient>(),
            context);
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
            timeProvider,
            gitHubToken: null,
            FileSystemContext.CreateDefault());

    /// <summary>
    /// Creates an endpoint client with a construction-time filesystem retry policy.
    /// </summary>
    /// <param name="endpoint">The registry endpoint configuration.</param>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="loggerFactory">The logger factory for typed loggers.</param>
    /// <param name="timeProvider">The time provider, or <see langword="null"/> for the system provider.</param>
    /// <param name="fileSystemRetry">The bounded filesystem retry options.</param>
    /// <returns>The registry client for the endpoint.</returns>
    public static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider,
        FileSystemRetryOptions fileSystemRetry) =>
        CreateClientForEndpointCore(
            endpoint,
            RegistryHttpTransport.CreateUnverified(httpClient),
            loggerFactory,
            installLimits: null,
            timeProvider,
            gitHubToken: null,
            new FileSystemContext(fileSystemRetry));

    /// <summary>
    /// Creates an endpoint client with explicit transport guarantees and a
    /// construction-time filesystem retry policy.
    /// </summary>
    /// <param name="endpoint">The registry endpoint configuration.</param>
    /// <param name="transport">The HTTP transport to use for requests.</param>
    /// <param name="loggerFactory">The logger factory for typed loggers.</param>
    /// <param name="timeProvider">The time provider, or <see langword="null"/> for the system provider.</param>
    /// <param name="fileSystemRetry">The bounded filesystem retry options.</param>
    /// <returns>The registry client for the endpoint.</returns>
    public static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider,
        FileSystemRetryOptions fileSystemRetry) =>
        CreateClientForEndpointCore(
            endpoint,
            transport,
            loggerFactory,
            installLimits: null,
            timeProvider,
            gitHubToken: null,
            new FileSystemContext(fileSystemRetry));

    internal static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        PackageInstallLimits installLimits,
        TimeProvider? timeProvider = null,
        string? gitHubToken = null)
        => CreateClientForEndpoint(
            endpoint,
            transport,
            loggerFactory,
            installLimits,
            timeProvider,
            gitHubToken,
            FileSystemContext.CreateDefault());

    internal static IRegistryClient CreateClientForEndpoint(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        PackageInstallLimits installLimits,
        TimeProvider? timeProvider,
        string? gitHubToken,
        FileSystemContext context)
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
            timeProvider,
            gitHubToken,
            context);
    }

    private static IRegistryClient CreateClientForEndpointCore(
        RegistryEndpoint endpoint,
        RegistryHttpTransport transport,
        ILoggerFactory loggerFactory,
        PackageInstallLimits? installLimits,
        TimeProvider? timeProvider,
        string? gitHubToken,
        FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(context);

        return endpoint.Type switch
        {
            RegistryType.FhirNpm => new FhirNpmRegistryClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<FhirNpmRegistryClient>(),
                context),
            RegistryType.FhirCiBuild => new FhirCiBuildClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<FhirCiBuildClient>(),
                timeProvider,
                gitHubToken,
                context),
            RegistryType.FhirHttp => new Hl7WebsiteClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<Hl7WebsiteClient>(),
                context),
            RegistryType.Npm => new NpmRegistryClient(
                transport,
                endpoint,
                loggerFactory.CreateLogger<NpmRegistryClient>(),
                installLimits ?? PackageInstallLimits.FromEnvironment(),
                context),
            _ => throw new ArgumentOutOfRangeException(
                nameof(endpoint), endpoint.Type, $"Unsupported registry type: {endpoint.Type}.")
        };
    }
}
