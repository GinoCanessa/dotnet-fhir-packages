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

    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PackageInstallLimits _installLimits;
    private readonly PackageCacheValidator _validator;
    private readonly PackageCacheMetadataStore _metadataStore;
    private readonly PackageCacheCommitter _committer;

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
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver)
    {
        ArgumentNullException.ThrowIfNull(installLimits);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(faultObserver);
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
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(reference, ct)
                    .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Valid)
                return null;

            CacheMetadataEntry? entry =
                await _metadataStore.GetEntryAsync(
                        inspection.CacheKey,
                        ct)
                    .ConfigureAwait(false);
            return CreateRecord(
                inspection.CacheKey.DisplayReference,
                inspection,
                entry);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            CacheMetadata metadata = await _metadataStore.ReadAsync(ct)
                .ConfigureAwait(false);
            List<PackageRecord> results = [];
            foreach ((
                PackageCacheKey cacheKey,
                string _) in EnumeratePackageTargets())
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

                PackageCacheInspection inspection =
                    await _validator.InspectAsync(cacheKey, ct)
                        .ConfigureAwait(false);
                if (inspection.State != PackageCacheInspectionState.Valid)
                    continue;

                metadata.Packages.TryGetValue(
                    cacheKey.MetadataKey,
                    out CacheMetadataEntry? entry);
                results.Add(CreateRecord(reference, inspection, entry));
            }

            return results;
        }
        finally
        {
            _installLock.Release();
        }
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

        await InspectInstallTargetAsync(
                cacheKey,
                options,
                ct)
            .ConfigureAwait(false);

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
                acquiredContent = await PackageContentAcquirer.AcquireAsync(
                        tarballStream,
                        CacheDirectory,
                        effectiveLimits,
                        options.ReportedContentLength,
                        options.ExpectedSha256Sum,
                        options.ExpectedShaSum,
                        options.VerifyChecksum,
                        reference.FhirDirective,
                        ct)
                    .ConfigureAwait(false);
                ownsAcquiredContent = true;
            }

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
            await _installLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecoverNoLockAsync(ct).ConfigureAwait(false);
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
            finally
            {
                _installLock.Release();
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
    public async Task<bool> RemoveAsync(
        PackageReference reference,
        CancellationToken ct = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
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
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            List<(PackageCacheKey Key, string Path)> targets =
                EnumeratePackageTargets().ToList();
            int count = 0;
            foreach ((PackageCacheKey cacheKey, string _) in targets)
            {
                ct.ThrowIfCancellationRequested();
                PackageCacheInspection inspection =
                    await _validator.InspectAsync(cacheKey, ct)
                        .ConfigureAwait(false);
                if (inspection.State == PackageCacheInspectionState.Missing)
                    continue;

                bool removed = await _committer.RemoveAsync(
                        cacheKey,
                        inspection,
                        Guid.NewGuid().ToString("N"),
                        ct)
                    .ConfigureAwait(false);
                if (removed)
                    count++;
            }

            RemoveEmptyScopeDirectories();
            await _metadataStore.ClearManagedEntriesAsync(ct)
                .ConfigureAwait(false);
            return count;
        }
        finally
        {
            _installLock.Release();
        }
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
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(reference, ct)
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
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetFileContentAsync(
        PackageReference reference,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(reference, ct)
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
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public string? GetPackageContentPath(PackageReference reference)
    {
        _installLock.Wait();
        try
        {
            RecoverNoLockAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            PackageCacheInspection inspection = _validator.Inspect(reference);
            return inspection.State == PackageCacheInspectionState.Valid
                ? inspection.ContentPath
                : null;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CacheMetadata> GetMetadataAsync(
        CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            return await _metadataStore.ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        PackageReference reference,
        CacheMetadataEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(ct).ConfigureAwait(false);
            await _metadataStore.SetEntryAsync(
                    cacheKey,
                    entry,
                    mutation: null,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
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

    internal async Task<PackageCacheInspection> InspectAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(cancellationToken)
                .ConfigureAwait(false);
            return await _validator.InspectAsync(
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task InspectInstallTargetAsync(
        PackageCacheKey cacheKey,
        InstallCacheOptions options,
        CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await RecoverNoLockAsync(cancellationToken)
                .ConfigureAwait(false);
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            ThrowIfInstallCannotReplace(cacheKey, inspection, options);
        }
        finally
        {
            _installLock.Release();
        }
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

    private async Task RecoverNoLockAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _committer.RecoverAsync(cancellationToken)
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

    /// <inheritdoc />
    public void Dispose()
    {
        _installLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
