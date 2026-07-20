// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using FhirPkg.Models;
using FhirPkg.Resolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirPkg.Registry;

/// <summary>
/// A composite <see cref="IRegistryClient"/> that merges registry knowledge while
/// preserving source-coherent package metadata and distinguishing absence from failure.
/// </summary>
/// <remarks>
/// <para>Fallback behaviour per method:</para>
/// <list type="bullet">
///   <item><description>
///     <see cref="SearchAsync"/>: queries <b>all</b> clients and merges results, deduplicating by package name.
///   </description></item>
///   <item><description>
///     <see cref="GetPackageListingAsync"/>: queries eligible clients under the
///     configured concurrency limit and merges successful listings.
///   </description></item>
///   <item><description>
///     <see cref="ResolveAsync"/>: selects from merged source candidates and fails
///     when an incomplete listing cannot support an authoritative selection.
///   </description></item>
///   <item><description>
///     <see cref="DownloadAsync"/>: honors source provenance, with legacy fallback
///     only for directives that lack it.
///   </description></item>
///   <item><description>
///     <see cref="PublishAsync"/>: tries clients in order; returns the first successful result or
///     throws the last exception if all fail.
///   </description></item>
/// </list>
/// <para>
/// Individual failures are logged with their original exception and exposed publicly
/// only as sanitized <see cref="RegistryAttemptFailure"/> snapshots.
/// <see cref="OperationCanceledException"/> always propagates immediately.
/// Nested redundant clients are flattened so one concurrency limit applies to all
/// leaf registry operations.
/// </para>
/// </remarks>
public sealed class RedundantRegistryClient : IRegistryClient
{
    private readonly IReadOnlyList<IRegistryClient> _clients;
    private readonly ILogger<RedundantRegistryClient> _logger;
    private readonly int _maxParallelRegistryQueries;

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
        : this(clients, maxParallelRegistryQueries: 3, logger)
    {
    }

    internal RedundantRegistryClient(
        IEnumerable<IRegistryClient> clients,
        int maxParallelRegistryQueries,
        ILogger<RedundantRegistryClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelRegistryQueries, 1);

        List<IRegistryClient> flattenedClients = [];
        foreach (IRegistryClient client in clients)
        {
            if (client is RedundantRegistryClient redundantClient)
            {
                flattenedClients.AddRange(redundantClient._clients);
            }
            else
            {
                flattenedClients.Add(client);
            }
        }

        _clients = flattenedClients.AsReadOnly();

        if (_clients.Count == 0)
            throw new ArgumentException("At least one registry client is required.", nameof(clients));

        _logger = logger ?? NullLogger<RedundantRegistryClient>.Instance;
        _maxParallelRegistryQueries = maxParallelRegistryQueries;

        // Cache computed properties (the client list is immutable)
        _supportedNameTypes = _clients.SelectMany(c => c.SupportedNameTypes).Distinct().ToList().AsReadOnly();
        _supportedVersionTypes = _clients.SelectMany(c => c.SupportedVersionTypes).Distinct().ToList().AsReadOnly();
    }

    /// <summary>
    /// Initialises a new <see cref="RedundantRegistryClient"/> with the specified clients.
    /// </summary>
    /// <param name="clients">The registry clients to try, in priority order.</param>
    public RedundantRegistryClient(params IRegistryClient[] clients)
        : this((IEnumerable<IRegistryClient>)clients)
    {
    }

    private readonly IReadOnlyList<PackageNameType> _supportedNameTypes;
    private readonly IReadOnlyList<VersionType> _supportedVersionTypes;

    // ── IRegistryClient properties ──────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>Returns the endpoint of the first (highest-priority) client.</remarks>
    public RegistryEndpoint Endpoint => _clients[0].Endpoint;

    /// <inheritdoc />
    /// <remarks>Returns the union of all clients' supported name types, deduplicated.</remarks>
    public IReadOnlyList<PackageNameType> SupportedNameTypes => _supportedNameTypes;

    /// <inheritdoc />
    /// <remarks>Returns the union of all clients' supported version types, deduplicated.</remarks>
    public IReadOnlyList<VersionType> SupportedVersionTypes => _supportedVersionTypes;

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
        ArgumentNullException.ThrowIfNull(criteria);

        IReadOnlyList<SearchAttempt> attempts = await QuerySearchAsync(
                criteria,
                cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<CatalogEntry> merged = [];
        List<RegistryAttemptFailure> failures = [];

        foreach (SearchAttempt attempt in attempts.OrderBy(attempt => attempt.Priority))
        {
            failures.AddRange(attempt.Failures);
            foreach (CatalogEntry entry in attempt.Results)
            {
                if (seen.Add(entry.Name))
                {
                    merged.Add(entry);
                }
            }
        }

        if (merged.Count == 0 && failures.Count > 0)
        {
            throw new RegistryOperationException(
                "search",
                string.IsNullOrWhiteSpace(criteria.Name) ? "catalog" : criteria.Name,
                failures);
        }

        _logger.LogInformation("Merged search returned {Count} unique result(s)", merged.Count);

        return merged.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Queries eligible clients concurrently and merges their package metadata.
    /// </remarks>
    public async Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        PackageNameType nameType = PackageDirective.ClassifyName(packageId);
        IReadOnlyList<IRegistryClient> eligibleClients = _clients
            .Where(client => client.SupportedNameTypes.Contains(nameType))
            .ToArray();
        MergedListingState? merged = await QueryAndMergeListingsAsync(
                packageId,
                eligibleClients,
                cancellationToken)
            .ConfigureAwait(false);
        return merged?.Listing;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Queries eligible clients and selects one source-coherent package candidate.
    /// </remarks>
    public async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        IReadOnlyList<IRegistryClient> eligibleClients = _clients
            .Where(client =>
                client.SupportedVersionTypes.Contains(directive.VersionType)
                && client.SupportedNameTypes.Contains(directive.NameType))
            .ToArray();

        if (eligibleClients.Count == 0)
        {
            return null;
        }

        if (directive.VersionType is VersionType.CiBuild
            or VersionType.CiBuildBranch
            or VersionType.LocalBuild)
        {
            return await ResolveDirectAsync(
                    directive,
                    options,
                    eligibleClients,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        MergedListingState? merged = await QueryAndMergeListingsAsync(
                directive.PackageId,
                eligibleClients,
                cancellationToken)
            .ConfigureAwait(false);
        if (merged is null)
        {
            return await ResolveDirectAsync(
                    directive,
                    options,
                    eligibleClients,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!merged.Listing.IsComplete
            && directive.VersionType != VersionType.Exact)
        {
            throw CreateIncompleteResolutionException(merged.Listing);
        }

        SourceSelection? sourceSelection = SelectSourceCandidate(
            directive,
            options,
            merged.Listing);
        if (sourceSelection is null)
        {
            if (!merged.Listing.IsComplete)
            {
                throw CreateIncompleteResolutionException(merged.Listing);
            }

            return null;
        }

        ResolvedDirective resolved = await CreateResolvedDirectiveAsync(
                directive,
                options,
                sourceSelection,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Resolved {PackageId} to {Version} via {Endpoint}",
            directive.PackageId,
            resolved.Reference.Version,
            resolved.SourceRegistry?.Url);
        return resolved;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the resolving source when provenance is available; otherwise tries clients in order.
    /// </remarks>
    public async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (resolved.SourceRegistry is RegistryEndpoint sourceRegistry)
        {
            IReadOnlyList<IRegistryClient> sourceClients =
                FindSourceClients(
                    sourceRegistry,
                    resolved.SourceClient);
            if (sourceClients.Count != 1)
            {
                throw CreateSourceRoutingException(
                    "download",
                    resolved.Reference.Name,
                    sourceRegistry);
            }

            IRegistryClient sourceClient = sourceClients[0];
            _logger.LogDebug(
                "Downloading {PackageId} only from resolving source {Endpoint}",
                resolved.Reference.Name,
                sourceClient.Endpoint.Url);
            try
            {
                return await sourceClient.DownloadAsync(
                        resolved,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Download failed on resolving source {Endpoint} for {PackageId}",
                    sourceClient.Endpoint.Url,
                    resolved.Reference.Name);
                throw new RegistryOperationException(
                    "download",
                    resolved.Reference.Name,
                    CaptureFailures(sourceClient, exception));
            }
        }

        List<RegistryAttemptFailure> failures = [];

        foreach (IRegistryClient client in _clients)
        {
            try
            {
                _logger.LogDebug(
                    "Downloading {PackageId} from {Endpoint}",
                    resolved.Reference.Name, client.Endpoint.Url);

                PackageDownloadResult? result = await client.DownloadAsync(resolved, cancellationToken)
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
                failures.AddRange(CaptureFailures(client, ex));
                _logger.LogWarning(
                    ex,
                    "Download failed on {Endpoint} for {PackageId}; trying next registry",
                    client.Endpoint.Url, resolved.Reference.Name);
            }
        }

        if (failures.Count > 0)
        {
            throw new RegistryOperationException(
                "download",
                resolved.Reference.Name,
                failures);
        }

        return null;
    }

    private async Task<IReadOnlyList<SearchAttempt>> QuerySearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim semaphore = new(_maxParallelRegistryQueries);
        Task<SearchAttempt>[] tasks = _clients
            .Select((client, priority) => ExecuteSearchAttemptAsync(
                client,
                priority,
                criteria,
                semaphore,
                cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<SearchAttempt> ExecuteSearchAttemptAsync(
        IRegistryClient client,
        int priority,
        PackageSearchCriteria criteria,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Searching {Endpoint} for packages", client.Endpoint.Url);
            IReadOnlyList<CatalogEntry> results = await client.SearchAsync(
                    criteria,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SearchAttempt(priority, results, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Search failed on {Endpoint}",
                client.Endpoint.Url);
            return new SearchAttempt(
                priority,
                [],
                CaptureFailures(client, exception));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<MergedListingState?> QueryAndMergeListingsAsync(
        string packageId,
        IReadOnlyList<IRegistryClient> clients,
        CancellationToken cancellationToken)
    {
        if (clients.Count == 0)
        {
            return null;
        }

        using SemaphoreSlim semaphore = new(_maxParallelRegistryQueries);
        Task<ListingAttempt>[] tasks = clients
            .Select((client, priority) => ExecuteListingAttemptAsync(
                client,
                priority,
                packageId,
                semaphore,
                cancellationToken))
            .ToArray();
        IReadOnlyList<ListingAttempt> attempts = await Task.WhenAll(tasks)
            .ConfigureAwait(false);
        List<ListingAttempt> positiveAttempts = attempts
            .Where(attempt => attempt.Listing is not null)
            .OrderBy(attempt => attempt.Priority)
            .ToList();
        List<RegistryAttemptFailure> failures = attempts
            .SelectMany(attempt => attempt.Failures)
            .ToList();

        if (positiveAttempts.Count == 0)
        {
            if (failures.Count > 0)
            {
                throw new RegistryOperationException(
                    "get-package-listing",
                    packageId,
                    failures);
            }

            return null;
        }

        return MergeListings(packageId, positiveAttempts, failures);
    }

    private async Task<ListingAttempt> ExecuteListingAttemptAsync(
        IRegistryClient client,
        int priority,
        string packageId,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug(
                "Fetching listing for {PackageId} from {Endpoint}",
                packageId,
                client.Endpoint.Url);
            PackageListing? listing = await client.GetPackageListingAsync(
                    packageId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ListingAttempt(priority, client, listing, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "GetPackageListing failed on {Endpoint} for {PackageId}",
                client.Endpoint.Url,
                packageId);
            return new ListingAttempt(
                priority,
                client,
                null,
                CaptureFailures(client, exception));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static MergedListingState MergeListings(
        string packageId,
        IReadOnlyList<ListingAttempt> positiveAttempts,
        List<RegistryAttemptFailure> failures)
    {
        Dictionary<string, PackageVersionInfo> versions =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> distTags =
            new(StringComparer.OrdinalIgnoreCase);
        List<PackageVersionInfo> candidates = [];
        string? description = null;

        foreach (ListingAttempt attempt in positiveAttempts)
        {
            PackageListing listing = attempt.Listing!;
            description ??= listing.Description;
            failures.AddRange(listing.QueryFailures);

            if (listing.DistTags is not null)
            {
                foreach ((string tag, string version) in listing.DistTags)
                {
                    if (!tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
                    {
                        distTags.TryAdd(tag, version);
                    }
                }
            }

            if (listing.VersionCandidates.Count > 0)
            {
                foreach (PackageVersionInfo sourceCandidate in listing.VersionCandidates)
                {
                    PackageVersionInfo candidate = sourceCandidate with
                    {
                        SourceRegistry =
                            (sourceCandidate.SourceRegistry
                            ?? listing.SourceRegistry
                            ?? attempt.Client.Endpoint).ToProvenance(),
                        SourceClient =
                            sourceCandidate.SourceClient
                            ?? attempt.Client,
                    };
                    candidates.Add(candidate);
                    versions.TryAdd(candidate.Version, candidate);
                }

                continue;
            }

            foreach ((string key, PackageVersionInfo versionInfo) in listing.Versions)
            {
                bool isLatest = listing.DistTags is not null
                    && listing.DistTags.TryGetValue("latest", out string? latest)
                    && key.Equals(latest, StringComparison.OrdinalIgnoreCase);
                PackageVersionInfo candidate = versionInfo with
                {
                    Version = key,
                    SourceRegistry =
                        (versionInfo.SourceRegistry
                        ?? listing.SourceRegistry
                        ?? attempt.Client.Endpoint).ToProvenance(),
                    SourceClient =
                        versionInfo.SourceClient
                        ?? attempt.Client,
                    IsSourceLatest = versionInfo.IsSourceLatest || isLatest,
                };
                candidates.Add(candidate);
                versions.TryAdd(key, candidate);
            }
        }

        PackageVersionInfo? highestSourceLatest = candidates
            .Where(candidate => candidate.IsSourceLatest)
            .Select(candidate => new
            {
                Candidate = candidate,
                Version = FhirSemVer.TryParse(
                    candidate.Version,
                    out FhirSemVer? parsed)
                    ? parsed
                    : null,
            })
            .Where(item => item.Version is not null)
            .MaxBy(item => item.Version)
            ?.Candidate;
        if (highestSourceLatest is not null)
        {
            distTags["latest"] = highestSourceLatest.Version;
        }

        bool isComplete = failures.Count == 0
            && positiveAttempts.All(attempt => attempt.Listing!.IsComplete);
        RegistryEndpoint? sourceRegistry = positiveAttempts.Count == 1
            ? (positiveAttempts[0].Listing!.SourceRegistry
                ?? positiveAttempts[0].Client.Endpoint).ToProvenance()
            : null;
        PackageListing merged = new()
        {
            PackageId = packageId,
            Description = description,
            DistTags = distTags.Count == 0 ? null : distTags,
            Versions = versions,
            SourceRegistry = sourceRegistry,
            IsComplete = isComplete,
            QueryFailures = failures.ToArray(),
            VersionCandidates = candidates.ToArray(),
        };
        return new MergedListingState(merged);
    }

    private async Task<ResolvedDirective?> ResolveDirectAsync(
        PackageDirective directive,
        VersionResolveOptions? options,
        IReadOnlyList<IRegistryClient> clients,
        CancellationToken cancellationToken)
    {
        List<RegistryAttemptFailure> failures = [];
        foreach (IRegistryClient client in clients)
        {
            try
            {
                ResolvedDirective? resolved = await client.ResolveAsync(
                        directive,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is not null)
                {
                    RegistryEndpoint provenance =
                        (resolved.SourceRegistry ?? client.Endpoint)
                        .ToProvenance();
                    return resolved with
                    {
                        SourceRegistry = provenance,
                        SourceClient = resolved.SourceClient ?? client,
                    };
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.AddRange(CaptureFailures(client, exception));
                _logger.LogWarning(
                    exception,
                    "Resolve failed on {Endpoint} for {PackageId}",
                    client.Endpoint.Url,
                    directive.PackageId);
            }
        }

        if (failures.Count > 0)
        {
            throw new RegistryOperationException(
                "resolve",
                directive.PackageId,
                failures);
        }

        return null;
    }

    private static SourceSelection? SelectSourceCandidate(
        PackageDirective directive,
        VersionResolveOptions? options,
        PackageListing listing)
    {
        IReadOnlyList<PackageVersionInfo> sourceCandidates =
            listing.VersionCandidates.Count > 0
                ? listing.VersionCandidates
                : listing.Versions.Values.ToArray();
        List<PackageVersionInfo> eligibleCandidates = [];

        foreach (PackageVersionInfo candidate in sourceCandidates)
        {
            PackageDirective exactDirective =
                PackageDirective.Parse(
                    $"{directive.PackageId}#{candidate.Version}");
            PackageListing candidateListing = new()
            {
                PackageId = directive.PackageId,
                Versions = new Dictionary<string, PackageVersionInfo>
                {
                    [candidate.Version] = candidate,
                },
            };
            if (PackageVersionSelector.Select(
                    exactDirective,
                    candidateListing,
                    options) is not null)
            {
                eligibleCandidates.Add(candidate);
            }
        }

        if (eligibleCandidates.Count == 0)
        {
            return null;
        }

        Dictionary<string, PackageVersionInfo> versions =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageVersionInfo candidate in eligibleCandidates)
        {
            versions.TryAdd(candidate.Version, candidate);
        }

        Dictionary<string, string>? distTags = null;
        PackageVersionInfo? highestSourceLatest = eligibleCandidates
            .Where(candidate => candidate.IsSourceLatest)
            .Select(candidate => new
            {
                Candidate = candidate,
                Version = FhirSemVer.TryParse(
                    candidate.Version,
                    out FhirSemVer? parsed)
                    ? parsed
                    : null,
            })
            .Where(item => item.Version is not null)
            .MaxBy(item => item.Version)
            ?.Candidate;
        if (highestSourceLatest is not null)
        {
            distTags = new Dictionary<string, string>
            {
                ["latest"] = highestSourceLatest.Version,
            };
        }

        PackageListing selectionListing = new()
        {
            PackageId = directive.PackageId,
            DistTags = distTags,
            Versions = versions,
        };
        PackageVersionSelection? selection =
            PackageVersionSelector.Select(directive, selectionListing, options);
        if (selection is null)
        {
            return null;
        }

        PackageVersionInfo selectedCandidate =
            PackageVersionSelector.SelectExactSourceCandidate(
                directive.PackageId,
                selection.Key,
                eligibleCandidates,
                options)
            ?? throw new RegistryOperationException(
                "resolve",
                directive.PackageId,
                [
                    new RegistryAttemptFailure(
                        null,
                        RegistryFailureCategory.InvalidResponse)
                ]);
        return new SourceSelection(selection.Key, selectedCandidate);
    }

    private async Task<ResolvedDirective> CreateResolvedDirectiveAsync(
        PackageDirective directive,
        VersionResolveOptions? options,
        SourceSelection selection,
        CancellationToken cancellationToken)
    {
        PackageVersionInfo candidate = selection.Candidate;
        RegistryEndpoint sourceRegistry = candidate.SourceRegistry
            ?? throw new RegistryOperationException(
                "resolve",
                directive.PackageId,
                [
                    new RegistryAttemptFailure(
                        null,
                        RegistryFailureCategory.InvalidResponse)
                ]);
        if (candidate.Distribution?.TarballUrl is string tarballUrl
            && Uri.TryCreate(tarballUrl, UriKind.Absolute, out Uri? tarballUri))
        {
            return CreateResolvedDirective(
                directive.PackageId,
                selection.Key,
                candidate,
                sourceRegistry,
                tarballUri);
        }

        IReadOnlyList<IRegistryClient> sourceClients =
            FindSourceClients(
                sourceRegistry,
                candidate.SourceClient);
        if (sourceClients.Count != 1)
        {
            throw CreateSourceRoutingException(
                "resolve",
                directive.PackageId,
                sourceRegistry);
        }

        PackageDirective exactDirective =
            PackageDirective.Parse($"{directive.PackageId}#{selection.Key}");
        IRegistryClient sourceClient = sourceClients[0];
        try
        {
            ResolvedDirective? resolved = await sourceClient.ResolveAsync(
                    exactDirective,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved with
                {
                    Reference = new PackageReference(
                        directive.PackageId,
                        selection.Key),
                    ShaSum =
                        candidate.Distribution?.ShaSum
                        ?? resolved.ShaSum,
                    Integrity =
                        candidate.Distribution?.Integrity
                        ?? resolved.Integrity,
                    SourceRegistry = sourceRegistry,
                    SourceClient = sourceClient,
                    PublicationDate =
                        TryGetPublicationDate(candidate)
                        ?? resolved.PublicationDate,
                    Dependencies =
                        candidate.Dependencies
                        ?? resolved.Dependencies,
                    FhirVersions =
                        GetFhirVersions(candidate)
                        ?? resolved.FhirVersions,
                };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Failed to materialize selected version {Version} from {Endpoint}",
                selection.Key,
                sourceRegistry.Url);
            throw new RegistryOperationException(
                "resolve",
                directive.PackageId,
                CaptureFailures(sourceClient, exception));
        }

        throw new RegistryOperationException(
            "resolve",
            directive.PackageId,
            [
                new RegistryAttemptFailure(
                    sourceRegistry.Url,
                    RegistryFailureCategory.InvalidResponse)
            ]);
    }

    private static ResolvedDirective CreateResolvedDirective(
        string packageId,
        string version,
        PackageVersionInfo candidate,
        RegistryEndpoint sourceRegistry,
        Uri tarballUri) =>
        new()
        {
            Reference = new PackageReference(packageId, version),
            TarballUri = tarballUri,
            ShaSum = candidate.Distribution?.ShaSum,
            Integrity = candidate.Distribution?.Integrity,
            SourceRegistry = sourceRegistry,
            SourceClient = candidate.SourceClient,
            PublicationDate = TryGetPublicationDate(candidate),
            Dependencies = candidate.Dependencies,
            FhirVersions = GetFhirVersions(candidate),
        };

    private static DateTime? TryGetPublicationDate(
        PackageVersionInfo candidate) =>
        DateTime.TryParse(
            candidate.PublicationDate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTime publicationDate)
            ? publicationDate
            : null;

    private static IReadOnlyList<string>? GetFhirVersions(
        PackageVersionInfo candidate) =>
        candidate.FhirVersions
        ?? (candidate.FhirVersion is string fhirVersion
            ? [fhirVersion]
            : null);

    private static RegistryOperationException CreateIncompleteResolutionException(
        PackageListing listing)
    {
        IReadOnlyList<RegistryAttemptFailure> failures =
            listing.QueryFailures.Count > 0
                ? listing.QueryFailures
                :
                [
                    new RegistryAttemptFailure(
                        null,
                        RegistryFailureCategory.Unexpected)
                ];
        return new RegistryOperationException(
            "resolve",
            listing.PackageId,
            failures);
    }

    private static IReadOnlyList<RegistryAttemptFailure> CaptureFailures(
        IRegistryClient client,
        Exception exception) =>
        exception is RegistryOperationException operationException
            ? operationException.Failures
            : [RegistryAttemptFailure.Capture(client.Endpoint, exception)];

    private IReadOnlyList<IRegistryClient> FindSourceClients(
        RegistryEndpoint sourceRegistry,
        IRegistryClient? sourceClient)
    {
        if (sourceClient is not null)
        {
            IRegistryClient[] routedMatches = _clients
                .Where(client => ReferenceEquals(client, sourceClient))
                .ToArray();
            if (routedMatches.Length > 0)
            {
                return routedMatches;
            }
        }

        List<IRegistryClient> matches = [];
        CollectSourceClients(_clients, sourceRegistry, matches);
        return matches.AsReadOnly();
    }

    private static void CollectSourceClients(
        IEnumerable<IRegistryClient> clients,
        RegistryEndpoint sourceRegistry,
        List<IRegistryClient> matches)
    {
        foreach (IRegistryClient client in clients)
        {
            if (client is RedundantRegistryClient redundantClient)
            {
                CollectSourceClients(
                    redundantClient._clients,
                    sourceRegistry,
                    matches);
                continue;
            }

            if (EndpointsMatch(client.Endpoint, sourceRegistry))
            {
                matches.Add(client);
            }
        }
    }

    private static RegistryOperationException CreateSourceRoutingException(
        string operation,
        string packageId,
        RegistryEndpoint sourceRegistry) =>
        new(
            operation,
            packageId,
            [
                new RegistryAttemptFailure(
                    sourceRegistry.Url,
                    RegistryFailureCategory.InvalidResponse)
            ]);

    private sealed record SearchAttempt(
        int Priority,
        IReadOnlyList<CatalogEntry> Results,
        IReadOnlyList<RegistryAttemptFailure> Failures);

    private sealed record ListingAttempt(
        int Priority,
        IRegistryClient Client,
        PackageListing? Listing,
        IReadOnlyList<RegistryAttemptFailure> Failures);

    private sealed record MergedListingState(PackageListing Listing);

    private sealed record SourceSelection(
        string Key,
        PackageVersionInfo Candidate);

    private static bool EndpointsMatch(
        RegistryEndpoint left,
        RegistryEndpoint right)
    {
        if (left.Type != right.Type)
            return false;

        return TryNormalizeEndpointUri(left.Url, out Uri? leftUri)
            && TryNormalizeEndpointUri(right.Url, out Uri? rightUri)
            && leftUri!.Equals(rightUri);
    }

    private static bool TryNormalizeEndpointUri(
        string value,
        out Uri? normalized)
    {
        string origin = RegistryAttemptFailure.SanitizeOrigin(value);
        if (origin == "unknown"
            || !Uri.TryCreate(origin + "/", UriKind.Absolute, out Uri? uri))
        {
            normalized = null;
            return false;
        }

        normalized = uri;
        return true;
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

        foreach (IRegistryClient client in _clients)
        {
            try
            {
                // Rewind the stream for each attempt.
                tarballStream.Position = 0;

                _logger.LogDebug(
                    "Publishing {PackageId}@{Version} to {Endpoint}",
                    reference.Name, reference.Version, client.Endpoint.Url);

                PublishResult result = await client.PublishAsync(reference, tarballStream, cancellationToken)
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
