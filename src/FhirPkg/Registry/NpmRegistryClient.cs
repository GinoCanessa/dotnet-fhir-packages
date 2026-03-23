// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using System.Text.Json.Serialization;
using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Registry client for standard NPM registries (e.g., <c>registry.npmjs.org</c> or
/// private Verdaccio / Artifactory instances).
/// </summary>
/// <remarks>
/// Supports the standard NPM registry protocol for searching, listing, resolving, downloading,
/// and publishing packages. FHIR-specific version resolution (wildcard, range) is supported
/// through <see cref="FhirSemVer"/>.
/// </remarks>
public sealed class NpmRegistryClient : RegistryClientBase, IRegistryClient
{
    /// <summary>
    /// Initialises a new <see cref="NpmRegistryClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The NPM registry endpoint.</param>
    /// <param name="logger">The logger instance.</param>
    public NpmRegistryClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<NpmRegistryClient> logger)
        : base(httpClient, endpoint, logger)
    {
    }

    // ── IRegistryClient properties ──────────────────────────────────────

    /// <inheritdoc />
    public override IReadOnlyList<PackageNameType> SupportedNameTypes { get; } =
    [
        PackageNameType.CoreFull,
        PackageNameType.CorePartial,
        PackageNameType.GuideWithFhirSuffix,
        PackageNameType.GuideWithoutSuffix,
        PackageNameType.NonHl7Guide,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<VersionType> SupportedVersionTypes { get; } =
    [
        VersionType.Exact,
        VersionType.Latest,
        VersionType.Wildcard,
        VersionType.Range,
    ];

    // ── IRegistryClient methods ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Calls the standard NPM search endpoint: <c>GET {baseUrl}/-/v1/search?text={name}</c>
    /// and converts the results to <see cref="CatalogEntry"/> instances.
    /// </remarks>
    public override async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (string.IsNullOrWhiteSpace(criteria.Name))
        {
            Logger.LogDebug("No search name provided for NPM search; returning empty list");
            return [];
        }

        var url = $"{BaseUrl}/-/v1/search?text={Uri.EscapeDataString(criteria.Name)}";

        Logger.LogInformation("Searching NPM registry at {Url}", url);

        var response = await GetJsonAsync<NpmSearchResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        if (response?.Objects is null or { Count: 0 })
        {
            Logger.LogDebug("NPM search returned no results");
            return [];
        }

        var entries = response.Objects
            .Where(o => o.Package is not null)
            .Select(o => new CatalogEntry
            {
                Name = o.Package!.Name!,
                Description = o.Package.Description,
                Version = o.Package.Version,
            })
            .ToList();

        Logger.LogDebug("NPM search returned {Count} results", entries.Count);

        return entries.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>GET {baseUrl}/{packageId}</c> using the standard NPM package document format.
    /// </remarks>
    public override async Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var url = $"{BaseUrl}/{Uri.EscapeDataString(packageId)}";

        Logger.LogInformation("Fetching NPM package listing for {PackageId} from {Url}", packageId, url);

        var listing = await GetJsonAsync<PackageListing>(url, cancellationToken)
            .ConfigureAwait(false);

        if (listing is not null)
        {
            Logger.LogDebug(
                "NPM package {PackageId} has {VersionCount} version(s), latest = {Latest}",
                packageId,
                listing.Versions?.Count ?? 0,
                listing.LatestVersion ?? "(none)");
        }

        return listing;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolution follows the same strategy as <see cref="FhirNpmRegistryClient"/>:
    /// exact lookup, dist-tags for latest, <see cref="FhirSemVer.MaxSatisfying"/> for
    /// wildcards, and <see cref="FhirSemVer.SatisfyingRange"/> for ranges.
    /// </remarks>
    public override async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        Logger.LogInformation(
            "Resolving {PackageId} ({VersionType}: {Version}) on NPM registry",
            directive.PackageId, directive.VersionType, directive.RequestedVersion ?? "latest");

        var listing = await GetPackageListingAsync(directive.PackageId, cancellationToken)
            .ConfigureAwait(false);

        if (listing?.Versions is null or { Count: 0 })
        {
            Logger.LogWarning("Package {PackageId} not found or has no versions on NPM", directive.PackageId);
            return null;
        }

        var resolvedVersion = ResolveVersion(directive, listing, options);

        if (resolvedVersion is null)
        {
            Logger.LogWarning(
                "No NPM version satisfying {VersionType} '{Specifier}' found for {PackageId}",
                directive.VersionType, directive.RequestedVersion, directive.PackageId);
            return null;
        }

        if (!listing.Versions.TryGetValue(resolvedVersion, out var versionInfo))
        {
            Logger.LogWarning(
                "Resolved version {Version} is not in the NPM versions dictionary for {PackageId}",
                resolvedVersion, directive.PackageId);
            return null;
        }

        var tarballUrl = versionInfo.Distribution?.TarballUrl
            ?? $"{BaseUrl}/{Uri.EscapeDataString(directive.PackageId)}" +
               $"/-/{Uri.EscapeDataString(directive.PackageId)}-{resolvedVersion}.tgz";

        Logger.LogInformation(
            "Resolved {PackageId} to version {Version} on NPM (tarball: {Tarball})",
            directive.PackageId, resolvedVersion, tarballUrl);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, resolvedVersion),
            TarballUri = new Uri(tarballUrl),
            ShaSum = versionInfo.Distribution?.ShaSum,
            SourceRegistry = Endpoint,
            PublicationDate = DateTime.TryParse(versionInfo.PublicationDate, out var pubDate) ? pubDate : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Downloads the tarball from the URI in the resolved directive.
    /// The caller must dispose the returned <see cref="PackageDownloadResult"/>.
    /// </remarks>
    public override async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var url = resolved.TarballUri.ToString();
        Logger.LogInformation("Downloading tarball from NPM: {Url}", url);

        var response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            Logger.LogWarning("NPM tarball not found at {Url}", url);
            return null;
        }

        try
        {
            return await CreateDownloadResultAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends <c>PUT {baseUrl}/{name}</c> with the tarball stream.
    /// Requires authentication to be configured on the endpoint.
    /// </remarks>
    public override async Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);

        var url = $"{BaseUrl}/{Uri.EscapeDataString(reference.Name)}";

        Logger.LogInformation(
            "Publishing {PackageId}@{Version} to NPM registry at {Url}",
            reference.Name, reference.Version, url);

        try
        {
            using var response = await PutStreamAsync(
                    url, tarballStream, "application/gzip", cancellationToken)
                .ConfigureAwait(false);

            Logger.LogInformation(
                "Published {PackageId}@{Version} to NPM successfully ({StatusCode})",
                reference.Name, reference.Version, (int)response.StatusCode);

            return new PublishResult
            {
                Success = true,
                StatusCode = response.StatusCode,
                Message = $"Package {reference.Name}@{reference.Version} published to NPM.",
            };
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(
                ex, "Failed to publish {PackageId}@{Version} to NPM", reference.Name, reference.Version);

            return new PublishResult
            {
                Success = false,
                StatusCode = ex.StatusCode ?? HttpStatusCode.InternalServerError,
                Message = ex.Message,
            };
        }
    }

    // ── Internal DTOs for NPM search response ───────────────────────────

    /// <summary>Represents the top-level NPM search API response.</summary>
    private sealed class NpmSearchResponse
    {
        [JsonPropertyName("objects")]
        public List<NpmSearchObject>? Objects { get; set; }
    }

    /// <summary>Represents a single search result object from the NPM search API.</summary>
    private sealed class NpmSearchObject
    {
        [JsonPropertyName("package")]
        public NpmSearchPackage? Package { get; set; }
    }

    /// <summary>Represents the package metadata within an NPM search result.</summary>
    private sealed class NpmSearchPackage
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
