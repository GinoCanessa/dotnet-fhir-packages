// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Registry client for the FHIR CI build server at <c>build.fhir.org</c>.
/// </summary>
/// <remarks>
/// <para>
/// CI builds are resolved using the <c>qas.json</c> index for IG packages and fixed URL patterns
/// for core packages. The <c>qas.json</c> response is cached in memory with a configurable TTL
/// to avoid excessive network traffic.
/// </para>
/// <para>URL patterns:</para>
/// <list type="bullet">
///   <item><description>IG packages (default branch): <c>{baseUrl}/ig/{org}/{repo}/package.tgz</c></description></item>
///   <item><description>IG packages (specific branch): <c>{baseUrl}/ig/{org}/{repo}/branches/{branch}/package.tgz</c></description></item>
///   <item><description>Core packages (default branch): <c>{baseUrl}/{packageName}.tgz</c></description></item>
///   <item><description>Core packages (specific branch): <c>{baseUrl}/branches/{branch}/{packageName}.tgz</c></description></item>
///   <item><description>Core manifest: <c>{baseUrl}/{packageName}.manifest.json</c></description></item>
/// </list>
/// </remarks>
public sealed class FhirCiBuildClient : RegistryClientBase, IRegistryClient
{
    private static readonly TimeSpan QasCacheDuration = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _qasCacheLock = new(1, 1);
    private IReadOnlyList<CiBuildRecord>? _qasCache;
    private DateTimeOffset _qasCacheExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// Initialises a new <see cref="FhirCiBuildClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The CI build endpoint (typically <see cref="RegistryEndpoint.FhirCiBuild"/>).</param>
    /// <param name="logger">The logger instance.</param>
    public FhirCiBuildClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<FhirCiBuildClient> logger)
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
        VersionType.CiBuild,
        VersionType.CiBuildBranch,
    ];

    // ── IRegistryClient methods ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// CI build registries do not support catalog search. Returns an empty list.
    /// </remarks>
    public Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("SearchAsync is not supported for CI builds; returning empty list");
        return Task.FromResult<IReadOnlyList<CatalogEntry>>([]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// CI build registries do not support package listings. Returns <see langword="null"/>.
    /// </remarks>
    public Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("GetPackageListingAsync is not supported for CI builds; returning null");
        return Task.FromResult<PackageListing?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// For <see cref="PackageNameType.CoreFull"/> or <see cref="PackageNameType.CorePartial"/>
    /// packages, the tarball URL is constructed from fixed patterns. For IG packages, the
    /// <c>qas.json</c> index is consulted to find the repository and latest build date.
    /// </para>
    /// <para>
    /// When <see cref="VersionType.CiBuildBranch"/> is used, the
    /// <see cref="PackageDirective.CiBranch"/> value selects a specific branch build.
    /// </para>
    /// </remarks>
    public async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        Logger.LogInformation(
            "Resolving CI build for {PackageId} ({VersionType}, branch: {Branch})",
            directive.PackageId,
            directive.VersionType,
            directive.CiBranch ?? "(default)");

        if (directive.NameType is PackageNameType.CoreFull or PackageNameType.CorePartial)
        {
            return ResolveCorePackage(directive);
        }

        return await ResolveIgPackageAsync(directive, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Downloads the tarball from the URI specified in <see cref="ResolvedDirective.TarballUri"/>.
    /// The caller must dispose the returned <see cref="PackageDownloadResult"/>.
    /// </remarks>
    public async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var url = resolved.TarballUri.ToString();
        Logger.LogInformation("Downloading CI build tarball from {Url}", url);

        var response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            Logger.LogWarning("CI build tarball not found at {Url}", url);
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
    /// Publishing is not supported for CI build registries. Always returns a failure result.
    /// </remarks>
    public Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("Publishing to CI build registries is not supported");

        return Task.FromResult(new PublishResult
        {
            Success = false,
            StatusCode = System.Net.HttpStatusCode.MethodNotAllowed,
            Message = "CI build registries do not support publishing.",
        });
    }

    // ── Core package resolution ─────────────────────────────────────────

    private ResolvedDirective ResolveCorePackage(PackageDirective directive)
    {
        string tarballUrl;

        if (directive.VersionType is VersionType.CiBuildBranch && directive.CiBranch is not null)
        {
            tarballUrl = $"{BaseUrl}/branches/{Uri.EscapeDataString(directive.CiBranch)}" +
                         $"/{Uri.EscapeDataString(directive.PackageId)}.tgz";
        }
        else
        {
            tarballUrl = $"{BaseUrl}/{Uri.EscapeDataString(directive.PackageId)}.tgz";
        }

        Logger.LogInformation(
            "Resolved core CI build {PackageId} → {TarballUrl}",
            directive.PackageId, tarballUrl);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, "current"),
            TarballUri = new Uri(tarballUrl),
            SourceRegistry = Endpoint,
        };
    }

    // ── IG package resolution via qas.json ──────────────────────────────

    private async Task<ResolvedDirective?> ResolveIgPackageAsync(
        PackageDirective directive, CancellationToken cancellationToken)
    {
        var records = await GetQasRecordsAsync(cancellationToken).ConfigureAwait(false);

        var matching = records.Where(r =>
            string.Equals(r.PackageId, directive.PackageId, StringComparison.OrdinalIgnoreCase));

        if (directive.VersionType is VersionType.CiBuildBranch && directive.CiBranch is not null)
        {
            matching = matching.Where(r =>
            {
                var parsed = r.ParseRepo();
                if (parsed is null) return false;
                var (_, _, branch) = parsed.Value;
                return string.Equals(branch, directive.CiBranch, StringComparison.OrdinalIgnoreCase);
            });
        }

        var newest = matching
            .OrderByDescending(r => r.DateISO8601 ?? r.Date)
            .FirstOrDefault();

        if (newest is null)
        {
            Logger.LogWarning("No CI build record found for {PackageId}", directive.PackageId);
            return null;
        }

        var repoParsed = newest.ParseRepo();
        if (repoParsed is null)
        {
            Logger.LogWarning("Unable to parse repo field for CI build record of {PackageId}", directive.PackageId);
            return null;
        }

        var (org, repo, _) = repoParsed.Value;
        string tarballUrl;

        if (directive.VersionType is VersionType.CiBuildBranch && directive.CiBranch is not null)
        {
            tarballUrl = $"{BaseUrl}/ig/{Uri.EscapeDataString(org)}" +
                         $"/{Uri.EscapeDataString(repo)}" +
                         $"/branches/{Uri.EscapeDataString(directive.CiBranch)}/package.tgz";
        }
        else
        {
            tarballUrl = $"{BaseUrl}/ig/{Uri.EscapeDataString(org)}" +
                         $"/{Uri.EscapeDataString(repo)}/package.tgz";
        }

        Logger.LogInformation(
            "Resolved IG CI build {PackageId} → {TarballUrl} (build date: {Date})",
            directive.PackageId, tarballUrl, newest.Date);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, newest.IgVersion ?? "current"),
            TarballUri = new Uri(tarballUrl),
            SourceRegistry = Endpoint,
            PublicationDate = TryParseDate(newest.DateISO8601 ?? newest.Date),
        };
    }

    // ── qas.json caching ────────────────────────────────────────────────

    /// <summary>
    /// Downloads and caches the <c>qas.json</c> index, refreshing it when the cache expires.
    /// </summary>
    private async Task<IReadOnlyList<CiBuildRecord>> GetQasRecordsAsync(
        CancellationToken cancellationToken)
    {
        if (_qasCache is not null && DateTimeOffset.UtcNow < _qasCacheExpiry)
            return _qasCache;

        await _qasCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Double-check after acquiring the lock.
            if (_qasCache is not null && DateTimeOffset.UtcNow < _qasCacheExpiry)
                return _qasCache;

            Logger.LogInformation("Downloading qas.json from {BaseUrl}", BaseUrl);

            var url = $"{BaseUrl}/ig/qas.json";
            var records = await GetJsonAsync<List<CiBuildRecord>>(url, cancellationToken)
                .ConfigureAwait(false);

            _qasCache = records?.AsReadOnly() ?? (IReadOnlyList<CiBuildRecord>)[];
            _qasCacheExpiry = DateTimeOffset.UtcNow.Add(QasCacheDuration);

            Logger.LogDebug("Cached {Count} QA records (expires at {Expiry})",
                _qasCache.Count, _qasCacheExpiry);

            return _qasCache;
        }
        finally
        {
            _qasCacheLock.Release();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static DateTime? TryParseDate(string? dateString)
    {
        if (dateString is null)
            return null;

        return DateTime.TryParse(dateString, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
    }
}
