// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
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
    IDisposable
{
    private const string PackageSubdirectory = "package";
    private const string IndexFileName = ".index.json";

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
        return CreateRecord(
            cacheKey.DisplayReference,
            inspection,
            entry);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default)
    {
        await RecoverPendingTransactionsAsync(ct).ConfigureAwait(false);
        PackageCacheKeySnapshot snapshot;
        await using (PackageCacheLease globalLease =
            await _coordinator.AcquireGlobalAsync(ct).ConfigureAwait(false))
        {
            snapshot = await SnapshotCacheKeysNoLockAsync(ct)
                .ConfigureAwait(false);
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

            CacheMetadataEntry? entry;
            await using (PackageCacheLease globalLease =
                await _coordinator.AcquireGlobalAsync(ct)
                    .ConfigureAwait(false))
            {
                entry = await _metadataStore.GetEntryAsync(cacheKey, ct)
                    .ConfigureAwait(false);
            }

            results.Add(CreateRecord(reference, inspection, entry));
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
            return CreateRecord(
                cacheKey.DisplayReference,
                initialInspection,
                existingEntry);
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
                    return CreateRecord(
                        cacheKey.DisplayReference,
                        initialInspection,
                        refreshedEntry);
                }
            }

            ReportProgress(
                options.Progress,
                reference.Name,
                PackageProgressPhase.Extracting);
            string stagingDirectory = Path.Combine(
                acquiredContent.OperationDirectory,
                "expanded");
            PackageManifest manifest;
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

                manifest = identity.Manifest;
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
                    ?? acquiredContent.Sha256
            };

            PackageCacheInspection committedInspection;
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

            return new PackageRecord
            {
                Reference = reference,
                DirectoryPath = committedInspection.TargetPath,
                ContentPath = committedInspection.ContentPath!,
                Manifest = manifest,
                InstalledAt = entry.DownloadDateTime,
                SizeBytes = sizeBytes
            };
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
                return CreateRecord(
                    cacheKey.DisplayReference,
                    inspection,
                    existingEntry);
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
                    ?? acquiredContent.Sha256
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
                    return CreateRecord(
                        cacheKey.DisplayReference,
                        currentInspection,
                        existingEntry);
                }

                ThrowIfInstallCannotReplace(
                    cacheKey,
                    currentInspection,
                    options);
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

            return new PackageRecord
            {
                Reference = cacheKey.DisplayReference,
                DirectoryPath = committedInspection.TargetPath,
                ContentPath = committedInspection.ContentPath!,
                Manifest = identity.Manifest,
                InstalledAt = entry.DownloadDateTime,
                SizeBytes = sizeBytes
            };
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

        string indexPath = Path.Combine(
            inspection.ContentPath!,
            IndexFileName);
        if (!File.Exists(indexPath))
            return null;

        await using FileStream stream = new FileStream(
            indexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PackageIndex>(
                stream,
                s_jsonOptions,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetFileContentAsync(
        PackageReference reference,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using PackageCacheLease identityLease =
            await AcquireIdentityAndRecoverAsync(cacheKey, ct)
                .ConfigureAwait(false);
        PackageCacheInspection inspection =
            await _validator.InspectAsync(cacheKey, ct)
                .ConfigureAwait(false);
        if (inspection.State != PackageCacheInspectionState.Valid)
            return null;

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

        return await File.ReadAllTextAsync(filePath, ct)
            .ConfigureAwait(false);
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
        await _metadataStore.SetEntryAsync(
                cacheKey,
                entry,
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

    private static PackageRecord CreateRecord(
        PackageReference reference,
        PackageCacheInspection inspection,
        CacheMetadataEntry? entry) =>
        new()
        {
            Reference = reference,
            DirectoryPath = inspection.TargetPath,
            ContentPath = inspection.ContentPath!,
            Manifest = inspection.Manifest!,
            InstalledAt = entry?.DownloadDateTime,
            SizeBytes = entry?.SizeBytes
        };

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

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
