// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirPkg.Cache;

/// <summary>
/// Disk-based FHIR package cache implementation.
/// By default, the cache root is resolved as follows: an explicit path passed to
/// the constructor takes priority; if <c>null</c>, the <c>PACKAGE_CACHE_FOLDER</c>
/// environment variable is used when set; otherwise falls back to <c>~/.fhir/packages</c>.
/// Provides thread-safe installation with atomic directory moves and
/// concurrent-safe reads via a <see cref="SemaphoreSlim"/> for write operations.
/// </summary>
/// <remarks>
/// <para>
/// The cache stores each package version in a directory named <c>{name}#{version}</c>,
/// with all content under a <c>package/</c> subdirectory. Metadata is tracked in a
/// <c>packages.ini</c> file at the cache root.
/// </para>
/// <para>
/// Tarballs are staged and validated beneath the cache root before promotion.
/// Replacement keeps a backup of the prior package until content and metadata commit,
/// and restores that backup when promotion or metadata update fails.
/// </para>
/// </remarks>
public class DiskPackageCache : IPackageCache, IDisposable
{
    private const string PackageSubdirectory = "package";
    private const string ManifestFileName = "package.json";
    private const string IndexFileName = ".index.json";
    private const string MetadataFileName = "packages.ini";
    private const string MetadataDateFormat = "yyyyMMddHHmmss";
    private const string SourcePublicationDatesSection = "package-source-publication-dates";
    private const string ArchiveSha256Section = "package-archive-sha256";
    private const string OperationsDirectoryName = ".fhirpkg";
    private const string BackupDirectoryName = "backup";

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

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <summary>
    /// Creates a new <see cref="DiskPackageCache"/> using the specified or default cache directory.
    /// </summary>
    /// <param name="cacheDirectory">
    /// Full path to the cache directory. If <c>null</c>, the <c>PACKAGE_CACHE_FOLDER</c>
    /// environment variable is used when set; otherwise defaults to
    /// <c>~/.fhir/packages</c> (or <c>%USERPROFILE%\.fhir\packages</c> on Windows).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
    public DiskPackageCache(string? cacheDirectory = null, ILogger<DiskPackageCache>? logger = null, TimeProvider? timeProvider = null)
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
    {
        ArgumentNullException.ThrowIfNull(installLimits);
        installLimits.Validate();

        _logger = logger ?? NullLogger<DiskPackageCache>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installLimits = PackageInstallLimits.ResolvePerCall(
            installLimits,
            requestedLimits: null);
        CacheDirectory = cacheDirectory
            ?? Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fhir",
                "packages");

        Directory.CreateDirectory(CacheDirectory);
    }

    /// <inheritdoc />
    public Task<bool> IsInstalledAsync(PackageReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string contentPath = GetContentPath(reference);
        return Task.FromResult(Directory.Exists(contentPath));
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> GetPackageAsync(PackageReference reference, CancellationToken ct = default)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string contentPath = GetContentPath(reference);
        if (!Directory.Exists(contentPath))
            return null;

        PackageManifest? manifest = await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false);
        if (manifest is null)
            return null;

        string directoryPath = GetPackageDirectoryPath(reference);
        CacheMetadata metadata = await GetMetadataAsync(ct).ConfigureAwait(false);
        metadata.Packages.TryGetValue(cacheKey.MetadataKey, out CacheMetadataEntry? entry);

        return new PackageRecord
        {
            Reference = reference,
            DirectoryPath = directoryPath,
            ContentPath = contentPath,
            Manifest = manifest,
            InstalledAt = entry?.DownloadDateTime,
            SizeBytes = entry?.SizeBytes
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(CacheDirectory))
            return [];

        // Read metadata once for all packages instead of per-directory
        CacheMetadata metadata = await GetMetadataAsync(ct).ConfigureAwait(false);
        List<PackageRecord> results = new List<PackageRecord>();

        foreach ((PackageCacheKey cacheKey, string directoryPath) in EnumeratePackageDirectories())
        {
            ct.ThrowIfCancellationRequested();

            PackageReference reference = cacheKey.CanonicalReference;

            // Apply filters
            if (packageIdFilter is not null
                && !reference.Name.StartsWith(packageIdFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (versionFilter is not null
                && !string.Equals(reference.Version, versionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            string contentPath = GetContentPath(reference);
            if (!Directory.Exists(contentPath))
                continue;

            PackageManifest? manifest = await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false);
            if (manifest is null)
                continue;

            metadata.Packages.TryGetValue(cacheKey.MetadataKey, out CacheMetadataEntry? entry);

            results.Add(new PackageRecord
            {
                Reference = reference,
                DirectoryPath = directoryPath,
                ContentPath = contentPath,
                Manifest = manifest,
                InstalledAt = entry?.DownloadDateTime,
                SizeBytes = entry?.SizeBytes
            });
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
            throw new ArgumentException("PackageReference must have a version for installation.", nameof(reference));

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        options ??= new InstallCacheOptions();
        PackageInstallLimits effectiveLimits = PackageInstallLimits.ResolvePerCall(
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

            string targetDirectory = cacheKey.GetPackageDirectoryPath(CacheDirectory);
            string operationRoot = Path.Combine(
                CacheDirectory,
                OperationsDirectoryName);
            string backupRoot = Path.Combine(
                operationRoot,
                BackupDirectoryName);
            string stagingDirectory = Path.Combine(
                acquiredContent.OperationDirectory,
                "expanded");
            string backupDirectory = Path.Combine(
                backupRoot,
                $"{cacheKey.LockHash}-{acquiredContent.OperationId}");

            await _installLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool targetExists = Directory.Exists(targetDirectory);
                if (targetExists && !options.OverwriteExisting)
                {
                    throw new PackageInstallException(
                        PackageInstallErrorCode.CommitFailed,
                        PackageInstallStage.Commit,
                        $"Package {reference.FhirDirective} is already installed. " +
                        "Set OverwriteExisting to true to overwrite.",
                        reference.FhirDirective);
                }

                Directory.CreateDirectory(backupRoot);
                string? targetParent = Path.GetDirectoryName(targetDirectory);
                if (targetParent is not null)
                    Directory.CreateDirectory(targetParent);

                bool commitSucceeded = false;
                try
                {
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

                    TarballExtractor.NormalizePackageStructure(stagingDirectory);

                    string stagedContentPath = Path.Combine(
                        stagingDirectory,
                        PackageSubdirectory);
                    PackageManifest? manifest;
                    try
                    {
                        manifest = await ReadManifestFromPathAsync(stagedContentPath, ct)
                            .ConfigureAwait(false);
                    }
                    catch (JsonException exception)
                    {
                        throw InvalidArchive(reference, exception);
                    }

                    if (manifest is null)
                    {
                        throw new PackageInstallException(
                            PackageInstallErrorCode.InvalidArchive,
                            PackageInstallStage.ArchiveValidation,
                            $"Package {reference.FhirDirective} is missing package.json manifest.",
                            reference.FhirDirective);
                    }

                    long sizeBytes = CalculateDirectorySize(stagedContentPath);
                    CacheMetadataEntry entry = new CacheMetadataEntry
                    {
                        DownloadDateTime = _timeProvider.GetUtcNow().UtcDateTime,
                        SizeBytes = sizeBytes,
                        SourcePublicationDate = options.SourcePublicationDate,
                        ArchiveSha256 = options.ArchiveSha256
                            ?? acquiredContent.Sha256
                    };
                    MetadataFileSnapshot metadataBefore =
                        await CaptureMetadataFileNoLockAsync(ct)
                            .ConfigureAwait(false);

                    ct.ThrowIfCancellationRequested();

                    bool backupCreated = false;
                    bool replacementPromoted = false;
                    try
                    {
                        if (targetExists)
                        {
                            Directory.Move(targetDirectory, backupDirectory);
                            backupCreated = true;
                        }

                        Directory.Move(stagingDirectory, targetDirectory);
                        replacementPromoted = true;
                        await UpdateMetadataNoLockAsync(
                                reference,
                                entry,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        commitSucceeded = true;
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        await RollbackInstallNoLockAsync(
                                reference,
                                targetDirectory,
                                backupDirectory,
                                backupCreated,
                                replacementPromoted,
                                metadataBefore)
                            .ConfigureAwait(false);
                        throw CommitFailure(reference, exception);
                    }
                    catch (IOException exception)
                    {
                        await RollbackInstallNoLockAsync(
                                reference,
                                targetDirectory,
                                backupDirectory,
                                backupCreated,
                                replacementPromoted,
                                metadataBefore)
                            .ConfigureAwait(false);
                        throw CommitFailure(reference, exception);
                    }

                    TryDeleteDirectory(
                        backupDirectory,
                        "backup directory after successful package replacement");

                    string contentPath = Path.Combine(
                        targetDirectory,
                        PackageSubdirectory);
                    return new PackageRecord
                    {
                        Reference = reference,
                        DirectoryPath = targetDirectory,
                        ContentPath = contentPath,
                        Manifest = manifest,
                        InstalledAt = entry.DownloadDateTime,
                        SizeBytes = sizeBytes
                    };
                }
                finally
                {
                    TryDeleteDirectory(
                        stagingDirectory,
                        "expanded staging directory after package installation");

                    if (!commitSucceeded && !Directory.Exists(targetDirectory))
                    {
                        TryRestoreBackup(
                            backupDirectory,
                            targetDirectory,
                            "backup directory during final rollback");
                    }
                }
            }
            finally
            {
                _installLock.Release();
            }
        }
        finally
        {
            if (ownsAcquiredContent && acquiredContent is not null)
                await acquiredContent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(PackageReference reference, CancellationToken ct = default)
    {
        string directoryPath = GetPackageDirectoryPath(reference);
        if (!Directory.Exists(directoryPath))
            return false;

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directoryPath))
                return false;

            Directory.Delete(directoryPath, recursive: true);

            // Remove from metadata
            await RemoveFromMetadataNoLockAsync(reference, ct).ConfigureAwait(false);

            return true;
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
            if (!Directory.Exists(CacheDirectory))
                return 0;

            int count = 0;

            List<(PackageCacheKey Key, string DirectoryPath)> packageDirectories =
                EnumeratePackageDirectories().ToList();

            foreach ((PackageCacheKey _, string directoryPath) in packageDirectories)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(directoryPath, recursive: true);
                    count++;
                }
                catch (IOException)
                {
                    // Skip directories that cannot be deleted (in use, permissions, etc.)
                }
            }

            foreach (string scopeDirectory in Directory.GetDirectories(CacheDirectory, "@*"))
            {
                if (!Directory.EnumerateFileSystemEntries(scopeDirectory).Any())
                    Directory.Delete(scopeDirectory);
            }

            // Clear metadata file
            string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);

            return count;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PackageManifest?> ReadManifestAsync(PackageReference reference, CancellationToken ct = default)
    {
        string contentPath = GetContentPath(reference);
        if (!Directory.Exists(contentPath))
            return null;

        return await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageIndex?> GetIndexAsync(PackageReference reference, CancellationToken ct = default)
    {
        string contentPath = GetContentPath(reference);
        if (!Directory.Exists(contentPath))
            return null;

        string indexPath = Path.Combine(contentPath, IndexFileName);
        if (!File.Exists(indexPath))
            return null;

        await using FileStream stream = File.OpenRead(indexPath);
        return await JsonSerializer.DeserializeAsync<PackageIndex>(stream, s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetFileContentAsync(
        PackageReference reference,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        string contentPath = GetContentPath(reference);
        if (!Directory.Exists(contentPath))
            return null;

        string filePath = Path.GetFullPath(Path.Combine(contentPath, relativePath));

        // Path traversal protection
        if (!filePath.StartsWith(Path.GetFullPath(contentPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Relative path must not traverse outside the package directory.", nameof(relativePath));

        if (!File.Exists(filePath))
            return null;

        return await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string? GetPackageContentPath(PackageReference reference)
    {
        string contentPath = GetContentPath(reference);
        return Directory.Exists(contentPath) ? contentPath : null;
    }

    /// <inheritdoc />
    public async Task<CacheMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetMetadataNoLockAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task<CacheMetadata> GetMetadataNoLockAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini = await IniParser.ParseFileAsync(metadataPath, ct).ConfigureAwait(false);

        int cacheVersion = 3;
        if (ini.TryGetValue("cache", out IReadOnlyDictionary<string, string>? cacheSection)
            && cacheSection.TryGetValue("version", out string? versionStr)
            && int.TryParse(versionStr, out int parsed))
        {
            cacheVersion = parsed;
        }

        Dictionary<string, CacheMetadataEntry> packages = new Dictionary<string, CacheMetadataEntry>(StringComparer.OrdinalIgnoreCase);

        if (ini.TryGetValue("packages", out IReadOnlyDictionary<string, string>? packagesSection))
        {
            // Also try to get sizes
            ini.TryGetValue("package-sizes", out IReadOnlyDictionary<string, string>? sizesSection);
            ini.TryGetValue(SourcePublicationDatesSection, out IReadOnlyDictionary<string, string>? publicationDatesSection);
            ini.TryGetValue(ArchiveSha256Section, out IReadOnlyDictionary<string, string>? archiveSha256Section);

            foreach ((string? directive, string? dateStr) in packagesSection)
            {
                DateTime? downloadDate = null;
                if (DateTime.TryParseExact(dateStr, MetadataDateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime dt))
                {
                    downloadDate = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }

                long? sizeBytes = null;
                if (sizesSection is not null
                    && sizesSection.TryGetValue(directive, out string? sizeStr)
                    && long.TryParse(sizeStr, out long size))
                {
                    sizeBytes = size;
                }

                DateTimeOffset? sourcePublicationDate = null;
                if (publicationDatesSection is not null
                    && publicationDatesSection.TryGetValue(directive, out string? publicationDateText)
                    && DateTimeOffset.TryParse(
                        publicationDateText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTimeOffset parsedPublicationDate))
                {
                    sourcePublicationDate = parsedPublicationDate;
                }

                string? archiveSha256 = null;
                if (archiveSha256Section is not null)
                    archiveSha256Section.TryGetValue(directive, out archiveSha256);

                packages[directive] = new CacheMetadataEntry
                {
                    DownloadDateTime = downloadDate ?? DateTime.MinValue,
                    SizeBytes = sizeBytes,
                    SourcePublicationDate = sourcePublicationDate,
                    ArchiveSha256 = archiveSha256
                };
            }
        }

        return new CacheMetadata
        {
            CacheVersion = cacheVersion,
            Packages = packages
        };
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(PackageReference reference, CacheMetadataEntry entry, CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateMetadataNoLockAsync(reference, entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task UpdateMetadataNoLockAsync(
        PackageReference reference,
        CacheMetadataEntry entry,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini = await IniParser.ParseFileAsync(metadataPath, ct).ConfigureAwait(false);

        // Build mutable copy of the INI structure
        Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? key, IReadOnlyDictionary<string, string>? value) in ini)
        {
            sections[key] = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
        }

        // Ensure required sections exist
        EnsureSection(sections, "cache", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["version"] = "3" });
        EnsureSection(sections, "urls");
        EnsureSection(sections, "local");
        EnsureSection(sections, "packages");
        EnsureSection(sections, "package-sizes");
        EnsureSection(sections, SourcePublicationDatesSection);
        EnsureSection(sections, ArchiveSha256Section);

        string directive = cacheKey.MetadataKey;
        string dateStr = entry.DownloadDateTime.ToString(MetadataDateFormat, CultureInfo.InvariantCulture);

        // Update the packages section
        sections["packages"].Remove(directive);
        sections["packages"][directive] = dateStr;

        // Update the package-sizes section
        sections["package-sizes"].Remove(directive);
        if (entry.SizeBytes.HasValue)
        {
            sections["package-sizes"][directive] = entry.SizeBytes.Value.ToString(CultureInfo.InvariantCulture);
        }

        sections[SourcePublicationDatesSection].Remove(directive);
        if (entry.SourcePublicationDate.HasValue)
        {
            sections[SourcePublicationDatesSection][directive] =
                entry.SourcePublicationDate.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        sections[ArchiveSha256Section].Remove(directive);
        if (entry.ArchiveSha256 is not null)
        {
            sections[ArchiveSha256Section][directive] = entry.ArchiveSha256;
        }

        await IniParser.WriteFileAsync(metadataPath, sections.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a package entry from the cache metadata file.
    /// </summary>
    private async Task RemoveFromMetadataNoLockAsync(
        PackageReference reference,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
            return;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini = await IniParser.ParseFileAsync(metadataPath, ct).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? key, IReadOnlyDictionary<string, string>? value) in ini)
        {
            sections[key] = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
        }

        string directive = cacheKey.MetadataKey;

        if (sections.TryGetValue("packages", out Dictionary<string, string>? pkgSection))
            pkgSection.Remove(directive);

        if (sections.TryGetValue("package-sizes", out Dictionary<string, string>? sizeSection))
            sizeSection.Remove(directive);

        if (sections.TryGetValue(SourcePublicationDatesSection, out Dictionary<string, string>? publicationSection))
            publicationSection.Remove(directive);

        if (sections.TryGetValue(ArchiveSha256Section, out Dictionary<string, string>? archiveHashSection))
            archiveHashSection.Remove(directive);

        await IniParser.WriteFileAsync(metadataPath, sections.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
    }

    private async Task<MetadataFileSnapshot> CaptureMetadataFileNoLockAsync(
        CancellationToken ct)
    {
        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
            return new MetadataFileSnapshot(false, null);

        byte[] content = await File.ReadAllBytesAsync(metadataPath, ct)
            .ConfigureAwait(false);
        return new MetadataFileSnapshot(true, content);
    }

    private async Task RestoreMetadataFileNoLockAsync(
        PackageReference reference,
        MetadataFileSnapshot snapshot)
    {
        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        try
        {
            if (snapshot.Existed)
            {
                await File.WriteAllBytesAsync(
                        metadataPath,
                        snapshot.Content!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to restore metadata while rolling back {Directive}.",
                reference.FhirDirective);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to restore metadata while rolling back {Directive}.",
                reference.FhirDirective);
        }
    }

    private async Task RollbackInstallNoLockAsync(
        PackageReference reference,
        string targetDirectory,
        string backupDirectory,
        bool backupCreated,
        bool replacementPromoted,
        MetadataFileSnapshot metadataBefore)
    {
        bool targetRemoved = !replacementPromoted
            || TryDeleteDirectory(
                targetDirectory,
                "new package target during rollback");

        if (backupCreated && targetRemoved)
        {
            TryRestoreBackup(
                backupDirectory,
                targetDirectory,
                "prior package target during rollback");
        }

        await RestoreMetadataFileNoLockAsync(reference, metadataBefore)
            .ConfigureAwait(false);
    }

    private bool TryDeleteDirectory(string directoryPath, string purpose)
    {
        if (!Directory.Exists(directoryPath))
            return true;

        try
        {
            Directory.Delete(directoryPath, recursive: true);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete {Purpose} at '{DirectoryPath}'.",
                purpose,
                directoryPath);
            return false;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete {Purpose} at '{DirectoryPath}'.",
                purpose,
                directoryPath);
            return false;
        }
    }

    private bool TryRestoreBackup(
        string backupDirectory,
        string targetDirectory,
        string purpose)
    {
        if (!Directory.Exists(backupDirectory) || Directory.Exists(targetDirectory))
            return false;

        try
        {
            Directory.Move(backupDirectory, targetDirectory);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to restore {Purpose} from '{BackupDirectory}'.",
                purpose,
                backupDirectory);
            return false;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to restore {Purpose} from '{BackupDirectory}'.",
                purpose,
                backupDirectory);
            return false;
        }
    }

    private static PackageInstallException InvalidArchive(
        PackageReference reference,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            $"Package {reference.FhirDirective} contains an invalid archive.",
            reference.FhirDirective,
            exception);

    private static PackageInstallException CommitFailure(
        PackageReference reference,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.CommitFailed,
            PackageInstallStage.Commit,
            $"Package {reference.FhirDirective} could not be committed to the cache.",
            reference.FhirDirective,
            exception);

    private sealed record MetadataFileSnapshot(bool Existed, byte[]? Content);

    /// <summary>
    /// Reads and deserializes package.json from the given content directory.
    /// </summary>
    private static async Task<PackageManifest?> ReadManifestFromPathAsync(string contentPath, CancellationToken ct)
    {
        string manifestPath = Path.Combine(contentPath, ManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        await using FileStream stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<PackageManifest>(stream, s_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the full path to a package's root directory in the cache.
    /// </summary>
    private string GetPackageDirectoryPath(PackageReference reference)
        => PackageCacheKey.Create(reference).GetPackageDirectoryPath(CacheDirectory);

    /// <summary>
    /// Gets the full path to a package's content directory (package/ subfolder).
    /// </summary>
    private string GetContentPath(PackageReference reference)
        => Path.Combine(GetPackageDirectoryPath(reference), PackageSubdirectory);

    private IEnumerable<(PackageCacheKey Key, string DirectoryPath)> EnumeratePackageDirectories()
    {
        foreach (string directoryPath in Directory.GetDirectories(CacheDirectory))
        {
            string directoryName = Path.GetFileName(directoryPath);
            if (directoryName.StartsWith('.'))
                continue;

            if (PackageCacheKey.TryParseRelativePath(directoryName, out PackageCacheKey? unscopedKey))
            {
                yield return (unscopedKey!, directoryPath);
                continue;
            }

            if (!directoryName.StartsWith('@'))
                continue;

            foreach (string scopedDirectoryPath in Directory.GetDirectories(directoryPath))
            {
                string relativePath = Path.GetRelativePath(CacheDirectory, scopedDirectoryPath);
                if (PackageCacheKey.TryParseRelativePath(relativePath, out PackageCacheKey? scopedKey))
                    yield return (scopedKey!, scopedDirectoryPath);
            }
        }
    }

    /// <summary>
    /// Calculates the total size of all files in a directory tree.
    /// </summary>
    private static long CalculateDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return new DirectoryInfo(path)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Ensures a section exists in the INI structure, using defaults if not present.
    /// </summary>
    private static void EnsureSection(
        Dictionary<string, Dictionary<string, string>> sections,
        string sectionName,
        Dictionary<string, string>? defaults = null)
    {
        if (!sections.ContainsKey(sectionName))
            sections[sectionName] = defaults ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _installLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
