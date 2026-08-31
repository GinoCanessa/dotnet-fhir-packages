// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using FhirPkg.Models;
using FhirPkg.Registry.CiBuild;
using FhirPkg.Resolution;
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
/// <para>
/// IG resolution runs a small policy pipeline: <c>qas.json</c> records are projected into
/// <see cref="CiBuildCandidate"/> values, <see cref="CiBuildCanonicalRepositorySelector"/>
/// chooses the canonical publishing repository, and <see cref="CiBuildArtifactResolver"/>
/// turns that choice into a tarball location. Selections that are not the canonical
/// repository's default build are reported through
/// <see cref="ResolvedDirective.ResolutionWarnings"/> as well as the logger.
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
public sealed class FhirCiBuildClient : RegistryClientBase, IRegistryClient, ICiBuildManifestSource
{
    private static readonly TimeSpan QasCacheDuration = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _qasCacheLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly CiBuildCanonicalRepositorySelector _selector;
    private readonly CiBuildArtifactResolver _artifactResolver;
    private IReadOnlyList<CiBuildRecord>? _qasCache;
    private DateTimeOffset _qasCacheExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// Initialises a new <see cref="FhirCiBuildClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The CI build endpoint (typically <see cref="RegistryEndpoint.FhirCiBuild"/>).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
    public FhirCiBuildClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<FhirCiBuildClient> logger,
        TimeProvider? timeProvider = null)
        : base(httpClient, endpoint, logger)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        // This constructor can only ever produce an unverified transport, on which a
        // GitHub token is refused by design; it therefore passes none.
        _selector = new CiBuildCanonicalRepositorySelector(
            new GitHubRepositoryFactsProvider(
                RegistryHttpTransport.CreateUnverified(httpClient),
                logger),
            logger);
        _artifactResolver = new CiBuildArtifactResolver(BaseUrl, this, logger);
    }

    internal FhirCiBuildClient(
        RegistryHttpTransport transport,
        RegistryEndpoint endpoint,
        ILogger<FhirCiBuildClient> logger,
        TimeProvider? timeProvider = null,
        string? gitHubToken = null)
        : base(transport, endpoint, logger)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _selector = new CiBuildCanonicalRepositorySelector(
            new GitHubRepositoryFactsProvider(transport, logger, gitHubToken),
            logger);
        _artifactResolver = new CiBuildArtifactResolver(BaseUrl, this, logger);
    }

    internal FhirCiBuildClient(
        RegistryHttpTransport transport,
        RegistryEndpoint endpoint,
        ILogger<FhirCiBuildClient> logger,
        IGitHubRepositoryFactsProvider factsProvider,
        TimeProvider? timeProvider = null)
        : base(transport, endpoint, logger)
    {
        ArgumentNullException.ThrowIfNull(factsProvider);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _selector = new CiBuildCanonicalRepositorySelector(factsProvider, logger);
        _artifactResolver = new CiBuildArtifactResolver(BaseUrl, this, logger);
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
    public override Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("SearchAsync is not supported for CI builds; returning empty list");
        return Task.FromResult<IReadOnlyList<CatalogEntry>>([]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// CI build registries do not support package listings. Returns <see langword="null"/>.
    /// </remarks>
    public override Task<PackageListing?> GetPackageListingAsync(
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
    public override async Task<ResolvedDirective?> ResolveAsync(
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

        if (options?.AllowPreRelease == false)
        {
            Logger.LogDebug(
                "CI build resolution for {PackageId} was skipped because pre-release versions are disabled",
                directive.PackageId);
            return null;
        }

        if (options?.FhirRelease is FhirRelease preferredRelease
            && directive.NameType is PackageNameType.CoreFull or PackageNameType.CorePartial
            && FhirReleaseMapping.FromPackageName(directive.PackageId) != preferredRelease)
        {
            return null;
        }

        if (directive.NameType is PackageNameType.CoreFull or PackageNameType.CorePartial)
        {
            return ResolveCorePackage(directive);
        }

        return await ResolveIgPackageAsync(directive, options, cancellationToken).ConfigureAwait(false);
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
        Logger.LogInformation("Downloading CI build tarball from {Url}", url);

        HttpResponseMessage? response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

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
    public override Task<PublishResult> PublishAsync(
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
            SourceRegistry = Endpoint.ToProvenance(),
            SourceClient = this,
        };
    }

    // ── IG package resolution via qas.json ──────────────────────────────

    private async Task<ResolvedDirective?> ResolveIgPackageAsync(
        PackageDirective directive,
        VersionResolveOptions? options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CiBuildRecord> records = await GetQasRecordsAsync(cancellationToken).ConfigureAwait(false);

        List<CiBuildRecord> packageMatches = records
            .Where(r => string.Equals(r.PackageId, directive.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Captured through the same parsing basis used below, so an unparseable record
        // can never be misattributed to the FHIR-release filter.
        HashSet<string> preReleaseFilterOrganizations = new(StringComparer.OrdinalIgnoreCase);
        foreach (CiBuildRecord record in packageMatches)
        {
            if (CiBuildCandidate.TryCreate(record) is CiBuildCandidate parsed)
                preReleaseFilterOrganizations.Add(parsed.Repository.Org);
        }

        IEnumerable<CiBuildRecord> matching = packageMatches;

        if (options?.FhirRelease is FhirRelease preferredRelease)
        {
            matching = matching.Where(record =>
                record.FhirVersion is string fhirVersion
                    ? FhirReleaseExtractor.TryMap(fhirVersion, out FhirRelease release)
                        && release == preferredRelease
                    : FhirReleaseMapping.FromPackageName(record.PackageId) == preferredRelease);
        }

        List<CiBuildCandidate> candidates = [];
        foreach (CiBuildRecord record in matching)
        {
            CiBuildCandidate? candidate = CiBuildCandidate.TryCreate(record);
            if (candidate is null)
            {
                Logger.LogDebug(
                    "Discarding CI build record for {PackageId} with unparseable repo '{Repo}'",
                    directive.PackageId, record.Repo);
                continue;
            }

            candidates.Add(candidate);
        }

        // Warning condition 3 needs the organization count before any branch filter.
        int publisherCount = candidates
            .Select(c => c.Repository.Org)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        string? requestedBranch =
            directive.VersionType is VersionType.CiBuildBranch && directive.CiBranch is not null
                ? directive.CiBranch
                : null;

        bool tableNamesOrganization = CanonicalOrganizationTable.TryGetOrganization(
            directive.PackageId, out string canonicalOrganization);

        // Warning condition 4: an explicit branch request is a deliberate choice and is
        // never second-guessed with a warning about an organization the caller did not ask for.
        bool canonicalOrganizationFilteredOut =
            requestedBranch is null
            && tableNamesOrganization
            && preReleaseFilterOrganizations.Contains(canonicalOrganization)
            && !candidates.Any(c =>
                string.Equals(c.Repository.Org, canonicalOrganization, StringComparison.OrdinalIgnoreCase));

        List<CiBuildCandidate> selectionCandidates = requestedBranch is null
            ? candidates
            : candidates
                .Where(c => string.Equals(c.Branch, requestedBranch, StringComparison.OrdinalIgnoreCase))
                .ToList();

        CiBuildRepositorySelection? selection = await _selector
            .SelectAsync(directive.PackageId, selectionCandidates, cancellationToken)
            .ConfigureAwait(false);

        if (selection is null)
        {
            Logger.LogWarning("No CI build record found for {PackageId}", directive.PackageId);
            return null;
        }

        CiBuildArtifactLocation? location = await _artifactResolver
            .ResolveAsync(selection.Repository, selectionCandidates, requestedBranch, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            Logger.LogWarning("No CI build record found for {PackageId}", directive.PackageId);
            return null;
        }

        IReadOnlyList<string>? warnings = BuildResolutionWarnings(
            directive,
            requestedBranch,
            selection,
            location,
            publisherCount,
            tableNamesOrganization ? canonicalOrganization : null,
            canonicalOrganizationFilteredOut);

        Logger.LogInformation(
            "Resolved IG CI build {PackageId} → {TarballUrl} (repository: {Repository}, tier: {Tier})",
            directive.PackageId, location.TarballUri, selection.Repository, selection.Tier);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, location.Version ?? "current"),
            TarballUri = location.TarballUri,
            SourceRegistry = Endpoint.ToProvenance(),
            SourceClient = this,
            PublicationDate = location.PublicationDate?.UtcDateTime,
            FhirVersions = location.FhirVersions,
            ResolutionWarnings = warnings,
        };
    }

    /// <summary>
    /// Builds the non-fatal diagnostics describing how a CI build source was chosen.
    /// </summary>
    /// <remarks>
    /// Conditions may legitimately co-fire and are deliberately not de-duplicated —
    /// each describes a different aspect of the same selection.
    /// </remarks>
    private IReadOnlyList<string>? BuildResolutionWarnings(
        PackageDirective directive,
        string? requestedBranch,
        CiBuildRepositorySelection selection,
        CiBuildArtifactLocation location,
        int publisherCount,
        string? canonicalOrganization,
        bool canonicalOrganizationFilteredOut)
    {
        List<string> warnings = [];

        string directiveText = requestedBranch is null
            ? $"{directive.PackageId}@current"
            : $"{directive.PackageId}@current${requestedBranch}";

        // Condition 1 keys off the artifact result, never off why the manifest was
        // rejected, so a future fallback cannot be added silently.
        if (requestedBranch is null && !location.IsDefaultBuild)
        {
            warnings.Add(
                $"Resolved '{directiveText}' to {selection.Repository} (branch '{location.Branch ?? "unknown"}'), " +
                "which is not the canonical repository's default build.");
        }

        if (requestedBranch is null && selection.Tier is CiBuildSelectionTier.Oldest)
        {
            warnings.Add(
                $"Resolved '{directiveText}' to {selection.Repository} (branch '{location.Branch ?? "default"}') " +
                "by falling back to the oldest CI build: no canonical-organization rule applied and " +
                "GitHub repository facts were unavailable.");
        }

        if (requestedBranch is not null
            && publisherCount > 1
            && !string.Equals(selection.Repository.Org, canonicalOrganization, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"Resolved '{directiveText}' to {selection.Repository} (branch '{location.Branch ?? requestedBranch}'), " +
                $"which is not the canonical publisher; {publisherCount} organizations publish CI builds for " +
                $"'{directive.PackageId}'.");
        }

        if (canonicalOrganizationFilteredOut)
        {
            warnings.Add(
                $"The canonical organization '{canonicalOrganization}' publishes CI builds for " +
                $"'{directive.PackageId}', but the requested FHIR release excluded all of them; " +
                $"resolved '{directiveText}' to {selection.Repository} (branch '{location.Branch ?? "default"}') instead.");
        }

        if (warnings.Count == 0)
            return null;

        foreach (string warning in warnings)
        {
            Logger.LogWarning("{ResolutionWarning}", warning);
        }

        return warnings;
    }

    // ── ICiBuildManifestSource ──────────────────────────────────────────

    /// <inheritdoc />
    async Task<CiBuildManifest?> ICiBuildManifestSource.TryGetDefaultBuildManifestAsync(
        CiBuildRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        string url = $"{BaseUrl}/ig/{repository.ToUrlPath()}/package.manifest.json";

        try
        {
            // GetJsonAsync returns null only for a 404; a 5xx surfaces as
            // HttpRequestException and a stalled body as RegistryResponseTimeoutException.
            return await GetJsonAsync<CiBuildManifest>(url, cancellationToken).ConfigureAwait(false);
        }
        catch (RegistryResponseTimeoutException exception)
        {
            Logger.LogDebug(
                "Timed out reading package.manifest.json for {Repository}: {Message}",
                repository, exception.Message);
            return null;
        }
        catch (HttpRequestException exception)
        {
            Logger.LogDebug(
                "Failed to read package.manifest.json for {Repository}: {Message}",
                repository, exception.Message);
            return null;
        }
        catch (JsonException exception)
        {
            Logger.LogDebug(
                "package.manifest.json for {Repository} was unreadable: {Message}",
                repository, exception.Message);
            return null;
        }
    }

    // ── qas.json caching ────────────────────────────────────────────────

    /// <summary>
    /// Downloads and caches the <c>qas.json</c> index, refreshing it when the cache expires.
    /// </summary>
    private async Task<IReadOnlyList<CiBuildRecord>> GetQasRecordsAsync(
        CancellationToken cancellationToken)
    {
        await _qasCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Check under lock to prevent torn reads
            if (_qasCache is not null && _timeProvider.GetUtcNow() < _qasCacheExpiry)
                return _qasCache;

            Logger.LogInformation("Downloading qas.json from {BaseUrl}", BaseUrl);

            string url = $"{BaseUrl}/ig/qas.json";
            List<CiBuildRecord>? records = await GetJsonAsync<List<CiBuildRecord>>(url, cancellationToken)
                .ConfigureAwait(false);

            _qasCache = records?.AsReadOnly() ?? (IReadOnlyList<CiBuildRecord>)[];
            _qasCacheExpiry = _timeProvider.GetUtcNow().Add(QasCacheDuration);

            Logger.LogDebug("Cached {Count} QA records (expires at {Expiry})",
                _qasCache.Count, _qasCacheExpiry);

            return _qasCache;
        }
        finally
        {
            _qasCacheLock.Release();
        }
    }
}
