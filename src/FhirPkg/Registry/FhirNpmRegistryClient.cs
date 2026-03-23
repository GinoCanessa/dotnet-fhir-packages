// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Registry client for FHIR NPM registries such as <c>packages.fhir.org</c> and
/// <c>packages2.fhir.org</c>.
/// </summary>
/// <remarks>
/// <para>
/// Supports the full lifecycle: catalog search, version listing, version resolution (exact,
/// latest, wildcard, range), tarball download, and package publish.
/// </para>
/// <para>
/// The client handles both PascalCase (primary registry) and camelCase (secondary registry)
/// JSON responses through case-insensitive deserialisation.
/// </para>
/// </remarks>
public sealed class FhirNpmRegistryClient : RegistryClientBase, IRegistryClient
{
    /// <summary>
    /// Initialises a new <see cref="FhirNpmRegistryClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The FHIR NPM registry endpoint.</param>
    /// <param name="logger">The logger instance.</param>
    public FhirNpmRegistryClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<FhirNpmRegistryClient> logger)
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
    /// Calls <c>GET {baseUrl}/catalog?op=find&amp;name={name}</c> with optional
    /// <c>fhirversion</c> and <c>canonical</c> query parameters.
    /// </remarks>
    public override async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        List<string> queryParams = new List<string> { "op=find" };

        if (!string.IsNullOrWhiteSpace(criteria.Name))
            queryParams.Add($"name={Uri.EscapeDataString(criteria.Name)}");

        if (!string.IsNullOrWhiteSpace(criteria.FhirVersion))
            queryParams.Add($"fhirversion={Uri.EscapeDataString(criteria.FhirVersion)}");

        if (!string.IsNullOrWhiteSpace(criteria.Canonical))
            queryParams.Add($"canonical={Uri.EscapeDataString(criteria.Canonical)}");

        string url = $"{BaseUrl}/catalog?{string.Join('&', queryParams)}";

        Logger.LogInformation("Searching FHIR NPM catalog at {Url}", url);

        List<CatalogEntry>? results = await GetJsonAsync<List<CatalogEntry>>(url, cancellationToken)
            .ConfigureAwait(false);

        Logger.LogDebug("Catalog search returned {Count} entries", results?.Count ?? 0);

        return results?.AsReadOnly() ?? (IReadOnlyList<CatalogEntry>)[];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>GET {baseUrl}/{packageId}</c> and returns the full NPM package document
    /// containing all versions and dist-tags.
    /// </remarks>
    public override async Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        string url = $"{BaseUrl}/{Uri.EscapeDataString(packageId)}";

        Logger.LogInformation("Fetching package listing for {PackageId} from {Url}", packageId, url);

        PackageListing? listing = await GetJsonAsync<PackageListing>(url, cancellationToken)
            .ConfigureAwait(false);

        if (listing is not null)
        {
            Logger.LogDebug(
                "Package {PackageId} has {VersionCount} version(s), latest = {Latest}",
                packageId,
                listing.Versions?.Count ?? 0,
                listing.LatestVersion ?? "(none)");
        }

        return listing;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>Resolution strategy varies by <see cref="VersionType"/>:</para>
    /// <list type="bullet">
    ///   <item><description><see cref="VersionType.Exact"/>: direct lookup in the versions dictionary.</description></item>
    ///   <item><description><see cref="VersionType.Latest"/>: uses the <c>dist-tags.latest</c> value.</description></item>
    ///   <item><description><see cref="VersionType.Wildcard"/>: uses <see cref="FhirSemVer.MaxSatisfying"/> against all versions.</description></item>
    ///   <item><description><see cref="VersionType.Range"/>: uses <see cref="FhirSemVer.SatisfyingRange"/> and picks the highest match.</description></item>
    /// </list>
    /// </remarks>
    public override async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        Logger.LogInformation(
            "Resolving {PackageId} ({VersionType}: {Version}) on FHIR NPM registry",
            directive.PackageId, directive.VersionType, directive.RequestedVersion ?? "latest");

        PackageListing? listing = await GetPackageListingAsync(directive.PackageId, cancellationToken)
            .ConfigureAwait(false);

        if (listing?.Versions is null or { Count: 0 })
        {
            Logger.LogWarning("Package {PackageId} not found or has no versions", directive.PackageId);
            return null;
        }

        string? resolvedVersion = ResolveVersion(directive, listing, options);

        if (resolvedVersion is null)
        {
            Logger.LogWarning(
                "No version satisfying {VersionType} '{Specifier}' found for {PackageId}",
                directive.VersionType, directive.RequestedVersion, directive.PackageId);
            return null;
        }

        if (!listing.Versions.TryGetValue(resolvedVersion, out PackageVersionInfo? versionInfo))
        {
            Logger.LogWarning(
                "Resolved version {Version} is not in the versions dictionary for {PackageId}",
                resolvedVersion, directive.PackageId);
            return null;
        }

        string tarballUrl = versionInfo.Distribution?.TarballUrl
            ?? $"{BaseUrl}/{Uri.EscapeDataString(directive.PackageId)}"
             + $"/-/{Uri.EscapeDataString(directive.PackageId)}-{resolvedVersion}.tgz";

        Logger.LogInformation(
            "Resolved {PackageId} to version {Version} (tarball: {Tarball})",
            directive.PackageId, resolvedVersion, tarballUrl);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, resolvedVersion),
            TarballUri = new Uri(tarballUrl),
            ShaSum = versionInfo.Distribution?.ShaSum,
            SourceRegistry = Endpoint,
            PublicationDate = DateTime.TryParse(versionInfo.PublicationDate, out DateTime pubDate) ? pubDate : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Downloads the tarball from the URI specified in <see cref="ResolvedDirective.TarballUri"/>.
    /// The caller must dispose the returned <see cref="PackageDownloadResult"/>.
    /// </remarks>
    public override async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string url = resolved.TarballUri.ToString();
        Logger.LogInformation("Downloading tarball from {Url}", url);

        HttpResponseMessage? response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            Logger.LogWarning("Tarball not found at {Url}", url);
            return null;
        }

        try
        {
            PackageDownloadResult result = await CreateDownloadResultAsync(response, cancellationToken)
                .ConfigureAwait(false);

            Logger.LogDebug(
                "Download started: {ContentType}, {Length} bytes",
                result.ContentType, result.ContentLength ?? -1);

            return result;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends <c>PUT {baseUrl}/{name}</c> with the tarball stream as the request body.
    /// Requires authentication to be configured on the endpoint.
    /// </remarks>
    public override async Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);

        string url = $"{BaseUrl}/{Uri.EscapeDataString(reference.Name)}";

        Logger.LogInformation(
            "Publishing {PackageId}@{Version} to {Url}",
            reference.Name, reference.Version, url);

        try
        {
            using HttpResponseMessage response = await PutStreamAsync(url, tarballStream, "application/gzip", cancellationToken)
                .ConfigureAwait(false);

            Logger.LogInformation(
                "Published {PackageId}@{Version} successfully ({StatusCode})",
                reference.Name, reference.Version, (int)response.StatusCode);

            return new PublishResult
            {
                Success = true,
                StatusCode = response.StatusCode,
                Message = $"Package {reference.Name}@{reference.Version} published successfully.",
            };
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(
                ex, "Failed to publish {PackageId}@{Version}", reference.Name, reference.Version);

            return new PublishResult
            {
                Success = false,
                StatusCode = ex.StatusCode ?? HttpStatusCode.InternalServerError,
                Message = ex.Message,
            };
        }
    }

}
