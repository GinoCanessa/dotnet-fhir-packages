// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirPkg;

/// <summary>
/// Production implementation of <see cref="IFhirPackageManager"/> that orchestrates
/// registry queries, cache operations, dependency resolution, and package installation.
/// </summary>
/// <remarks>
/// <para>
/// The manager can be constructed directly with <see cref="FhirPackageManagerOptions"/>
/// for standalone use, or injected via the DI container using
/// <see cref="ServiceCollectionExtensions.AddFhirPackageManagement"/>.
/// </para>
/// <para>
/// All public methods are thread-safe and propagate <see cref="CancellationToken"/>
/// through the entire call chain. Structured logging via <see cref="ILogger"/> provides
/// diagnostics at Debug, Information, Warning, and Error levels.
/// </para>
/// </remarks>
public sealed class FhirPackageManager : IFhirPackageManager, IDisposable
{
    private readonly IPackageCache _cache;
    private readonly IRegistryClient _registryClient;
    private readonly IVersionResolver _versionResolver;
    private readonly IDependencyResolver _dependencyResolver;
    private readonly IPackageIndexer _packageIndexer;
    private readonly MemoryResourceCache? _memoryCache;
    private readonly FhirPackageManagerOptions _options;
    private readonly ILogger<FhirPackageManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient? _ownedHttpClient;
    private bool _disposed;

    private const string LockFileName = "fhirpkg.lock.json";
    private const string ManifestFileName = "package.json";

    /// <summary>
    /// Initializes a new <see cref="FhirPackageManager"/> with default options.
    /// Creates all internal infrastructure components automatically.
    /// </summary>
    public FhirPackageManager()
        : this(new FhirPackageManagerOptions())
    {
    }

    /// <summary>
    /// Initializes a new <see cref="FhirPackageManager"/> with the specified options.
    /// Creates all internal infrastructure components (cache, HTTP client, registry clients,
    /// resolvers, indexer) based on the provided configuration.
    /// </summary>
    /// <param name="options">Configuration options for the package manager.</param>
    /// <param name="loggerFactory">Optional logger factory for creating typed loggers.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <c>null</c>.</exception>
    public FhirPackageManager(FhirPackageManagerOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _loggerFactory = factory;
        _logger = factory.CreateLogger<FhirPackageManager>();

        // Build cache
        _cache = new DiskPackageCache(options.CachePath);

        // Build HTTP client with configured timeout and redirect policy
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = options.MaxRedirects
        };
        _ownedHttpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = options.HttpTimeout
        };

        // Build registry client chain
        _registryClient = RegistryClientFactory.BuildRegistryClient(options, _ownedHttpClient, factory);

        // Build resolvers
        _versionResolver = new VersionResolver(_registryClient, _logger);
        _dependencyResolver = new DependencyResolver(_registryClient, _versionResolver, _cache, _logger);

        // Build indexer
        _packageIndexer = new PackageIndexer(factory.CreateLogger<PackageIndexer>());

        // Build optional memory cache
        if (options.ResourceCacheSize > 0)
        {
            _memoryCache = new MemoryResourceCache(options.ResourceCacheSize, options.ResourceCacheSafeMode);
        }
    }

    /// <summary>
    /// Initializes a new <see cref="FhirPackageManager"/> with externally provided dependencies.
    /// Intended for dependency injection scenarios where all components are registered in the DI container.
    /// </summary>
    /// <param name="cache">The package cache implementation.</param>
    /// <param name="registryClient">The registry client for querying and downloading packages.</param>
    /// <param name="versionResolver">The version resolver for resolving version specifiers.</param>
    /// <param name="dependencyResolver">The dependency resolver for transitive dependency trees.</param>
    /// <param name="packageIndexer">The package indexer for resource discovery.</param>
    /// <param name="options">Configuration options.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    /// <param name="memoryCache">Optional in-memory resource cache.</param>
    public FhirPackageManager(
        IPackageCache cache,
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IDependencyResolver dependencyResolver,
        IPackageIndexer packageIndexer,
        FhirPackageManagerOptions options,
        ILogger<FhirPackageManager> logger,
        MemoryResourceCache? memoryCache = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(dependencyResolver);
        ArgumentNullException.ThrowIfNull(packageIndexer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _registryClient = registryClient;
        _versionResolver = versionResolver;
        _dependencyResolver = dependencyResolver;
        _packageIndexer = packageIndexer;
        _options = options;
        _logger = logger;
        _loggerFactory = NullLoggerFactory.Instance;
        _memoryCache = memoryCache;
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> InstallAsync(
        string directive,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new InstallOptions();

        _logger.LogDebug("InstallAsync starting for directive '{Directive}'.", directive);

        // Step 1: Parse directive into a reference
        var reference = PackageReference.Parse(directive);
        _logger.LogDebug("Parsed directive to reference: {Name}#{Version}.", reference.Name, reference.Version);

        // Step 2: Apply known fixups
        reference = PackageFixups.Apply(reference);
        _logger.LogDebug("After fixups: {Name}#{Version}.", reference.Name, reference.Version);

        // Step 3: Check cache (skip download if already installed and not overwriting)
        if (!options.OverwriteExisting && reference.HasVersion)
        {
            var isInstalled = await _cache.IsInstalledAsync(reference, cancellationToken).ConfigureAwait(false);
            if (isInstalled)
            {
                _logger.LogInformation("Package {Name}#{Version} is already cached.", reference.Name, reference.Version);
                return await _cache.GetPackageAsync(reference, cancellationToken).ConfigureAwait(false);
            }
        }

        // Step 4: Resolve directive to an exact version
        ReportProgress(options.Progress, reference.Name, PackageProgressPhase.Resolving);

        var parsedDirective = DirectiveParser.Parse(directive);
        var resolved = await _registryClient.ResolveAsync(parsedDirective, new VersionResolveOptions
        {
            AllowPreRelease = options.AllowPreRelease,
            FhirRelease = options.PreferredFhirRelease
        }, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            _logger.LogWarning("Could not resolve directive '{Directive}' from any registry.", directive);
            ReportProgress(options.Progress, reference.Name, PackageProgressPhase.Failed);
            return null;
        }

        _logger.LogInformation("Resolved {Name} to version {Version} from {Registry}.",
            resolved.Reference.Name, resolved.Reference.Version, resolved.SourceRegistry?.Url ?? "unknown");

        // Re-check cache with the resolved exact version (the original directive may have been a wildcard)
        if (!options.OverwriteExisting)
        {
            var isInstalled = await _cache.IsInstalledAsync(resolved.Reference, cancellationToken).ConfigureAwait(false);
            if (isInstalled)
            {
                _logger.LogInformation("Resolved package {Name}#{Version} is already cached.",
                    resolved.Reference.Name, resolved.Reference.Version);
                ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Complete);
                return await _cache.GetPackageAsync(resolved.Reference, cancellationToken).ConfigureAwait(false);
            }
        }

        // Step 5: Download tarball
        ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Downloading);

        await using var downloadResult = await _registryClient.DownloadAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (downloadResult is null)
        {
            _logger.LogError("Failed to download package {Name}#{Version}.", resolved.Reference.Name, resolved.Reference.Version);
            ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Failed);
            return null;
        }

        // Step 6: Verify checksum if configured
        Stream contentStream = downloadResult.Content;
        MemoryStream? checksumBuffer = null;
        try
        {
            if (_options.VerifyChecksums && (resolved.Sha256Sum is not null || resolved.ShaSum is not null))
            {
                checksumBuffer = new MemoryStream();
                await contentStream.CopyToAsync(checksumBuffer, cancellationToken).ConfigureAwait(false);
                checksumBuffer.Position = 0;

                bool checksumValid;
                string algorithm;
                string expectedHash;

                if (resolved.Sha256Sum is not null)
                {
                    checksumValid = CheckSum.VerifySha256(checksumBuffer, resolved.Sha256Sum);
                    algorithm = "SHA-256";
                    expectedHash = resolved.Sha256Sum;
                }
                else
                {
                    checksumValid = CheckSum.Verify(checksumBuffer, resolved.ShaSum);
                    algorithm = "SHA-1";
                    expectedHash = resolved.ShaSum!;
                }

                if (!checksumValid)
                {
                    _logger.LogError(
                        "{Algorithm} checksum verification failed for {Name}#{Version}. Expected: {Expected}.",
                        algorithm, resolved.Reference.Name, resolved.Reference.Version, expectedHash);
                    ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Failed);
                    throw new InvalidOperationException(
                        $"{algorithm} checksum verification failed for {resolved.Reference.FhirDirective}.");
                }

                checksumBuffer.Position = 0;
                contentStream = checksumBuffer;
                _logger.LogDebug("{Algorithm} checksum verified for {Name}#{Version}.", algorithm, resolved.Reference.Name, resolved.Reference.Version);
            }

            // Step 7: Install to cache
            ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Extracting);

            var installCacheOptions = new InstallCacheOptions
            {
                OverwriteExisting = options.OverwriteExisting,
                VerifyChecksum = false // We already verified above
            };

            var record = await _cache.InstallAsync(resolved.Reference, contentStream, installCacheOptions, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Installed {Name}#{Version} to {Path}.",
                record.Reference.Name, record.Reference.Version, record.DirectoryPath);

            // Step 8: Recursively install dependencies if requested
            if (options.IncludeDependencies)
            {
                await InstallDependenciesAsync(record, options, cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(options.Progress, resolved.Reference.Name, PackageProgressPhase.Complete);
            return record;
        }
        finally
        {
            if (checksumBuffer is not null)
            {
                await checksumBuffer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
        IEnumerable<string> directives,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directives);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new InstallOptions();
        var directiveList = directives.ToList();

        if (directiveList.Count == 0)
            return [];

        _logger.LogInformation("InstallManyAsync starting for {Count} directives.", directiveList.Count);

        var results = new PackageInstallResult[directiveList.Count];
        using var semaphore = new SemaphoreSlim(_options.MaxParallelRegistryQueries);

        var tasks = directiveList.Select(async (directive, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var record = await InstallAsync(directive, options, cancellationToken).ConfigureAwait(false);
                results[index] = record is not null
                    ? new PackageInstallResult
                    {
                        Directive = directive,
                        Package = record,
                        Status = PackageInstallStatus.Installed
                    }
                    : new PackageInstallResult
                    {
                        Directive = directive,
                        Status = PackageInstallStatus.NotFound,
                        ErrorMessage = $"Package '{directive}' could not be resolved."
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to install '{Directive}'.", directive);
                results[index] = new PackageInstallResult
                {
                    Directive = directive,
                    Status = PackageInstallStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var installed = results.Count(r => r.Status == PackageInstallStatus.Installed);
        _logger.LogInformation("InstallManyAsync completed: {Installed}/{Total} packages installed.",
            installed, directiveList.Count);

        return results;
    }

    /// <inheritdoc />
    public async Task<PackageClosure> RestoreAsync(
        string projectPath,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new RestoreOptions();

        _logger.LogInformation("RestoreAsync starting for project at '{ProjectPath}'.", projectPath);

        // Step 1: Read project manifest (package.json)
        var manifestPath = Path.Combine(projectPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Package manifest not found at '{manifestPath}'.", manifestPath);
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest at '{manifestPath}'.");

        _logger.LogDebug("Read manifest: {Name}@{Version} with {DepCount} dependencies.",
            manifest.Name, manifest.Version, manifest.Dependencies?.Count ?? 0);

        // Step 2: Check for existing lock file
        var lockFilePath = Path.Combine(projectPath, LockFileName);
        PackageClosure closure;

        if (File.Exists(lockFilePath) && !options.OverwriteExisting)
        {
            _logger.LogDebug("Found existing lock file at '{LockFilePath}'.", lockFilePath);

            var lockFile = await ReadLockFileAsync(lockFilePath, cancellationToken).ConfigureAwait(false);

            // Verify lock file is not stale (all manifest dependencies are in the lock file)
            if (IsLockFileCurrent(manifest, lockFile))
            {
                _logger.LogInformation("Lock file is current. Restoring from lock file.");
                closure = await _dependencyResolver.RestoreFromLockFileAsync(lockFile, cancellationToken)
                    .ConfigureAwait(false);

                // Install all resolved packages from the lock file
                await InstallClosureAsync(closure, options, cancellationToken).ConfigureAwait(false);
                return closure;
            }

            _logger.LogInformation("Lock file is stale. Performing full dependency resolution.");
        }

        // Step 3: Resolve full dependency tree
        var resolveOptions = new DependencyResolveOptions
        {
            ConflictStrategy = options.ConflictStrategy,
            MaxDepth = options.MaxDepth,
            AllowPreRelease = options.AllowPreRelease,
            PreferredFhirRelease = options.PreferredFhirRelease
        };

        closure = await _dependencyResolver.ResolveAsync(manifest, resolveOptions, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Dependency resolution complete: {Resolved} resolved, {Missing} missing.",
            closure.Resolved.Count, closure.Missing.Count);

        // Step 4: Install all resolved packages
        await InstallClosureAsync(closure, options, cancellationToken).ConfigureAwait(false);

        // Step 5: Write lock file if configured
        if (options.WriteLockFile)
        {
            await WriteLockFileAsync(lockFilePath, closure, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lock file written to '{LockFilePath}'.", lockFilePath);
        }

        return closure;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRecord>> ListCachedAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("ListCachedAsync with filter '{Filter}'.", filter ?? "(none)");
        return await _cache.ListPackagesAsync(filter, ct: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        string directive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var reference = PackageReference.Parse(directive);
        _logger.LogInformation("Removing package {Name}#{Version} from cache.", reference.Name, reference.Version);

        var removed = await _cache.RemoveAsync(reference, cancellationToken).ConfigureAwait(false);

        if (removed)
        {
            _logger.LogInformation("Successfully removed {Name}#{Version}.", reference.Name, reference.Version);
            _memoryCache?.Clear();
        }
        else
        {
            _logger.LogWarning("Package {Name}#{Version} was not found in the cache.", reference.Name, reference.Version);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<int> CleanCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation("Cleaning all packages from cache.");
        var count = await _cache.ClearAsync(cancellationToken).ConfigureAwait(false);
        _memoryCache?.Clear();

        _logger.LogInformation("Removed {Count} packages from cache.", count);
        return count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("SearchAsync with criteria: Name={Name}, FhirVersion={FhirVersion}.",
            criteria.Name, criteria.FhirVersion);

        return await _registryClient.SearchAsync(criteria, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("GetPackageListingAsync for '{PackageId}'.", packageId);
        return await _registryClient.GetPackageListingAsync(packageId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ResolvedDirective?> ResolveAsync(
        string directive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogDebug("ResolveAsync for directive '{Directive}'.", directive);

        var parsedDirective = DirectiveParser.Parse(directive);
        var resolved = await _registryClient.ResolveAsync(parsedDirective, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (resolved is not null)
        {
            _logger.LogInformation("Resolved '{Directive}' to {Name}#{Version}.",
                directive, resolved.Reference.Name, resolved.Reference.Version);
        }
        else
        {
            _logger.LogWarning("Could not resolve directive '{Directive}'.", directive);
        }

        return resolved;
    }

    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(
        string tarballPath,
        RegistryEndpoint registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tarballPath);
        ArgumentNullException.ThrowIfNull(registry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(tarballPath))
        {
            throw new FileNotFoundException($"Tarball not found at '{tarballPath}'.", tarballPath);
        }

        _logger.LogInformation("Publishing tarball '{TarballPath}' to registry '{RegistryUrl}'.",
            tarballPath, registry.Url);

        // Extract the manifest to determine the package reference
        var reference = await ExtractReferenceFromTarballAsync(tarballPath, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Publishing as {Name}#{Version}.", reference.Name, reference.Version);

        // Find the appropriate registry client for the target endpoint
        var targetClient = FindRegistryClient(registry);

        // Open a fresh stream for the actual publish
        await using var tarballStream = File.OpenRead(tarballPath);
        var result = await targetClient.PublishAsync(reference, tarballStream, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation("Successfully published {Name}#{Version} to '{RegistryUrl}'.",
                reference.Name, reference.Version, registry.Url);
        }
        else
        {
            _logger.LogError("Failed to publish {Name}#{Version}: {Message} (HTTP {StatusCode}).",
                reference.Name, reference.Version, result.Message, result.StatusCode);
        }

        return result;
    }

    /// <summary>
    /// Releases unmanaged resources held by this instance, including
    /// the internally created <see cref="HttpClient"/> (if any).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ownedHttpClient?.Dispose();

        try { _logger.LogDebug("FhirPackageManager disposed."); }
        catch { /* Logger may already be disposed during DI container teardown */ }
    }

    #region Private helpers

    /// <summary>
    /// Recursively installs dependencies of a cached package.
    /// </summary>
    private async Task InstallDependenciesAsync(
        PackageRecord record,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var manifest = record.Manifest;
        if (manifest.Dependencies is null || manifest.Dependencies.Count == 0)
        {
            _logger.LogDebug("No dependencies for {Name}#{Version}.", record.Reference.Name, record.Reference.Version);
            return;
        }

        _logger.LogDebug("Installing {Count} dependencies for {Name}#{Version}.",
            manifest.Dependencies.Count, record.Reference.Name, record.Reference.Version);

        foreach (var (depName, depVersion) in manifest.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var depDirective = string.IsNullOrEmpty(depVersion) ? depName : $"{depName}#{depVersion}";
            _logger.LogDebug("Installing dependency: {Directive}.", depDirective);

            try
            {
                await InstallAsync(depDirective, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to install dependency '{Directive}' for {Name}#{Version}.",
                    depDirective, record.Reference.Name, record.Reference.Version);
            }
        }
    }

    /// <summary>
    /// Installs all packages referenced in a closure that are not already cached.
    /// </summary>
    private async Task InstallClosureAsync(
        PackageClosure closure,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var directives = closure.Resolved.Values
            .Where(r => r.HasVersion)
            .Select(r => r.FhirDirective)
            .ToList();

        if (directives.Count == 0)
            return;

        _logger.LogDebug("Installing {Count} packages from closure.", directives.Count);

        // Install without recursing into dependencies (the closure already has everything resolved)
        var installOptions = new InstallOptions
        {
            IncludeDependencies = false,
            OverwriteExisting = options.OverwriteExisting,
            AllowPreRelease = options.AllowPreRelease,
            PreferredFhirRelease = options.PreferredFhirRelease,
            Progress = options.Progress
        };

        await InstallManyAsync(directives, installOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether an existing lock file covers all dependencies in the manifest.
    /// </summary>
    private static bool IsLockFileCurrent(PackageManifest manifest, PackageLockFile lockFile)
    {
        if (manifest.Dependencies is null || manifest.Dependencies.Count == 0)
            return true;

        // The lock file is stale if any manifest dependency is missing or has a changed version specifier
        foreach (var (name, versionSpecifier) in manifest.Dependencies)
        {
            if (!lockFile.Dependencies.TryGetValue(name, out var lockedVersion))
                return false;

            // If the version specifier changed (e.g., "4.0.x" → "5.0.x"), the lock file is stale
            if (!string.Equals(versionSpecifier, lockedVersion, StringComparison.Ordinal)
                && !IsVersionSatisfiedBy(versionSpecifier, lockedVersion))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether a locked exact version could plausibly satisfy the given version specifier.
    /// This is a heuristic: exact versions satisfy themselves, and wildcard/range specifiers
    /// are checked via <see cref="FhirSemVer.Satisfies"/>.
    /// </summary>
    private static bool IsVersionSatisfiedBy(string specifier, string lockedVersion)
    {
        // If the specifier IS the locked version, it's satisfied
        if (string.Equals(specifier, lockedVersion, StringComparison.Ordinal))
            return true;

        // Try to parse both and check satisfaction
        if (FhirSemVer.TryParse(lockedVersion, out var lockedSemVer)
            && FhirSemVer.TryParse(specifier, out var specifierSemVer))
        {
            // If the specifier is a wildcard or range, check if the locked version satisfies it
            if (specifierSemVer.IsWildcard)
                return lockedSemVer.Satisfies(specifier);
        }

        // For "latest", "current", range expressions (^, ~), assume the lock file may be stale
        return false;
    }

    /// <summary>
    /// Reads a lock file from disk.
    /// </summary>
    private static async Task<PackageLockFile> ReadLockFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PackageLockFile>(json, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize lock file at '{path}'.");
    }

    /// <summary>
    /// Writes a lock file to disk from a resolved closure.
    /// </summary>
    private static async Task WriteLockFileAsync(
        string path,
        PackageClosure closure,
        CancellationToken cancellationToken)
    {
        var lockFile = new PackageLockFile
        {
            Updated = closure.Timestamp,
            Dependencies = closure.Resolved.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Version ?? string.Empty),
            Missing = closure.Missing.Count > 0 ? closure.Missing : null
        };

        var json = JsonSerializer.Serialize(lockFile, s_jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts a <see cref="PackageReference"/> from a tarball by reading its manifest.
    /// </summary>
    private async Task<PackageReference> ExtractReferenceFromTarballAsync(
        string tarballPath,
        CancellationToken cancellationToken)
    {
        // Create a temporary directory (prefer system temp, fall back to cache)
        var tempDir = TempDirectory.Create("fhirpkg", _cache.CacheDirectory);
        try
        {
            await using var stream = File.OpenRead(tarballPath);
            await TarballExtractor.ExtractAsync(stream, tempDir, cancellationToken).ConfigureAwait(false);

            var manifestPath = Path.Combine(tempDir, "package", ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                // Try without the package/ subdirectory
                manifestPath = Path.Combine(tempDir, ManifestFileName);
            }

            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException(
                    $"No {ManifestFileName} found in tarball '{tarballPath}'.");
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(json, s_jsonOptions)
                ?? throw new InvalidOperationException($"Failed to parse manifest in '{tarballPath}'.");

            return new PackageReference(manifest.Name, manifest.Version);
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up temp directory '{TempDir}'", tempDir); }
        }
    }

    /// <summary>
    /// Finds the registry client that matches the given endpoint, or falls back to creating a new one.
    /// </summary>
    private IRegistryClient FindRegistryClient(RegistryEndpoint registry)
    {
        // If the composite client matches, use it (the RedundantRegistryClient will route internally)
        if (string.Equals(_registryClient.Endpoint.Url, registry.Url, StringComparison.OrdinalIgnoreCase))
            return _registryClient;

        // For publish operations, we may need a client for a registry not in the default chain.
        // Create an ephemeral client. The caller owns the HTTP client lifetime externally.
        if (_ownedHttpClient is not null)
        {
            return RegistryClientFactory.CreateClientForEndpoint(registry, _ownedHttpClient, _loggerFactory);
        }

        // If we're in DI mode, fall back to the registered client
        return _registryClient;
    }

    /// <summary>
    /// Reports progress to the caller if a progress handler is configured.
    /// </summary>
    private static void ReportProgress(
        IProgress<PackageProgress>? progress,
        string packageId,
        PackageProgressPhase phase,
        double? percentComplete = null,
        long? bytesDownloaded = null,
        long? totalBytes = null)
    {
        progress?.Report(new PackageProgress
        {
            PackageId = packageId,
            Phase = phase,
            PercentComplete = percentComplete,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes
        });
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    #endregion
}
