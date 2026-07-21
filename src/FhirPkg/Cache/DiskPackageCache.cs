// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirPkg.Cache;

/// <summary>
/// Disk-based FHIR package cache with validated reads and transactional writes.
/// </summary>
public class DiskPackageCache :
    IPackageCache,
    IHardenedPackageCache,
    IHardenedPackageCacheCore,
    IPackageCacheConditionalRemoval,
    IPackageCacheIndexStore,
    IPackageCacheResourceStore,
    IPackageCacheMutationPublisher,
    IDisposable
{
    private const string PackageSubdirectory = "package";
    private const string IndexFileName = ".index.json";

    private enum PackageListMode
    {
        Hydrated,
        Summary,
        Indexing
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PackageInstallLimits _installLimits;
    private readonly PackageCacheValidator _validator;
    private readonly PackageCacheMetadataStore _metadataStore;
    private readonly PackageCacheCommitter _committer;
    private readonly PackageCacheCoordinator _coordinator;
    private readonly IPackageCacheFileOperations _fileOperations;
    private readonly object _mutationSubscriptionsLock = new();
    private readonly Dictionary<long, MutationSubscriptionCallbacks>
        _mutationSubscriptions = [];
    private long _nextMutationSubscriptionId;

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <summary>
    /// Creates a disk package cache using the supplied or default cache root.
    /// </summary>
    public DiskPackageCache(
        string? cacheDirectory = null,
        ILogger<DiskPackageCache>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            cacheDirectory,
            logger,
            timeProvider,
            PackageInstallLimits.ResolveManager(new PackageInstallLimits()))
    {
    }

    internal DiskPackageCache(
        string? cacheDirectory,
        ILogger<DiskPackageCache>? logger,
        TimeProvider? timeProvider,
        PackageInstallLimits installLimits)
        : this(
            cacheDirectory,
            logger,
            timeProvider,
            installLimits,
            SystemPackageCacheFileOperations.Instance,
            NullPackageCacheFaultObserver.Instance)
    {
    }

    internal DiskPackageCache(
        string? cacheDirectory,
        ILogger<DiskPackageCache>? logger,
        TimeProvider? timeProvider,
        PackageInstallLimits installLimits,
        IPackageCacheContentionObserver contentionObserver)
        : this(
            cacheDirectory,
            logger,
            timeProvider,
            installLimits,
            SystemPackageCacheFileOperations.Instance,
            NullPackageCacheFaultObserver.Instance,
            contentionObserver)
    {
    }

    internal DiskPackageCache(
        string? cacheDirectory,
        ILogger<DiskPackageCache>? logger,
        TimeProvider? timeProvider,
        PackageInstallLimits installLimits,
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver)
        : this(
            cacheDirectory,
            logger,
            timeProvider,
            installLimits,
            fileOperations,
            faultObserver,
            NullPackageCacheContentionObserver.Instance)
    {
    }

    internal DiskPackageCache(
        string? cacheDirectory,
        ILogger<DiskPackageCache>? logger,
        TimeProvider? timeProvider,
        PackageInstallLimits installLimits,
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver,
        IPackageCacheContentionObserver contentionObserver)
    {
        ArgumentNullException.ThrowIfNull(installLimits);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(faultObserver);
        ArgumentNullException.ThrowIfNull(contentionObserver);
        installLimits.Validate();

        _logger = logger ?? NullLogger<DiskPackageCache>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installLimits = PackageInstallLimits.ResolvePerCall(
            installLimits,
            requestedLimits: null);
        _fileOperations = fileOperations;
        CacheDirectory = Path.GetFullPath(
            cacheDirectory
            ?? Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER")
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                ".fhir",
                "packages"));

        Directory.CreateDirectory(CacheDirectory);
        _validator = new PackageCacheValidator(CacheDirectory);
        _metadataStore = new PackageCacheMetadataStore(
            CacheDirectory,
            fileOperations,
            faultObserver);
        PackageCacheJournalStore journalStore = new(
            CacheDirectory,
            fileOperations,
            faultObserver);
        _committer = new PackageCacheCommitter(
            CacheDirectory,
            _validator,
            _metadataStore,
            journalStore,
            fileOperations,
            faultObserver);
        _coordinator = new PackageCacheCoordinator(
            CacheDirectory,
            contentionObserver);
    }

    /// <inheritdoc />
    public async Task<bool> IsInstalledAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheInspection inspection = await InspectAsync(reference, ct)
            .ConfigureAwait(false);
        return inspection.State == PackageCacheInspectionState.Valid;
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> GetPackageAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(cacheKey, ct)
                .ConfigureAwait(false);
        if (inspection.State != PackageCacheInspectionState.Valid)
            return null;

        CacheMetadataEntry? entry =
            await _metadataStore.GetEntryAsync(cacheKey, ct)
                .ConfigureAwait(false);
        return await CreateRecordAsync(
                cacheKey.DisplayReference,
                inspection,
                entry,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default) =>
        await ListPackagesAsync(
                packageIdFilter,
                versionFilter,
                PackageListMode.Hydrated,
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Summary metadata is weakly consistent: keys and metadata are captured
    /// together before manifests are validated under their identity leases.
    /// </remarks>
    public async Task<IReadOnlyList<PackageRecord>>
        ListPackageSummariesAsync(
            string? packageIdFilter = null,
            string? versionFilter = null,
            CancellationToken ct = default) =>
        await ListPackagesAsync(
                packageIdFilter,
                versionFilter,
                PackageListMode.Summary,
                ct)
            .ConfigureAwait(false);

    async Task<IReadOnlyList<PackageRecord>>
        IPackageCacheIndexStore.ListPackagesForIndexingAsync(
            string? packageIdFilter,
            string? versionFilter,
            CancellationToken cancellationToken) =>
        await ListPackagesAsync(
                packageIdFilter,
                versionFilter,
                PackageListMode.Indexing,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<IReadOnlyList<PackageRecord>>
        ListPackagesAsync(
            string? packageIdFilter,
            string? versionFilter,
            PackageListMode mode,
            CancellationToken ct)
    {
        await RecoverPendingTransactionsAsync(ct).ConfigureAwait(false);
        PackageCacheKeySnapshot snapshot;
        CacheMetadata? summaryMetadata = null;
        await using (PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct).ConfigureAwait(false))
        {
            snapshot = await SnapshotCacheKeysNoLockAsync(ct)
                .ConfigureAwait(false);
            if (mode == PackageListMode.Summary)
            {
                summaryMetadata = await _metadataStore.ReadAsync(ct)
                    .ConfigureAwait(false);
            }
        }

        List<PackageCacheKey> cacheKeys = snapshot.CanonicalKeys
            .OrderBy(
                key => key.CanonicalIdentity,
                StringComparer.Ordinal)
            .ToList();
        List<PackageRecord> results = [];
        foreach (PackageCacheKey cacheKey in cacheKeys)
        {
            ct.ThrowIfCancellationRequested();
            PackageReference reference = cacheKey.CanonicalReference;
            if (packageIdFilter is not null
                && !reference.Name.StartsWith(
                    packageIdFilter,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (versionFilter is not null
                && !string.Equals(
                    reference.Version,
                    versionFilter,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using PackageCacheLease identityLease =
                await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Valid)
                continue;

            CacheMetadataEntry? entry = null;
            if (mode == PackageListMode.Summary)
            {
                summaryMetadata!.Packages.TryGetValue(
                    cacheKey.MetadataKey,
                    out entry);
            }
            else
            {
                await using PackageCacheLease globalLease =
                    await _coordinator.AcquireGlobalAsync(ct)
                        .ConfigureAwait(false);
                entry = await _metadataStore.GetEntryAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            }

            PackageRecord record = mode == PackageListMode.Hydrated
                ? await CreateRecordAsync(
                        reference,
                        inspection,
                        entry,
                        ct)
                    .ConfigureAwait(false)
                : CreateRecord(
                    reference,
                    inspection,
                    entry,
                    index: null);
            results.Add(record);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<PackageRecord> InstallAsync(
        PackageReference reference,
        Stream tarballStream,
        InstallCacheOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        if (!reference.HasVersion)
        {
            throw new ArgumentException(
                "PackageReference must have a version for installation.",
                nameof(reference));
        }

        options ??= new InstallCacheOptions();
        options.InstallOutcome = PackageCacheInstallOutcome.Unknown;
        if (!Enum.IsDefined(options.CorruptCacheBehavior))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "CorruptCacheBehavior is not a supported value.",
                reference.FhirDirective);
        }

        PackageIdentityExpectation identityExpectation =
            options.IdentityExpectation is null
                ? PackageIdentityValidator.CreateExpectation(
                    reference,
                    reference.FhirDirective)
                : PackageIdentityValidator.ValidateExpectation(
                    options.IdentityExpectation,
                    reference.FhirDirective);
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        PackageCacheKey expectationKey = PackageCacheKey.Create(
            identityExpectation.Reference);
        if (!expectationKey.Equals(cacheKey))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                "Expected package identity does not match the cache target.",
                reference.FhirDirective);
        }

        ReportProgress(
            options.Progress,
            reference.Name,
            PackageProgressPhase.WaitingForLock);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(
                    cacheKey,
                    ct,
                    options.AcquiredContent?.OperationId)
                .ConfigureAwait(false);
        PackageCacheInspection initialInspection =
            await _validator.InspectAsync(cacheKey, ct)
                .ConfigureAwait(false);
        if (initialInspection.State == PackageCacheInspectionState.Valid
            && !options.OverwriteExisting)
        {
            CacheMetadataEntry? existingEntry =
                await _metadataStore.GetEntryAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            PackageRecord existingRecord = await CreateRecordAsync(
                    cacheKey.DisplayReference,
                    initialInspection,
                    existingEntry,
                    ct)
                .ConfigureAwait(false);
            options.InstallOutcome = new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Unchanged,
                initialInspection.Manifest?.Date);
            return existingRecord;
        }

        ThrowIfInstallCannotReplace(
            cacheKey,
            initialInspection,
            options);
        if (initialInspection.State == PackageCacheInspectionState.Corrupt)
        {
            ReportProgress(
                options.Progress,
                reference.Name,
                PackageProgressPhase.Repairing);
        }

        PackageInstallLimits effectiveLimits =
            PackageInstallLimits.ResolvePerCall(
                _installLimits,
                options.Limits);
        PackageContentAcquisition? acquiredContent = options.AcquiredContent;
        bool ownsAcquiredContent = false;
        try
        {
            if (acquiredContent is null)
            {
                ReportProgress(
                    options.Progress,
                    reference.Name,
                    PackageProgressPhase.Acquiring);
                acquiredContent = await PackageContentAcquirer.AcquireAsync(
                        tarballStream,
                        CacheDirectory,
                        effectiveLimits,
                        options.ReportedContentLength,
                        options.ExpectedSha256Sum,
                        options.ExpectedShaSum,
                        options.VerifyChecksum,
                        reference.FhirDirective,
                        _coordinator,
                        ct)
                    .ConfigureAwait(false);
                ownsAcquiredContent = true;
            }

            if (options.SkipIfArchiveUnchanged
                && initialInspection.State
                    == PackageCacheInspectionState.Valid)
            {
                CacheMetadataEntry? existingEntry =
                    await _metadataStore.GetEntryAsync(cacheKey, ct)
                        .ConfigureAwait(false);
                if (existingEntry?.ArchiveSha256 is string existingHash
                    && string.Equals(
                        existingHash,
                        acquiredContent.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CacheMetadataEntry refreshedEntry = existingEntry with
                    {
                        SourcePublicationDate =
                            options.SourcePublicationDate
                            ?? existingEntry.SourcePublicationDate,
                        ArchiveSha256 = acquiredContent.Sha256
                    };
                    await using PackageCacheLease globalLease =
                        await _coordinator.AcquireGlobalAsync(ct)
                            .ConfigureAwait(false);
                    await _metadataStore.SetEntryAsync(
                            cacheKey,
                            refreshedEntry,
                            mutation: null,
                            ct)
                        .ConfigureAwait(false);
                    PackageRecord unchangedRecord = await CreateRecordAsync(
                            cacheKey.DisplayReference,
                            initialInspection,
                            refreshedEntry,
                            ct)
                        .ConfigureAwait(false);
                    options.InstallOutcome = new PackageCacheInstallOutcome(
                        PackageCacheInstallEffect.Unchanged,
                        initialInspection.Manifest?.Date);
                    return unchangedRecord;
                }
            }

            ReportProgress(
                options.Progress,
                reference.Name,
                PackageProgressPhase.Extracting);
            string stagingDirectory = Path.Combine(
                acquiredContent.OperationDirectory,
                "expanded");
            string stagedContentPath;
            try
            {
                await using FileStream archiveStream =
                    acquiredContent.OpenArchiveRead();
                ArchiveExtractionMetrics metrics =
                    await TarballExtractor.ExtractAsync(
                            archiveStream,
                            stagingDirectory,
                            effectiveLimits,
                            reference.FhirDirective,
                            ct)
                        .ConfigureAwait(false);
                _logger.LogDebug(
                    "Extracted {EntryCount} entries and {ExpandedBytes} bytes for {Directive}.",
                    metrics.EntryCount,
                    metrics.ExpandedBytes,
                    reference.FhirDirective);
                ReportProgress(
                    options.Progress,
                    reference.Name,
                    PackageProgressPhase.Validating);

                PackageArchiveInventory inventory = metrics.Inventory
                    ?? throw InvalidArchive(
                        reference,
                        "Package archive inventory was not produced.");
                PackageArchiveLayoutResult layout =
                    TarballExtractor.ValidateAndNormalizePackageStructure(
                        stagingDirectory,
                        inventory,
                        reference.FhirDirective);
                PackageIdentityValidationResult identity =
                    await PackageIdentityValidator.ValidateExpectedAsync(
                            layout.ManifestPath,
                            identityExpectation,
                            reference.FhirDirective,
                            ct)
                        .ConfigureAwait(false);
                if (!identity.CacheKey.Equals(cacheKey))
                {
                    throw new PackageInstallException(
                        PackageInstallErrorCode.InvalidPackageIdentity,
                        PackageInstallStage.IdentityValidation,
                        "Validated package identity does not match the cache target.",
                        reference.FhirDirective);
                }

                stagedContentPath = layout.ContentPath;
            }
            catch (PackageInstallException)
            {
                throw;
            }
            catch (InvalidDataException exception)
            {
                throw InvalidArchive(reference, exception);
            }
            catch (InvalidOperationException exception)
            {
                throw InvalidArchive(reference, exception);
            }

            long sizeBytes = CalculateDirectorySize(stagedContentPath);
            CacheMetadataEntry entry = new()
            {
                DownloadDateTime =
                    _timeProvider.GetUtcNow().UtcDateTime,
                SizeBytes = sizeBytes,
                SourcePublicationDate = options.SourcePublicationDate,
                ArchiveSha256 = options.ArchiveSha256
                    ?? acquiredContent.Sha256,
                ContentGeneration = acquiredContent.OperationId
            };

            PackageCacheInspection committedInspection;
            PackageCacheInstallEffect committedEffect;
            string? previousManifestDate;
            await using (PackageCacheLease globalLease =
                await _coordinator.AcquireGlobalAsync(ct)
                    .ConfigureAwait(false))
            {
                ReportProgress(
                    options.Progress,
                    reference.Name,
                    PackageProgressPhase.Committing);
                await RecoverNoLockAsync(cacheKey, ct)
                    .ConfigureAwait(false);
                PackageCacheInspection currentInspection =
                    await _validator.InspectAsync(cacheKey, ct)
                        .ConfigureAwait(false);
                ThrowIfInstallCannotReplace(
                    cacheKey,
                    currentInspection,
                    options);
                committedEffect =
                    currentInspection.State == PackageCacheInspectionState.Missing
                        ? PackageCacheInstallEffect.Created
                        : PackageCacheInstallEffect.Replaced;
                previousManifestDate = currentInspection.Manifest?.Date;
                PublishPackageInvalidatedIfPresent(
                    cacheKey,
                    currentInspection);
                await _committer.CommitInstallAsync(
                        cacheKey,
                        currentInspection,
                        stagingDirectory,
                        acquiredContent.OperationId,
                        entry,
                        ct)
                    .ConfigureAwait(false);
                committedInspection = await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (committedInspection.State
                    != PackageCacheInspectionState.Valid)
                {
                    throw new PackageInstallException(
                        PackageInstallErrorCode.CommitFailed,
                        PackageInstallStage.Commit,
                        "The committed package is not readable.",
                        reference.FhirDirective);
                }
            }

            PackageRecord committedRecord = await CreateRecordAsync(
                    reference,
                    committedInspection,
                    entry,
                    ct)
                .ConfigureAwait(false);
            options.InstallOutcome = new PackageCacheInstallOutcome(
                committedEffect,
                previousManifestDate);
            return committedRecord;
        }
        finally
        {
            if (ownsAcquiredContent && acquiredContent is not null)
                await acquiredContent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<PackageRecord> ImportAsync(
        Stream tarballStream,
        InstallCacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        options ??= new InstallCacheOptions();
        if (!Enum.IsDefined(options.CorruptCacheBehavior))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "CorruptCacheBehavior is not a supported value.");
        }

        PackageInstallLimits effectiveLimits =
            PackageInstallLimits.ResolvePerCall(
                _installLimits,
                options.Limits);
        PackageContentAcquisition? acquiredContent =
            options.AcquiredContent;
        bool ownsAcquiredContent = false;
        try
        {
            if (acquiredContent is null)
            {
                ReportProgress(
                    options.Progress,
                    "package import",
                    PackageProgressPhase.Acquiring);
                acquiredContent = await PackageContentAcquirer.AcquireAsync(
                        tarballStream,
                        CacheDirectory,
                        effectiveLimits,
                        options.ReportedContentLength,
                        options.ExpectedSha256Sum,
                        options.ExpectedShaSum,
                        options.VerifyChecksum,
                        "package import",
                        _coordinator,
                        cancellationToken)
                    .ConfigureAwait(false);
                ownsAcquiredContent = true;
            }

            string stagingDirectory = Path.Combine(
                acquiredContent.OperationDirectory,
                "expanded");
            ReportProgress(
                options.Progress,
                "package import",
                PackageProgressPhase.Extracting);
            PackageIdentityValidationResult identity;
            string stagedContentPath;
            try
            {
                await using FileStream archiveStream =
                    acquiredContent.OpenArchiveRead();
                ArchiveExtractionMetrics metrics =
                    await TarballExtractor.ExtractAsync(
                            archiveStream,
                            stagingDirectory,
                            effectiveLimits,
                            "package import",
                            cancellationToken)
                        .ConfigureAwait(false);
                PackageArchiveInventory inventory = metrics.Inventory
                    ?? throw new PackageInstallException(
                        PackageInstallErrorCode.InvalidArchive,
                        PackageInstallStage.ArchiveValidation,
                        "Package archive inventory was not produced.");
                ReportProgress(
                    options.Progress,
                    "package import",
                    PackageProgressPhase.Validating);
                PackageArchiveLayoutResult layout =
                    TarballExtractor.ValidateAndNormalizePackageStructure(
                        stagingDirectory,
                        inventory,
                        "package import");
                identity = await PackageIdentityValidator.DiscoverAsync(
                        layout.ManifestPath,
                        "package import",
                        cancellationToken)
                    .ConfigureAwait(false);
                stagedContentPath = layout.ContentPath;
            }
            catch (PackageInstallException)
            {
                throw;
            }
            catch (InvalidDataException exception)
            {
                throw new PackageInstallException(
                    PackageInstallErrorCode.InvalidArchive,
                    PackageInstallStage.ArchiveValidation,
                    "Imported package content contains an invalid archive.",
                    innerException: exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new PackageInstallException(
                    PackageInstallErrorCode.InvalidArchive,
                    PackageInstallStage.ArchiveValidation,
                    "Imported package content contains an invalid archive.",
                    innerException: exception);
            }

            PackageCacheKey cacheKey = identity.CacheKey;
            ReportProgress(
                options.Progress,
                cacheKey.DisplayReference.Name,
                PackageProgressPhase.WaitingForLock);
            await using PackageCacheLease identityLease =
                await AcquireIdentityAndRecoverAsync(
                        cacheKey,
                        cancellationToken,
                        acquiredContent.OperationId)
                    .ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (inspection.State == PackageCacheInspectionState.Valid
                && !options.OverwriteExisting)
            {
                CacheMetadataEntry? existingEntry =
                    await _metadataStore.GetEntryAsync(
                            cacheKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                return await CreateRecordAsync(
                        cacheKey.DisplayReference,
                        inspection,
                        existingEntry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ThrowIfInstallCannotReplace(cacheKey, inspection, options);
            if (inspection.State == PackageCacheInspectionState.Corrupt)
            {
                ReportProgress(
                    options.Progress,
                    cacheKey.DisplayReference.Name,
                    PackageProgressPhase.Repairing);
            }
            long sizeBytes = CalculateDirectorySize(stagedContentPath);
            CacheMetadataEntry entry = new()
            {
                DownloadDateTime =
                    _timeProvider.GetUtcNow().UtcDateTime,
                SizeBytes = sizeBytes,
                SourcePublicationDate = options.SourcePublicationDate,
                ArchiveSha256 = options.ArchiveSha256
                    ?? acquiredContent.Sha256,
                ContentGeneration = acquiredContent.OperationId
            };

            PackageCacheInspection committedInspection;
            await using (PackageCacheLease globalLease =
                await _coordinator.AcquireGlobalAsync(
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                ReportProgress(
                    options.Progress,
                    cacheKey.DisplayReference.Name,
                    PackageProgressPhase.Committing);
                await RecoverNoLockAsync(
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                PackageCacheInspection currentInspection =
                    await _validator.InspectAsync(
                            cacheKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (currentInspection.State
                        == PackageCacheInspectionState.Valid
                    && !options.OverwriteExisting)
                {
                    CacheMetadataEntry? existingEntry =
                        await _metadataStore.GetEntryAsync(
                                cacheKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                    return await CreateRecordAsync(
                            cacheKey.DisplayReference,
                            currentInspection,
                            existingEntry,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                ThrowIfInstallCannotReplace(
                    cacheKey,
                    currentInspection,
                    options);
                PublishPackageInvalidatedIfPresent(
                    cacheKey,
                    currentInspection);
                await _committer.CommitInstallAsync(
                        cacheKey,
                        currentInspection,
                        stagingDirectory,
                        acquiredContent.OperationId,
                        entry,
                        cancellationToken)
                    .ConfigureAwait(false);
                committedInspection = await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (committedInspection.State
                    != PackageCacheInspectionState.Valid)
                {
                    throw new PackageInstallException(
                        PackageInstallErrorCode.CommitFailed,
                        PackageInstallStage.Commit,
                        "The committed imported package is not readable.",
                        cacheKey.DisplayReference.FhirDirective);
                }
            }

            return await CreateRecordAsync(
                    cacheKey.DisplayReference,
                    committedInspection,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsAcquiredContent && acquiredContent is not null)
            {
                await acquiredContent.DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                .ConfigureAwait(false);
        return await RemoveUnderIdentityLeaseAsync(
                cacheKey,
                ct)
            .ConfigureAwait(false);
    }

    async Task<bool>
        IPackageCacheConditionalRemoval.RemoveIfUnchangedAsync(
            PackageRecord expected,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(expected.Reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        if (inspection.State
            != PackageCacheInspectionState.Valid)
        {
            return false;
        }

        CacheMetadataEntry? entry =
            await _metadataStore.GetEntryAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        PackageRecord current = CreateRecord(
            cacheKey.CanonicalReference,
            inspection,
            entry,
            index: null);
        if (!string.Equals(
                current.ContentGeneration,
                expected.ContentGeneration,
                StringComparison.Ordinal))
        {
            return false;
        }

        return await RemoveUnderIdentityLeaseAsync(
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> RemoveUnderIdentityLeaseAsync(
        PackageCacheKey cacheKey,
        CancellationToken ct)
    {
        await using PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct)
                .ConfigureAwait(false);
        await RecoverNoLockAsync(cacheKey, ct).ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(cacheKey, ct)
                .ConfigureAwait(false);
        if (inspection.State == PackageCacheInspectionState.Missing)
        {
            CacheMetadataEntry? staleEntry =
                await _metadataStore.GetEntryAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            if (staleEntry is not null)
            {
                await _metadataStore.SetEntryAsync(
                        cacheKey,
                        entry: null,
                        mutation: null,
                        ct)
                    .ConfigureAwait(false);
            }

            return false;
        }

        PublishPackageInvalidated(cacheKey.CanonicalReference);
        return await _committer.RemoveAsync(
                cacheKey,
                inspection,
                Guid.NewGuid().ToString("N"),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        PackageCacheKeySnapshot snapshot;
        await using (PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct).ConfigureAwait(false))
        {
            snapshot = await SnapshotCacheKeysNoLockAsync(ct)
                .ConfigureAwait(false);
            await _metadataStore.RemoveManagedKeysAsync(
                    snapshot.NonCanonicalManagedKeys,
                    ct)
                .ConfigureAwait(false);
        }

        int count = 0;
        foreach (PackageCacheKey cacheKey in snapshot.CanonicalKeys.OrderBy(
            key => key.CanonicalIdentity,
            StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await using PackageCacheLease identityLease =
                await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            await using PackageCacheLease globalLease =
                await _coordinator.AcquireGlobalAsync(ct)
                    .ConfigureAwait(false);
            await RecoverNoLockAsync(cacheKey, ct).ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            if (inspection.State == PackageCacheInspectionState.Missing)
            {
                await _metadataStore.SetEntryAsync(
                        cacheKey,
                        entry: null,
                        mutation: null,
                        ct)
                    .ConfigureAwait(false);
                continue;
            }

            PublishPackageInvalidated(cacheKey.CanonicalReference);
            bool removed = await _committer.RemoveAsync(
                    cacheKey,
                    inspection,
                    Guid.NewGuid().ToString("N"),
                    ct)
                .ConfigureAwait(false);
            if (removed)
                count++;
            RemoveEmptyScopeDirectories();
        }

        PublishCacheCleared();
        return count;
    }

    /// <inheritdoc />
    public async Task<PackageManifest?> ReadManifestAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheInspection inspection = await InspectAsync(reference, ct)
            .ConfigureAwait(false);
        return inspection.State == PackageCacheInspectionState.Valid
            ? inspection.Manifest
            : null;
    }

    /// <inheritdoc />
    public async Task<PackageIndex?> GetIndexAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(cacheKey, ct)
                .ConfigureAwait(false);
        if (inspection.State != PackageCacheInspectionState.Valid)
            return null;

        return await TryLoadValidIndexAsync(
                inspection.ContentPath!,
                ct)
            .ConfigureAwait(false);
    }

    async Task<PackageIndex?> IPackageCacheIndexStore.GetOrCreateIndexAsync(
        PackageReference reference,
        bool forceReindex,
        Func<PackageRecord, CancellationToken, Task<PackageIndex>> generator,
        CancellationToken cancellationToken,
        Action<PackageRecord, PackageIndex>? indexReady)
    {
        ArgumentNullException.ThrowIfNull(generator);

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        if (inspection.State != PackageCacheInspectionState.Valid)
            return null;

        PackageIndex? existingIndex =
            await TryLoadValidIndexAsync(
                    inspection.ContentPath!,
                    cancellationToken)
                .ConfigureAwait(false);
        CacheMetadataEntry? metadataEntry =
            await _metadataStore.GetEntryAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        PackageRecord package = CreateRecord(
            cacheKey.DisplayReference,
            inspection,
            metadataEntry,
            existingIndex);
        if (!forceReindex && existingIndex is not null)
        {
            indexReady?.Invoke(
                package,
                existingIndex);
            return existingIndex;
        }

        PackageIndex generatedIndex =
            await generator(package, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The package index generator returned null.");

        EnsureGeneratedIndexIsValid(
            generatedIndex,
            inspection.ContentPath!);
        byte[] serializedIndex = JsonSerializer.SerializeToUtf8Bytes(
            generatedIndex,
            s_jsonOptions);
        if (serializedIndex.LongLength > _installLimits.MaxEntryBytes)
        {
            throw new InvalidDataException(
                $"The generated package index exceeds the configured {_installLimits.MaxEntryBytes} byte entry limit.");
        }

        string expectedTargetPath = inspection.TargetPath;
        string expectedContentPath = inspection.ContentPath!;
        string indexPath = Path.Combine(
            expectedContentPath,
            IndexFileName);
        await DurableFileWriter.WriteAsync(
                indexPath,
                serializedIndex,
                _fileOperations,
                cancellationToken,
                async beforeCommitToken =>
                {
                    PackageCacheInspection currentInspection =
                        await _validator.InspectAsync(
                                cacheKey,
                                beforeCommitToken)
                            .ConfigureAwait(false);
                    EnsureIndexCommitTargetIsUnchanged(
                        cacheKey,
                        currentInspection,
                        expectedTargetPath,
                        expectedContentPath);
                    EnsureGeneratedIndexIsValid(
                        generatedIndex,
                        currentInspection.ContentPath!);
                })
            .ConfigureAwait(false);

        PackageIndex? persistedIndex =
            await TryLoadValidIndexAsync(
                    expectedContentPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        if (persistedIndex is null)
        {
            throw new IOException(
                $"The persisted package index for '{cacheKey.CanonicalReference.FhirDirective}' could not be validated.");
        }

        indexReady?.Invoke(
            package with
            {
                Index = persistedIndex,
            },
            persistedIndex);
        return persistedIndex;
    }

    IDisposable IPackageCacheMutationPublisher.Subscribe(
        Action<PackageReference> packageInvalidated,
        Action cacheCleared) =>
        SubscribeToMutations(packageInvalidated, cacheCleared);

    /// <inheritdoc />
    public async Task<string?> GetFileContentAsync(
        PackageReference reference,
        string relativePath,
        CancellationToken ct = default) =>
        await ReadFileAsync(
                reference,
                relativePath,
                _ => null,
                (_, content) => content,
                ct)
            .ConfigureAwait(false);

    async Task<TResult?>
        IPackageCacheResourceStore.ReadFileAsync<TResult>(
            PackageReference reference,
            string relativePath,
            Func<string, TResult?> tryGetCached,
            Func<string, string, TResult?> materialize,
            CancellationToken cancellationToken)
        where TResult : class =>
        await ReadFileAsync(
                reference,
                relativePath,
                tryGetCached,
                materialize,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<TResult?> ReadFileAsync<TResult>(
        PackageReference reference,
        string relativePath,
        Func<string, TResult?> tryGetCached,
        Func<string, string, TResult?> materialize,
        CancellationToken cancellationToken)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(tryGetCached);
        ArgumentNullException.ThrowIfNull(materialize);

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        if (inspection.State != PackageCacheInspectionState.Valid)
            return null;

        CacheMetadataEntry? metadataEntry =
            await _metadataStore.GetEntryAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        string contentGeneration =
            CreateContentGeneration(metadataEntry);
        TResult? cached =
            tryGetCached(contentGeneration);
        if (cached is not null)
            return cached;

        string contentPath = inspection.ContentPath!;
        string filePath = Path.GetFullPath(
            Path.Combine(contentPath, relativePath));
        string containment = Path.GetRelativePath(contentPath, filePath);
        if (Path.IsPathRooted(containment)
            || containment == ".."
            || containment.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || containment.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Relative path must not traverse outside the package directory.",
                nameof(relativePath));
        }

        if (!File.Exists(filePath))
            return null;

        string content =
            await File.ReadAllTextAsync(
                    filePath,
                    cancellationToken)
                .ConfigureAwait(false);
        return materialize(
            contentGeneration,
            content);
    }

    /// <inheritdoc />
    public string? GetPackageContentPath(PackageReference reference)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        using PackageCacheLease identityLease =
            AcquireIdentityAndRecover(cacheKey);
        PackageCacheInspection inspection = _validator.Inspect(cacheKey);
        return inspection.State == PackageCacheInspectionState.Valid
            ? inspection.ContentPath
            : null;
    }

    /// <inheritdoc />
    public async Task<CacheMetadata> GetMetadataAsync(
        CancellationToken ct = default)
    {
        await using PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct)
                .ConfigureAwait(false);
        return await _metadataStore.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        PackageReference reference,
        CacheMetadataEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                .ConfigureAwait(false);
        await using PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct)
                .ConfigureAwait(false);
        CacheMetadataEntry? existingEntry =
            await _metadataStore.GetEntryAsync(
                    cacheKey,
                    ct)
                .ConfigureAwait(false);
        CacheMetadataEntry effectiveEntry = entry with
        {
            ContentGeneration =
                existingEntry?.ContentGeneration,
        };
        await _metadataStore.SetEntryAsync(
                cacheKey,
                effectiveEntry,
                mutation: null,
                ct)
            .ConfigureAwait(false);
    }

    async Task<PackageCacheInspection>
        IHardenedPackageCacheCore.InspectAsync(
            PackageReference reference,
            CancellationToken cancellationToken) =>
        await InspectAsync(reference, cancellationToken)
            .ConfigureAwait(false);

    async Task<PackageIdentityValidationResult>
        IHardenedPackageCacheCore.DiscoverIdentityAsync(
            string manifestPath,
            string? directive,
            CancellationToken cancellationToken) =>
        await PackageIdentityValidator.DiscoverAsync(
                manifestPath,
                directive,
                cancellationToken)
            .ConfigureAwait(false);

    async Task<HardenedPackageCacheInspection>
        IHardenedPackageCache.InspectAsync(
            PackageReference reference,
            CancellationToken cancellationToken)
    {
        PackageCacheInspection inspection =
            await InspectAsync(reference, cancellationToken)
                .ConfigureAwait(false);
        return new HardenedPackageCacheInspection
        {
            State = inspection.State switch
            {
                PackageCacheInspectionState.Missing =>
                    HardenedPackageCacheState.Missing,
                PackageCacheInspectionState.Valid =>
                    HardenedPackageCacheState.Valid,
                _ => HardenedPackageCacheState.Corrupt
            },
            IsRepairable = inspection.IsRepairable,
            CorruptionReason = inspection.CorruptionReason
        };
    }

    internal async Task<PackageCacheInspection> InspectAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        return await _validator.InspectAsync(
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ThrowIfInstallCannotReplace(
        PackageCacheKey cacheKey,
        PackageCacheInspection inspection,
        InstallCacheOptions options)
    {
        if (inspection.State == PackageCacheInspectionState.Corrupt
            && (options.CorruptCacheBehavior == CorruptCacheBehavior.Strict
                || !inspection.IsRepairable))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.CorruptCache,
                PackageInstallStage.CacheInspection,
                $"Cached package '{cacheKey.DisplayReference.FhirDirective}' is corrupt: " +
                $"{inspection.CorruptionReason}",
                cacheKey.DisplayReference.FhirDirective);
        }

        if (inspection.State == PackageCacheInspectionState.Valid
            && !options.OverwriteExisting)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.CommitFailed,
                PackageInstallStage.Commit,
                $"Package {cacheKey.DisplayReference.FhirDirective} is already installed. " +
                "Set OverwriteExisting to true to overwrite.",
                cacheKey.DisplayReference.FhirDirective);
        }
    }

    private async Task<PackageCacheLease> AcquireIdentityAndRecoverAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken,
        string? protectedOperationId = null)
    {
        PackageCacheLease identityLease =
            await _coordinator.AcquireIdentityAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        bool completed = false;
        try
        {
            if (await _committer.HasPendingTransactionAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                PublishPackageInvalidated(
                    cacheKey.CanonicalReference);
                await using PackageCacheLease globalLease =
                    await _coordinator.AcquireGlobalAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                await RecoverNoLockAsync(cacheKey, cancellationToken)
                    .ConfigureAwait(false);
            }

            await CleanupAbandonedStagingNoLockAsync(
                    cancellationToken,
                    protectedOperationId)
                .ConfigureAwait(false);
            completed = true;
            return identityLease;
        }
        finally
        {
            if (!completed)
                await identityLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private PackageCacheLease AcquireIdentityAndRecover(
        PackageCacheKey cacheKey)
    {
        PackageCacheLease identityLease =
            _coordinator.AcquireIdentity(cacheKey);
        bool completed = false;
        try
        {
            bool hasPendingTransaction =
                _committer.HasPendingTransactionAsync(
                        cacheKey,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            if (hasPendingTransaction)
            {
                PublishPackageInvalidated(
                    cacheKey.CanonicalReference);
                using PackageCacheLease globalLease =
                    _coordinator.AcquireGlobal();
                RecoverNoLockAsync(cacheKey, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            CleanupAbandonedStagingNoLockAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            completed = true;
            return identityLease;
        }
        finally
        {
            if (!completed)
                identityLease.Dispose();
        }
    }

    private async Task RecoverPendingTransactionsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageCacheTransactionJournal> journals;
        await using (PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            journals = await _committer.GetPendingTransactionsAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }

        List<PackageCacheKey> cacheKeys = journals
            .Select(journal => journal.GetCacheKey())
            .Distinct()
            .OrderBy(
                key => key.CanonicalIdentity,
                StringComparer.Ordinal)
            .ToList();
        foreach (PackageCacheKey cacheKey in cacheKeys)
        {
            await using PackageCacheLease identityLease =
                await AcquireIdentityAndRecoverAsync(
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private async Task RecoverNoLockAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _committer.RecoverAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw RecoveryFailure(exception);
        }
        catch (IOException exception)
        {
            throw RecoveryFailure(exception);
        }
    }

    private async Task CleanupAbandonedStagingNoLockAsync(
        CancellationToken cancellationToken,
        string? protectedOperationId = null)
    {
        string stagingRoot = Path.Combine(
            CacheDirectory,
            ".fhirpkg",
            "staging");
        if (!Directory.Exists(stagingRoot))
            return;

        IReadOnlyList<PackageCacheTransactionJournal> journals =
            await _committer.GetPendingTransactionsAsync(cancellationToken)
                .ConfigureAwait(false);
        HashSet<string> journalOperationIds = journals
            .Select(journal => journal.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        string[] operationDirectories = Directory.GetDirectories(
            stagingRoot,
            "*",
            SearchOption.TopDirectoryOnly);
        Array.Sort(operationDirectories, StringComparer.Ordinal);
        foreach (string operationDirectory in operationDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operationId = Path.GetFileName(operationDirectory);
            if (journalOperationIds.Contains(operationId)
                || string.Equals(
                    operationId,
                    protectedOperationId,
                    StringComparison.Ordinal)
                || !Guid.TryParseExact(operationId, "N", out Guid _))
            {
                continue;
            }

            PackageCacheLease? ownerLease =
                _coordinator.TryAcquireOperationOwner(operationId);
            if (ownerLease is null)
                continue;

            using (ownerLease)
            {
                PackageContentAcquisition.TryDeleteOperationDirectory(
                    operationDirectory);
            }
        }
    }

    private IEnumerable<(PackageCacheKey Key, string Path)>
        EnumeratePackageTargets()
    {
        if (!Directory.Exists(CacheDirectory))
            yield break;

        foreach (string path in Directory.EnumerateFileSystemEntries(
            CacheDirectory))
        {
            string name = Path.GetFileName(path);
            if (string.Equals(
                name,
                ".fhirpkg",
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (PackageCacheKey.TryParseRelativePath(
                name,
                out PackageCacheKey? unscopedKey))
            {
                yield return (unscopedKey!, path);
                continue;
            }

            if (!name.StartsWith('@')
                || !Directory.Exists(path))
            {
                continue;
            }

            foreach (string scopedPath in
                Directory.EnumerateFileSystemEntries(path))
            {
                string relativePath = Path.GetRelativePath(
                    CacheDirectory,
                    scopedPath);
                if (PackageCacheKey.TryParseRelativePath(
                    relativePath,
                    out PackageCacheKey? scopedKey))
                {
                    yield return (scopedKey!, scopedPath);
                }
            }
        }
    }

    private async Task<PackageCacheKeySnapshot>
        SnapshotCacheKeysNoLockAsync(
            CancellationToken cancellationToken)
    {
        HashSet<PackageCacheKey> canonicalKeys = [];
        HashSet<string> nonCanonicalManagedKeys =
            new(StringComparer.Ordinal);
        foreach ((PackageCacheKey cacheKey, string _) in
            EnumeratePackageTargets())
        {
            canonicalKeys.Add(cacheKey);
        }

        IReadOnlyList<PackageCacheTransactionJournal> journals =
            await _committer.GetPendingTransactionsAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        foreach (PackageCacheTransactionJournal journal in journals)
            canonicalKeys.Add(journal.GetCacheKey());

        IReadOnlySet<string> metadataKeys =
            await _metadataStore.ReadManagedKeysAsync(
                cancellationToken)
            .ConfigureAwait(false);
        foreach (string metadataKey in metadataKeys)
        {
            if (!PackageCacheKey.TryParseRelativePath(
                    metadataKey,
                    out PackageCacheKey? cacheKey))
            {
                nonCanonicalManagedKeys.Add(metadataKey);
                continue;
            }

            PackageCacheKey parsedKey = cacheKey!;
            if (!string.Equals(
                    metadataKey,
                    parsedKey.MetadataKey,
                    StringComparison.Ordinal))
            {
                nonCanonicalManagedKeys.Add(metadataKey);
                continue;
            }

            canonicalKeys.Add(parsedKey);
        }

        return new PackageCacheKeySnapshot(
            canonicalKeys,
            nonCanonicalManagedKeys);
    }

    private sealed record PackageCacheKeySnapshot(
        HashSet<PackageCacheKey> CanonicalKeys,
        IReadOnlySet<string> NonCanonicalManagedKeys);

    private void RemoveEmptyScopeDirectories()
    {
        foreach (string scopePath in Directory.GetDirectories(
            CacheDirectory,
            "@*",
            SearchOption.TopDirectoryOnly))
        {
            if (!Directory.EnumerateFileSystemEntries(scopePath).Any())
                Directory.Delete(scopePath);
        }
    }

    private async Task<PackageRecord> CreateRecordAsync(
        PackageReference reference,
        PackageCacheInspection inspection,
        CacheMetadataEntry? entry,
        CancellationToken cancellationToken)
    {
        PackageIndex? index = await TryLoadValidIndexAsync(
                inspection.ContentPath!,
                cancellationToken)
            .ConfigureAwait(false);
        return CreateRecord(
            reference,
            inspection,
            entry,
            index);
    }

    private static PackageRecord CreateRecord(
        PackageReference reference,
        PackageCacheInspection inspection,
        CacheMetadataEntry? entry,
        PackageIndex? index) =>
        new()
        {
            Reference = reference,
            DirectoryPath = inspection.TargetPath,
            ContentPath = inspection.ContentPath!,
            Manifest = inspection.Manifest!,
            Index = index,
            InstalledAt = entry?.DownloadDateTime,
            SizeBytes = entry?.SizeBytes,
            ContentGeneration =
                CreateContentGeneration(entry)
        };

    private static string CreateContentGeneration(
        CacheMetadataEntry? entry) =>
        string.Join(
            "\0",
            entry?.ContentGeneration
                ?? "legacy",
            entry?.DownloadDateTime.ToString("O")
                ?? string.Empty,
            entry?.SizeBytes?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
            entry?.SourcePublicationDate?.ToString("O")
                ?? string.Empty,
            entry?.ArchiveSha256
                ?? string.Empty);

    private async Task<PackageIndex?> TryLoadValidIndexAsync(
        string packageContentPath,
        CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(
            packageContentPath,
            IndexFileName);
        try
        {
            await using FileStream stream =
                _fileOperations.OpenRead(indexPath);
            if (stream.Length > _installLimits.MaxEntryBytes)
            {
                _logger.LogDebug(
                    "Ignoring package index at '{Path}' because it exceeds the configured entry limit.",
                    indexPath);
                return null;
            }

            PackageIndex? index =
                await JsonSerializer.DeserializeAsync<PackageIndex>(
                        stream,
                        s_jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!PackageIndexValidation.TryValidateReferencedFiles(
                    index,
                    packageContentPath,
                    out string? failureReason))
            {
                _logger.LogDebug(
                    "Ignoring invalid package index at '{Path}': {Reason}",
                    indexPath,
                    failureReason);
                return null;
            }

            return index;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(
                exception,
                "Ignoring malformed package index at '{Path}'.",
                indexPath);
            return null;
        }
        catch (NotSupportedException exception)
        {
            _logger.LogDebug(
                exception,
                "Ignoring unsupported package index at '{Path}'.",
                indexPath);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogDebug(
                exception,
                "Ignoring unreadable package index at '{Path}'.",
                indexPath);
            return null;
        }
        catch (IOException exception)
        {
            _logger.LogDebug(
                exception,
                "Ignoring unreadable package index at '{Path}'.",
                indexPath);
            return null;
        }
    }

    private static void EnsureGeneratedIndexIsValid(
        PackageIndex index,
        string packageContentPath)
    {
        if (!PackageIndexValidation.TryValidateReferencedFiles(
                index,
                packageContentPath,
                out string? failureReason))
        {
            throw new InvalidDataException(
                $"The generated package index is invalid: {failureReason}");
        }
    }

    private static void EnsureIndexCommitTargetIsUnchanged(
        PackageCacheKey expectedCacheKey,
        PackageCacheInspection inspection,
        string expectedTargetPath,
        string expectedContentPath)
    {
        if (inspection.State != PackageCacheInspectionState.Valid
            || !inspection.CacheKey.Equals(expectedCacheKey)
            || !string.Equals(
                inspection.TargetPath,
                expectedTargetPath,
                StringComparison.Ordinal)
            || !string.Equals(
                inspection.ContentPath,
                expectedContentPath,
                StringComparison.Ordinal))
        {
            throw new IOException(
                $"Package '{expectedCacheKey.CanonicalReference.FhirDirective}' changed while its index was being generated.");
        }
    }

    private IDisposable SubscribeToMutations(
        Action<PackageReference> packageInvalidated,
        Action cacheCleared)
    {
        ArgumentNullException.ThrowIfNull(packageInvalidated);
        ArgumentNullException.ThrowIfNull(cacheCleared);

        long subscriptionId;
        lock (_mutationSubscriptionsLock)
        {
            subscriptionId = checked(++_nextMutationSubscriptionId);
            _mutationSubscriptions.Add(
                subscriptionId,
                new MutationSubscriptionCallbacks(
                    packageInvalidated,
                    cacheCleared));
        }

        return new MutationSubscription(
            this,
            subscriptionId);
    }

    private void PublishPackageInvalidatedIfPresent(
        PackageCacheKey cacheKey,
        PackageCacheInspection inspection)
    {
        if (inspection.State != PackageCacheInspectionState.Missing)
        {
            PublishPackageInvalidated(
                cacheKey.CanonicalReference);
        }
    }

    private void PublishPackageInvalidated(
        PackageReference reference)
    {
        IReadOnlyList<MutationSubscriptionCallbacks> subscriptions =
            SnapshotMutationSubscriptions();
        foreach (MutationSubscriptionCallbacks subscription in subscriptions)
            subscription.PackageInvalidated(reference);
    }

    private void PublishCacheCleared()
    {
        IReadOnlyList<MutationSubscriptionCallbacks> subscriptions =
            SnapshotMutationSubscriptions();
        foreach (MutationSubscriptionCallbacks subscription in subscriptions)
            subscription.CacheCleared();
    }

    private IReadOnlyList<MutationSubscriptionCallbacks>
        SnapshotMutationSubscriptions()
    {
        lock (_mutationSubscriptionsLock)
        {
            return _mutationSubscriptions
                .OrderBy(
                    pair => pair.Key)
                .Select(
                    pair => pair.Value)
                .ToList();
        }
    }

    private void UnsubscribeFromMutations(long subscriptionId)
    {
        lock (_mutationSubscriptionsLock)
            _mutationSubscriptions.Remove(subscriptionId);
    }

    private static long CalculateDirectorySize(string path)
    {
        long total = 0;
        foreach (FileInfo file in new DirectoryInfo(path)
            .EnumerateFiles("*", SearchOption.AllDirectories))
        {
            checked
            {
                total += file.Length;
            }
        }

        return total;
    }

    private static PackageInstallException InvalidArchive(
        PackageReference reference,
        string message) =>
        new(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            reference.FhirDirective);

    private static PackageInstallException InvalidArchive(
        PackageReference reference,
        Exception exception) =>
        new(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            $"Package {reference.FhirDirective} contains an invalid archive.",
            reference.FhirDirective,
            exception);

    private static PackageInstallException RecoveryFailure(
        Exception exception) =>
        new(
            PackageInstallErrorCode.CoordinationFailed,
            PackageInstallStage.Coordination,
            "A pending cache transaction could not be recovered.",
            innerException: exception);

    private static void ReportProgress(
        IProgress<PackageProgress>? progress,
        string packageId,
        PackageProgressPhase phase)
    {
        progress?.Report(new PackageProgress
        {
            PackageId = packageId,
            Phase = phase
        });
    }

    private sealed record MutationSubscriptionCallbacks(
        Action<PackageReference> PackageInvalidated,
        Action CacheCleared);

    private sealed class MutationSubscription : IDisposable
    {
        private DiskPackageCache? _owner;
        private readonly long _subscriptionId;

        internal MutationSubscription(
            DiskPackageCache owner,
            long subscriptionId)
        {
            _owner = owner;
            _subscriptionId = subscriptionId;
        }

        public void Dispose()
        {
            DiskPackageCache? owner =
                Interlocked.Exchange(ref _owner, null);
            owner?.UnsubscribeFromMutations(_subscriptionId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
