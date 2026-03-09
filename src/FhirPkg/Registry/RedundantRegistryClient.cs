// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using FhirPkg.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirPkg.Registry;

/// <summary>
/// A composite <see cref="IRegistryClient"/> that wraps multiple clients and tries them
/// in order, falling back to the next client when one fails or returns <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>Fallback behaviour per method:</para>
/// <list type="bullet">
///   <item><description>
///     <see cref="SearchAsync"/>: queries <b>all</b> clients and merges results, deduplicating by package name.
///   </description></item>
///   <item><description>
///     <see cref="GetPackageListingAsync"/>, <see cref="ResolveAsync"/>, <see cref="DownloadAsync"/>:
///     tries clients in order; returns the first non-null result.
///   </description></item>
///   <item><description>
///     <see cref="PublishAsync"/>: tries clients in order; returns the first successful result or
///     throws the last exception if all fail.
///   </description></item>
/// </list>
/// <para>
/// Exceptions thrown by individual clients are caught and logged, then the next client is tried.
/// <see cref="OperationCanceledException"/> is never caught and always propagates immediately.
/// </para>
/// </remarks>
public sealed class RedundantRegistryClient : IRegistryClient
{
    private readonly IReadOnlyList<IRegistryClient> _clients;
    private readonly ILogger<RedundantRegistryClient> _logger;

    /// <summary>
    /// Initialises a new <see cref="RedundantRegistryClient"/> with the specified ordered list of clients.
    /// </summary>
    /// <param name="clients">The registry clients to try, in priority order.</param>
    /// <param name="logger">
    /// An optional logger. When <see langword="null"/>, a <see cref="NullLogger{T}"/> is used.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="clients"/> is empty.</exception>
    public RedundantRegistryClient(
        IEnumerable<IRegistryClient> clients,
        ILogger<RedundantRegistryClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clients);

        _clients = clients.ToList().AsReadOnly();

        if (_clients.Count == 0)
            throw new ArgumentException("At least one registry client is required.", nameof(clients));

        _logger = logger ?? NullLogger<RedundantRegistryClient>.Instance;
    }

    /// <summary>
    /// Initialises a new <see cref="RedundantRegistryClient"/> with the specified clients.
    /// </summary>
    /// <param name="clients">The registry clients to try, in priority order.</param>
    public RedundantRegistryClient(params IRegistryClient[] clients)
        : this((IEnumerable<IRegistryClient>)clients)
    {
    }

    // ── IRegistryClient properties ──────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>Returns the endpoint of the first (highest-priority) client.</remarks>
    public RegistryEndpoint Endpoint => _clients[0].Endpoint;

    /// <inheritdoc />
    /// <remarks>Returns the union of all clients' supported name types, deduplicated.</remarks>
    public IReadOnlyList<PackageNameType> SupportedNameTypes =>
        _clients.SelectMany(c => c.SupportedNameTypes).Distinct().ToList().AsReadOnly();

    /// <inheritdoc />
    /// <remarks>Returns the union of all clients' supported version types, deduplicated.</remarks>
    public IReadOnlyList<VersionType> SupportedVersionTypes =>
        _clients.SelectMany(c => c.SupportedVersionTypes).Distinct().ToList().AsReadOnly();

    // ── IRegistryClient methods ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Queries <b>all</b> clients and merges results. Entries are deduplicated by
    /// <see cref="CatalogEntry.Name"/>, with the first occurrence (from the highest-priority
    /// client) taking precedence. Failures on individual clients are logged and skipped.
    /// </remarks>
    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<CatalogEntry>();

        foreach (var client in _clients)
        {
            try
            {
                _logger.LogDebug("Searching {Endpoint} for packages", client.Endpoint.Url);

                var results = await client.SearchAsync(criteria, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entry in results)
                {
                    if (seen.Add(entry.Name))
                    {
                        merged.Add(entry);
                    }
                }

                _logger.LogDebug(
                    "Search on {Endpoint} returned {Count} result(s)",
                    client.Endpoint.Url, results.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Search failed on {Endpoint}; continuing with remaining registries",
                    client.Endpoint.Url);
            }
        }

        _logger.LogInformation("Merged search returned {Count} unique result(s)", merged.Count);

        return merged.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tries each client in order and returns the first non-null listing.
    /// </remarks>
    public async Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        foreach (var client in _clients)
        {
            try
            {
                _logger.LogDebug(
                    "Fetching listing for {PackageId} from {Endpoint}",
                    packageId, client.Endpoint.Url);

                var listing = await client.GetPackageListingAsync(packageId, cancellationToken)
                    .ConfigureAwait(false);

                if (listing is not null)
                {
                    _logger.LogDebug(
                        "Got listing for {PackageId} from {Endpoint}",
                        packageId, client.Endpoint.Url);
                    return listing;
                }

                _logger.LogDebug(
                    "{PackageId} not found on {Endpoint}; trying next registry",
                    packageId, client.Endpoint.Url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "GetPackageListing failed on {Endpoint} for {PackageId}; trying next registry",
                    client.Endpoint.Url, packageId);
            }
        }

        if (lastException is not null)
        {
            _logger.LogDebug(
                "All registries failed or returned null for {PackageId}; last error: {Error}",
                packageId, lastException.Message);
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tries each client in order and returns the first non-null resolved directive.
    /// Only clients that support the directive's <see cref="PackageDirective.VersionType"/>
    /// and <see cref="PackageDirective.NameType"/> are tried.
    /// </remarks>
    public async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        Exception? lastException = null;

        foreach (var client in _clients)
        {
            // Skip clients that don't support this version type.
            if (!client.SupportedVersionTypes.Contains(directive.VersionType))
            {
                _logger.LogDebug(
                    "Skipping {Endpoint}: does not support {VersionType}",
                    client.Endpoint.Url, directive.VersionType);
                continue;
            }

            // Skip clients that don't support this name type.
            if (!client.SupportedNameTypes.Contains(directive.NameType))
            {
                _logger.LogDebug(
                    "Skipping {Endpoint}: does not support {NameType}",
                    client.Endpoint.Url, directive.NameType);
                continue;
            }

            try
            {
                _logger.LogDebug(
                    "Resolving {PackageId} on {Endpoint}",
                    directive.PackageId, client.Endpoint.Url);

                var resolved = await client.ResolveAsync(directive, options, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved is not null)
                {
                    _logger.LogInformation(
                        "Resolved {PackageId} to {Version} via {Endpoint}",
                        directive.PackageId, resolved.Reference.Version, client.Endpoint.Url);
                    return resolved;
                }

                _logger.LogDebug(
                    "{PackageId} not resolved by {Endpoint}; trying next registry",
                    directive.PackageId, client.Endpoint.Url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Resolve failed on {Endpoint} for {PackageId}; trying next registry",
                    client.Endpoint.Url, directive.PackageId);
            }
        }

        if (lastException is not null)
        {
            _logger.LogDebug(
                "All registries failed to resolve {PackageId}; last error: {Error}",
                directive.PackageId, lastException.Message);
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tries each client in order and returns the first non-null download result.
    /// </remarks>
    public async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        Exception? lastException = null;

        foreach (var client in _clients)
        {
            try
            {
                _logger.LogDebug(
                    "Downloading {PackageId} from {Endpoint}",
                    resolved.Reference.Name, client.Endpoint.Url);

                var result = await client.DownloadAsync(resolved, cancellationToken)
                    .ConfigureAwait(false);

                if (result is not null)
                {
                    _logger.LogDebug(
                        "Download succeeded for {PackageId} via {Endpoint}",
                        resolved.Reference.Name, client.Endpoint.Url);
                    return result;
                }

                _logger.LogDebug(
                    "Download returned null from {Endpoint}; trying next registry",
                    client.Endpoint.Url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Download failed on {Endpoint} for {PackageId}; trying next registry",
                    client.Endpoint.Url, resolved.Reference.Name);
            }
        }

        if (lastException is not null)
        {
            _logger.LogDebug(
                "All registries failed to download {PackageId}; last error: {Error}",
                resolved.Reference.Name, lastException.Message);
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tries each client in order and returns the first successful publish result.
    /// If all clients fail, throws the last exception encountered. The tarball stream must
    /// support seeking so it can be rewound between attempts.
    /// </remarks>
    public async Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);

        if (!tarballStream.CanSeek)
        {
            throw new ArgumentException(
                "The tarball stream must support seeking when publishing to a redundant client " +
                "chain, so the stream can be rewound for retry attempts.", nameof(tarballStream));
        }

        Exception? lastException = null;
        PublishResult? lastResult = null;

        foreach (var client in _clients)
        {
            try
            {
                // Rewind the stream for each attempt.
                tarballStream.Position = 0;

                _logger.LogDebug(
                    "Publishing {PackageId}@{Version} to {Endpoint}",
                    reference.Name, reference.Version, client.Endpoint.Url);

                var result = await client.PublishAsync(reference, tarballStream, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Published {PackageId}@{Version} via {Endpoint}",
                        reference.Name, reference.Version, client.Endpoint.Url);
                    return result;
                }

                lastResult = result;
                _logger.LogWarning(
                    "Publish to {Endpoint} returned failure: {Message}; trying next registry",
                    client.Endpoint.Url, result.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Publish failed on {Endpoint} for {PackageId}@{Version}; trying next registry",
                    client.Endpoint.Url, reference.Name, reference.Version);
            }
        }

        // All clients failed.
        if (lastException is not null)
        {
            _logger.LogError(
                lastException,
                "All registries failed to publish {PackageId}@{Version}",
                reference.Name, reference.Version);
            throw lastException;
        }

        // No exception, but all returned failure results.
        return lastResult ?? new PublishResult
        {
            Success = false,
            StatusCode = System.Net.HttpStatusCode.ServiceUnavailable,
            Message = "All registries failed to publish the package.",
        };
    }
}
