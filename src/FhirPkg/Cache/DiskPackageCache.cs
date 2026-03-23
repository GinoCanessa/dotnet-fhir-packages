// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using FhirPkg.Indexing;
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
/// Installation is atomic: tarballs are extracted to a temporary directory, normalized
/// to ensure the <c>package/</c> subdirectory exists, then moved to the final location.
/// On Windows, cross-volume moves fall back to a copy-then-delete strategy.
/// </para>
/// </remarks>
public class DiskPackageCache : IPackageCache, IDisposable
{
    private const string PackageSubdirectory = "package";
    private const string ManifestFileName = "package.json";
    private const string IndexFileName = ".index.json";
    private const string MetadataFileName = "packages.ini";
    private const string MetadataDateFormat = "yyyyMMddHHmmss";
    private const string DirectorySeparator = "#";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

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
    {
        _logger = logger ?? NullLogger<DiskPackageCache>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        string contentPath = GetContentPath(reference);
        if (!Directory.Exists(contentPath))
            return null;

        PackageManifest? manifest = await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false);
        if (manifest is null)
            return null;

        string directoryPath = GetPackageDirectoryPath(reference);
        CacheMetadata metadata = await GetMetadataAsync(ct).ConfigureAwait(false);
        string directive = reference.FhirDirective;

        metadata.Packages.TryGetValue(directive, out CacheMetadataEntry? entry);

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

        // Find all directories matching the name#version pattern
        foreach (string dir in Directory.GetDirectories(CacheDirectory, $"*{DirectorySeparator}*"))
        {
            ct.ThrowIfCancellationRequested();

            string dirName = Path.GetFileName(dir);
            int separatorIndex = dirName.IndexOf(DirectorySeparator, StringComparison.Ordinal);
            if (separatorIndex <= 0)
                continue;

            string name = dirName[..separatorIndex];
            string version = dirName[(separatorIndex + 1)..];

            // Apply filters
            if (packageIdFilter is not null
                && !name.StartsWith(packageIdFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (versionFilter is not null
                && !string.Equals(version, versionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            PackageReference reference = new PackageReference(name, version);
            string contentPath = GetContentPath(reference);
            if (!Directory.Exists(contentPath))
                continue;

            PackageManifest? manifest = await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false);
            if (manifest is null)
                continue;

            string directive = reference.FhirDirective;
            metadata.Packages.TryGetValue(directive, out CacheMetadataEntry? entry);

            results.Add(new PackageRecord
            {
                Reference = reference,
                DirectoryPath = GetPackageDirectoryPath(reference),
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

        options ??= new InstallCacheOptions();

        // Verify checksum if requested
        if (options.VerifyChecksum)
        {
            if (options.ExpectedSha256Sum is not null)
            {
                if (!CheckSum.VerifySha256(tarballStream, options.ExpectedSha256Sum))
                {
                    throw new InvalidOperationException(
                        $"SHA-256 checksum mismatch for package {reference.FhirDirective}. " +
                        $"Expected: {options.ExpectedSha256Sum}");
                }
            }
            else if (options.ExpectedShaSum is not null)
            {
                if (!CheckSum.Verify(tarballStream, options.ExpectedShaSum))
                {
                    throw new InvalidOperationException(
                        $"SHA-1 checksum mismatch for package {reference.FhirDirective}. " +
                        $"Expected: {options.ExpectedShaSum}");
                }
            }
            // Verify now resets position by default when the stream supports seeking
        }

        string targetDirectory = GetPackageDirectoryPath(reference);

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check if already installed
            if (Directory.Exists(targetDirectory))
            {
                if (!options.OverwriteExisting)
                {
                    throw new InvalidOperationException(
                        $"Package {reference.FhirDirective} is already installed. " +
                        "Set OverwriteExisting to true to overwrite.");
                }

                // Remove existing installation
                Directory.Delete(targetDirectory, recursive: true);
            }

            // Extract to a temporary directory (prefer system temp, fall back to cache)
            string tempDir = TempDirectory.Create("fhir-pkg", CacheDirectory);
            try
            {
                await TarballExtractor.ExtractAsync(tarballStream, tempDir, ct).ConfigureAwait(false);

                // Normalize structure (ensure package/ subdir exists)
                TarballExtractor.NormalizePackageStructure(tempDir);

                // Atomic move to cache
                AtomicMoveToCache(tempDir, targetDirectory);
            }
            finally
            {
                // Clean up temp directory if it still exists (move failed or was a copy)
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up temp directory '{TempDir}'", tempDir); }
                }
            }

            // Update metadata
            string contentPath = GetContentPath(reference);
            long sizeBytes = CalculateDirectorySize(contentPath);
            CacheMetadataEntry entry = new CacheMetadataEntry
            {
                DownloadDateTime = _timeProvider.GetUtcNow().UtcDateTime,
                SizeBytes = sizeBytes
            };
            await UpdateMetadataAsync(reference, entry, ct).ConfigureAwait(false);

            // Read the installed manifest and build the record
            PackageManifest manifest = await ReadManifestFromPathAsync(contentPath, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Installed package {reference.FhirDirective} is missing package.json manifest.");

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
            _installLock.Release();
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
            await RemoveFromMetadataAsync(reference, ct).ConfigureAwait(false);

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

            foreach (string dir in Directory.GetDirectories(CacheDirectory, $"*{DirectorySeparator}*"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(dir, recursive: true);
                    count++;
                }
                catch (IOException)
                {
                    // Skip directories that cannot be deleted (in use, permissions, etc.)
                }
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

        Dictionary<string, CacheMetadataEntry> packages = new Dictionary<string, CacheMetadataEntry>(StringComparer.Ordinal);

        if (ini.TryGetValue("packages", out IReadOnlyDictionary<string, string>? packagesSection))
        {
            // Also try to get sizes
            ini.TryGetValue("package-sizes", out IReadOnlyDictionary<string, string>? sizesSection);

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

                packages[directive] = new CacheMetadataEntry
                {
                    DownloadDateTime = downloadDate ?? DateTime.MinValue,
                    SizeBytes = sizeBytes
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
        ct.ThrowIfCancellationRequested();

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

        string directive = reference.FhirDirective;
        string dateStr = entry.DownloadDateTime.ToString(MetadataDateFormat, CultureInfo.InvariantCulture);

        // Update the packages section
        sections["packages"][directive] = dateStr;

        // Update the package-sizes section
        if (entry.SizeBytes.HasValue)
        {
            sections["package-sizes"][directive] = entry.SizeBytes.Value.ToString(CultureInfo.InvariantCulture);
        }

        await IniParser.WriteFileAsync(metadataPath, sections.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a package entry from the cache metadata file.
    /// </summary>
    private async Task RemoveFromMetadataAsync(PackageReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string metadataPath = Path.Combine(CacheDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
            return;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini = await IniParser.ParseFileAsync(metadataPath, ct).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? key, IReadOnlyDictionary<string, string>? value) in ini)
        {
            sections[key] = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
        }

        string directive = reference.FhirDirective;

        if (sections.TryGetValue("packages", out Dictionary<string, string>? pkgSection))
            pkgSection.Remove(directive);

        if (sections.TryGetValue("package-sizes", out Dictionary<string, string>? sizeSection))
            sizeSection.Remove(directive);

        await IniParser.WriteFileAsync(metadataPath, sections.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
    }

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
    /// Performs an atomic move from the temp directory to the cache directory.
    /// Falls back to recursive copy + delete if the move fails (e.g., cross-volume on Windows).
    /// </summary>
    private static void AtomicMoveToCache(string sourceDir, string targetDir)
    {
        string? targetParent = Path.GetDirectoryName(targetDir);
        if (targetParent is not null)
            Directory.CreateDirectory(targetParent);

        try
        {
            Directory.Move(sourceDir, targetDir);
        }
        catch (IOException)
        {
            // Cross-volume move: fall back to copy + delete
            CopyDirectoryRecursive(sourceDir, targetDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    /// <summary>
    /// Recursively copies all files and subdirectories from source to destination.
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destDir);
        }
    }

    /// <summary>
    /// Gets the full path to a package's root directory in the cache.
    /// </summary>
    private string GetPackageDirectoryPath(PackageReference reference)
        => Path.Combine(CacheDirectory, reference.CacheDirectoryName);

    /// <summary>
    /// Gets the full path to a package's content directory (package/ subfolder).
    /// </summary>
    private string GetContentPath(PackageReference reference)
        => Path.Combine(CacheDirectory, reference.CacheDirectoryName, PackageSubdirectory);

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
