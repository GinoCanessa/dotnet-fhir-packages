// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
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
    private readonly PackageInstallLimits _managerInstallLimits;
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
        : this(options, loggerFactory, ResolveManagerLimits(options))
    {
    }

    private FhirPackageManager(
        FhirPackageManagerOptions options,
        ILoggerFactory? loggerFactory,
        PackageInstallLimits managerInstallLimits)
    {
        _options = options;
        _managerInstallLimits = managerInstallLimits;
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        _loggerFactory = factory;
        _logger = factory.CreateLogger<FhirPackageManager>();

        // Build cache
        _cache = new DiskPackageCache(options.CachePath, null, null, managerInstallLimits);

        // Build HTTP client with configured timeout and redirect policy
        HttpClientHandler handler = new HttpClientHandler
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
        : this(
            cache,
            registryClient,
            versionResolver,
            dependencyResolver,
            packageIndexer,
            options,
            logger,
            memoryCache,
            ResolveManagerLimits(options))
    {
    }

    internal FhirPackageManager(
        IPackageCache cache,
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IDependencyResolver dependencyResolver,
        IPackageIndexer packageIndexer,
        FhirPackageManagerOptions options,
        ILogger<FhirPackageManager> logger,
        MemoryResourceCache? memoryCache,
        PackageInstallLimits managerInstallLimits)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(dependencyResolver);
        ArgumentNullException.ThrowIfNull(packageIndexer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(managerInstallLimits);
        managerInstallLimits.Validate();

        _cache = cache;
        _registryClient = registryClient;
        _versionResolver = versionResolver;
        _dependencyResolver = dependencyResolver;
        _packageIndexer = packageIndexer;
        _options = options;
        _managerInstallLimits = managerInstallLimits;
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

        ResolvedPackageInstallPolicy policy = ResolvedPackageInstallPolicy.Resolve(
            _options,
            _managerInstallLimits,
            options);

        return await InstallDirectiveAsync(directive, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
        IEnumerable<string> directives,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directives);
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<string> directiveList = directives.ToList();
        ResolvedPackageInstallPolicy policy = ResolvedPackageInstallPolicy.Resolve(
            _options,
            _managerInstallLimits,
            options);

        if (directiveList.Count == 0)
            return [];

        _logger.LogInformation("InstallManyAsync starting for {Count} directives.", directiveList.Count);
        return await InstallManyResolvedAsync(directiveList, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageClosure> RestoreAsync(
        string projectPath,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        RestoreOptions effectiveOptions = options ?? new RestoreOptions();
        ResolvedPackageInstallPolicy installPolicy = ResolvedPackageInstallPolicy.Resolve(
            _options,
            _managerInstallLimits,
            effectiveOptions);

        _logger.LogInformation("RestoreAsync starting for project at '{ProjectPath}'.", projectPath);

        // Step 1: Read project manifest (package.json)
        string manifestPath = Path.Combine(projectPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Package manifest not found at '{manifestPath}'.", manifestPath);
        }

        string manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest at '{manifestPath}'.");

        _logger.LogDebug("Read manifest: {Name}@{Version} with {DepCount} dependencies.",
            manifest.Name, manifest.Version, manifest.Dependencies?.Count ?? 0);

        // Step 2: Check for existing lock file
        string lockFilePath = Path.Combine(projectPath, LockFileName);
        PackageClosure closure;

        if (File.Exists(lockFilePath) && !installPolicy.OverwriteExisting)
        {
            _logger.LogDebug("Found existing lock file at '{LockFilePath}'.", lockFilePath);

            PackageLockFile lockFile = await ReadLockFileAsync(lockFilePath, cancellationToken).ConfigureAwait(false);

            // Verify lock file is not stale (all manifest dependencies are in the lock file)
            if (IsLockFileCurrent(manifest, lockFile))
            {
                _logger.LogInformation("Lock file is current. Restoring from lock file.");
                closure = await _dependencyResolver.RestoreFromLockFileAsync(lockFile, cancellationToken)
                    .ConfigureAwait(false);

                // Install all resolved packages from the lock file
                await InstallClosureAsync(closure, installPolicy, cancellationToken).ConfigureAwait(false);
                return closure;
            }

            _logger.LogInformation("Lock file is stale. Performing full dependency resolution.");
        }

        // Step 3: Resolve full dependency tree
        DependencyResolveOptions resolveOptions = new DependencyResolveOptions
        {
            ConflictStrategy = effectiveOptions.ConflictStrategy,
            MaxDepth = effectiveOptions.MaxDepth,
            AllowPreRelease = installPolicy.AllowPreRelease,
            PreferredFhirRelease = installPolicy.PreferredFhirRelease
        };

        closure = await _dependencyResolver.ResolveAsync(manifest, resolveOptions, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Dependency resolution complete: {Resolved} resolved, {Missing} missing.",
            closure.Resolved.Count, closure.Missing.Count);

        // Step 4: Install all resolved packages
        await InstallClosureAsync(closure, installPolicy, cancellationToken).ConfigureAwait(false);

        // Step 5: Write lock file if configured
        if (effectiveOptions.WriteLockFile)
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

        PackageReference reference = PackageReference.Parse(directive);
        _logger.LogInformation("Removing package {Name}#{Version} from cache.", reference.Name, reference.Version);

        bool removed = await _cache.RemoveAsync(reference, cancellationToken).ConfigureAwait(false);

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
        int count = await _cache.ClearAsync(cancellationToken).ConfigureAwait(false);
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

        PackageDirective parsedDirective = DirectiveParser.Parse(directive);
        ResolvedDirective? resolved = await _registryClient.ResolveAsync(parsedDirective, cancellationToken: cancellationToken)
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
        PackageReference reference = await ExtractReferenceFromTarballAsync(tarballPath, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Publishing as {Name}#{Version}.", reference.Name, reference.Version);

        // Find the appropriate registry client for the target endpoint
        IRegistryClient targetClient = FindRegistryClient(registry);

        // Open a fresh stream for the actual publish
        await using FileStream tarballStream = File.OpenRead(tarballPath);
        PublishResult result = await targetClient.PublishAsync(reference, tarballStream, cancellationToken).ConfigureAwait(false);

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

    private static PackageInstallLimits ResolveManagerLimits(FhirPackageManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.CorruptCacheBehavior))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "CorruptCacheBehavior is not a supported value.");
        }

        return PackageInstallLimits.ResolveManager(options.InstallLimits);
    }

    private async Task<PackageRecord?> InstallDirectiveAsync(
        string directive,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            return await InstallDirectiveCoreAsync(directive, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageInstallException)
        {
            ReportProgress(policy.Progress, directive, PackageProgressPhase.Failed);
            throw;
        }
    }

    private async Task<PackageRecord?> InstallDirectiveCoreAsync(
        string directive,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Installing directive '{Directive}' through the unified install contract.", directive);

        PackageDirective parsedDirective = DirectiveParser.Parse(directive);
        PackageReference requestedReference = PackageFixups.Apply(parsedDirective.ToReference());
        if (!requestedReference.Equals(parsedDirective.ToReference()))
        {
            parsedDirective = DirectiveParser.Parse(requestedReference.FhirDirective);
        }

        PackageCacheKey.ValidatePackageName(requestedReference);
        PackageInstallFreshness freshness = GetFreshness(parsedDirective.VersionType);
        PackageCacheKey? requestedCacheKey = parsedDirective.VersionType
            is VersionType.Exact
                or VersionType.CiBuild
                or VersionType.CiBuildBranch
                or VersionType.LocalBuild
            ? PackageCacheKey.Create(requestedReference)
            : null;

        if (freshness == PackageInstallFreshness.LocalAuthoritative)
        {
            PackageCacheKey localKey = requestedCacheKey!;
            bool isInstalled = await IsInstalledAsync(localKey, cancellationToken).ConfigureAwait(false);
            if (isInstalled)
            {
                _logger.LogInformation(
                    "Local package alias {Name}#{Version} is already cached.",
                    requestedReference.Name,
                    requestedReference.Version);
                return await GetCachedPackageAsync(localKey, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Local package alias '{Directive}' is not present and cannot be resolved from registries.",
                directive);
            ReportProgress(policy.Progress, requestedReference.Name, PackageProgressPhase.Failed);
            return null;
        }

        if (parsedDirective.VersionType == VersionType.Exact && !policy.OverwriteExisting)
        {
            PackageCacheKey requestedKey = requestedCacheKey!;
            if (await IsInstalledAsync(requestedKey, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Package {Name}#{Version} is already cached.",
                    requestedReference.Name,
                    requestedReference.Version);
                return await GetCachedPackageAsync(requestedKey, cancellationToken).ConfigureAwait(false);
            }
        }

        ReportProgress(policy.Progress, requestedReference.Name, PackageProgressPhase.Resolving);
        ResolvedDirective? resolved = await ResolveDirectiveAsync(
                directive,
                parsedDirective,
                policy,
                cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            _logger.LogWarning("Could not resolve directive '{Directive}' from any registry.", directive);
            ReportProgress(policy.Progress, requestedReference.Name, PackageProgressPhase.Failed);
            return null;
        }

        bool preservesAlias = parsedDirective.VersionType
            is VersionType.CiBuild or VersionType.CiBuildBranch;
        PackageReference cacheReference = preservesAlias
            ? requestedReference
            : resolved.Reference;
        PackageCacheKey cacheKey = preservesAlias
            ? requestedCacheKey!
            : PackageCacheKey.Create(cacheReference);

        PackageIdentityExpectation identityExpectation = new PackageIdentityExpectation
        {
            Kind = preservesAlias
                ? PackageIdentityExpectationKind.Alias
                : PackageIdentityExpectationKind.Exact,
            Reference = preservesAlias ? requestedReference : resolved.Reference
        };

        PackageInstallRequest request = new PackageInstallRequest
        {
            Directive = directive,
            CacheKey = cacheKey,
            Source = PackageInstallSource.FromDirective(resolved),
            IdentityExpectation = identityExpectation,
            Freshness = freshness,
            Policy = policy
        };

        _logger.LogInformation(
            "Resolved {Name} to version {Version} from {Registry}; cache key is {CacheKey}.",
            resolved.Reference.Name,
            resolved.Reference.Version,
            resolved.SourceRegistry?.Url ?? "unknown",
            cacheKey.MetadataKey);

        return await InstallResolvedAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedDirective?> ResolveDirectiveAsync(
        string directive,
        PackageDirective parsedDirective,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _registryClient.ResolveAsync(
                    parsedDirective,
                    new VersionResolveOptions
                    {
                        AllowPreRelease = policy.AllowPreRelease,
                        FhirRelease = policy.PreferredFhirRelease
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.ResolutionFailed,
                PackageInstallStage.Resolution,
                $"Package directive '{directive}' could not be resolved before the source timed out.",
                directive,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.ResolutionFailed,
                PackageInstallStage.Resolution,
                $"Package directive '{directive}' could not be resolved from its source.",
                directive,
                exception);
        }
        catch (IOException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.ResolutionFailed,
                PackageInstallStage.Resolution,
                $"Package directive '{directive}' could not be resolved from its source.",
                directive,
                exception);
        }
    }

    private async Task<PackageRecord> InstallResolvedAsync(
        PackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        PackageReference cacheReference = request.CacheKey.DisplayReference;
        ResolvedDirective resolved = request.Source.ResolvedDirective;
        bool isInstalled = await IsInstalledAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
        PackageRecord? cachedRecord = isInstalled
            ? await GetCachedPackageAsync(request.CacheKey, cancellationToken).ConfigureAwait(false)
            : null;
        CacheMetadataEntry? existingMetadata = null;

        if (request.Freshness == PackageInstallFreshness.Immutable
            && isInstalled
            && !request.Policy.OverwriteExisting
            && cachedRecord is not null)
        {
            ReportProgress(
                request.Policy.Progress,
                cacheReference.Name,
                PackageProgressPhase.Complete);
            return cachedRecord;
        }

        DateTimeOffset? sourcePublicationDate = NormalizePublicationDate(resolved.PublicationDate);
        if (request.Freshness == PackageInstallFreshness.RefreshableAlias && isInstalled)
        {
            existingMetadata = await GetCacheMetadataEntryAsync(
                    request.CacheKey,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!request.Policy.OverwriteExisting
                && cachedRecord is not null
                && sourcePublicationDate.HasValue
                && existingMetadata?.SourcePublicationDate is DateTimeOffset cachedPublicationDate
                && cachedPublicationDate >= sourcePublicationDate.Value)
            {
                _logger.LogInformation(
                    "Mutable alias {CacheKey} is current according to source publication metadata.",
                    request.CacheKey.MetadataKey);
                ReportProgress(
                    request.Policy.Progress,
                    cacheReference.Name,
                    PackageProgressPhase.Complete);
                return cachedRecord;
            }
        }

        ReportProgress(
            request.Policy.Progress,
            cacheReference.Name,
            PackageProgressPhase.Downloading);

        await using PackageDownloadResult downloadResult = await DownloadAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        ResolvedDirective resolvedDirective = request.Source.ResolvedDirective;
        await using PackageContentAcquisition acquiredContent =
            await PackageContentAcquirer.AcquireAsync(
                    downloadResult.Content,
                    GetAcquisitionCacheRoot(),
                    request.Policy.Limits,
                    downloadResult.ContentLength,
                    resolvedDirective.Sha256Sum,
                    resolvedDirective.ShaSum,
                    request.Policy.VerifyChecksums,
                    request.Directive,
                    cancellationToken)
            .ConfigureAwait(false);

        if (request.Freshness == PackageInstallFreshness.RefreshableAlias
            && !request.Policy.OverwriteExisting
            && cachedRecord is not null
            && existingMetadata?.ArchiveSha256 is string cachedArchiveSha256
            && string.Equals(
                cachedArchiveSha256,
                acquiredContent.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAliasMetadataAsync(
                    request.CacheKey,
                    existingMetadata,
                    sourcePublicationDate,
                    acquiredContent.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Mutable alias {CacheKey} has unchanged archive content.",
                request.CacheKey.MetadataKey);
            ReportProgress(
                request.Policy.Progress,
                cacheReference.Name,
                PackageProgressPhase.Complete);
            return cachedRecord;
        }

        ReportProgress(
            request.Policy.Progress,
            cacheReference.Name,
            PackageProgressPhase.Extracting);

        InstallCacheOptions installCacheOptions = new InstallCacheOptions
        {
            OverwriteExisting = request.Policy.OverwriteExisting
                || (request.Freshness == PackageInstallFreshness.RefreshableAlias && isInstalled),
            VerifyChecksum = request.Policy.VerifyChecksums,
            Limits = request.Policy.Limits,
            ReportedContentLength = downloadResult.ContentLength,
            ExpectedSha256Sum = resolvedDirective.Sha256Sum,
            ExpectedShaSum = resolvedDirective.ShaSum,
            SourcePublicationDate = sourcePublicationDate,
            ArchiveSha256 = acquiredContent.Sha256,
            AcquiredContent = acquiredContent,
            IdentityExpectation = request.IdentityExpectation
        };

        PackageRecord record;
        try
        {
            await using FileStream archiveStream = acquiredContent.OpenArchiveRead();
            record = await _cache.InstallAsync(
                    cacheReference,
                    archiveStream,
                    installCacheOptions,
                    cancellationToken)
                .ConfigureAwait(false);
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
                $"Package '{request.Directive}' contains an invalid archive.",
                request.Directive,
                exception);
        }
        catch (JsonException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidArchive,
                PackageInstallStage.ArchiveValidation,
                $"Package '{request.Directive}' contains an invalid manifest.",
                request.Directive,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CommitFailure(request.Directive, exception);
        }
        catch (IOException exception)
        {
            throw CommitFailure(request.Directive, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw CommitFailure(request.Directive, exception);
        }

        _logger.LogInformation(
            "Installed {Name}#{Version} to {Path}.",
            record.Reference.Name,
            record.Reference.Version,
            record.DirectoryPath);

        if (request.Policy.IncludeDependencies)
        {
            await InstallDependenciesAsync(record, request.Policy, cancellationToken)
                .ConfigureAwait(false);
        }

        ReportProgress(
            request.Policy.Progress,
            cacheReference.Name,
            PackageProgressPhase.Complete);
        return record;
    }

    private async Task<IReadOnlyList<PackageInstallResult>> InstallManyResolvedAsync(
        IReadOnlyList<string> directives,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        PackageInstallResult[] results = new PackageInstallResult[directives.Count];
        using SemaphoreSlim semaphore = new SemaphoreSlim(_options.MaxParallelRegistryQueries);

        IEnumerable<Task> tasks = directives.Select(async (directive, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PackageRecord? record = await InstallDirectiveAsync(
                        directive,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
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
                        ErrorMessage = $"Package '{directive}' could not be resolved.",
                        ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                        ErrorStage = PackageInstallStage.Resolution
                    };
            }
            catch (PackageInstallException exception)
            {
                _logger.LogError(exception, "Failed to install '{Directive}'.", directive);
                results[index] = new PackageInstallResult
                {
                    Directive = directive,
                    Status = PackageInstallStatus.Failed,
                    ErrorMessage = exception.Message,
                    ErrorCode = exception.ErrorCode,
                    ErrorStage = exception.Stage
                };
            }
            catch (ArgumentException exception)
            {
                _logger.LogError(exception, "Invalid package directive '{Directive}'.", directive);
                results[index] = new PackageInstallResult
                {
                    Directive = directive,
                    Status = PackageInstallStatus.Failed,
                    ErrorMessage = exception.Message,
                    ErrorCode = PackageInstallErrorCode.InvalidPackageIdentity,
                    ErrorStage = PackageInstallStage.IdentityValidation
                };
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        int installed = results.Count(result => result.Status == PackageInstallStatus.Installed);
        _logger.LogInformation(
            "InstallManyAsync completed: {Installed}/{Total} packages installed.",
            installed,
            directives.Count);
        return results;
    }

    private async Task<PackageDownloadResult> DownloadAsync(
        PackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            PackageDownloadResult? result = await _registryClient.DownloadAsync(
                    request.Source.ResolvedDirective,
                    cancellationToken)
                .ConfigureAwait(false);

            return result
                ?? throw new PackageInstallException(
                    PackageInstallErrorCode.DownloadFailed,
                    PackageInstallStage.Acquisition,
                    $"Package '{request.Directive}' could not be downloaded.",
                    request.Directive);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                $"Package '{request.Directive}' download timed out.",
                request.Directive,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                $"Package '{request.Directive}' could not be downloaded.",
                request.Directive,
                exception);
        }
        catch (IOException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                $"Package '{request.Directive}' could not be downloaded.",
                request.Directive,
                exception);
        }
    }

    private async Task<bool> IsInstalledAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.IsInstalledAsync(cacheKey.DisplayReference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
    }

    private async Task<PackageRecord?> GetCachedPackageAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetPackageAsync(cacheKey.DisplayReference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
    }

    private async Task<CacheMetadataEntry?> GetCacheMetadataEntryAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            CacheMetadata? metadata = await _cache.GetMetadataAsync(cancellationToken)
                .ConfigureAwait(false);
            if (metadata is null)
                return null;

            metadata.Packages.TryGetValue(
                cacheKey.MetadataKey,
                out CacheMetadataEntry? entry);
            return entry;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            throw CacheInspectionFailure(cacheKey, exception);
        }
    }

    private async Task RefreshAliasMetadataAsync(
        PackageCacheKey cacheKey,
        CacheMetadataEntry existingMetadata,
        DateTimeOffset? sourcePublicationDate,
        string archiveSha256,
        CancellationToken cancellationToken)
    {
        CacheMetadataEntry refreshedMetadata = existingMetadata with
        {
            SourcePublicationDate = sourcePublicationDate
                ?? existingMetadata.SourcePublicationDate,
            ArchiveSha256 = archiveSha256
        };

        try
        {
            await _cache.UpdateMetadataAsync(
                    cacheKey.DisplayReference,
                    refreshedMetadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CommitFailure(cacheKey.DisplayReference.FhirDirective, exception);
        }
        catch (IOException exception)
        {
            throw CommitFailure(cacheKey.DisplayReference.FhirDirective, exception);
        }
    }

    private static PackageInstallFreshness GetFreshness(VersionType versionType) =>
        versionType switch
        {
            VersionType.CiBuild or VersionType.CiBuildBranch =>
                PackageInstallFreshness.RefreshableAlias,
            VersionType.LocalBuild => PackageInstallFreshness.LocalAuthoritative,
            _ => PackageInstallFreshness.Immutable
        };

    private string GetAcquisitionCacheRoot()
    {
        if (!string.IsNullOrWhiteSpace(_cache.CacheDirectory))
            return _cache.CacheDirectory;

        if (!string.IsNullOrWhiteSpace(_options.CachePath))
            return _options.CachePath;

        string? configuredCacheRoot =
            Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER");
        return !string.IsNullOrWhiteSpace(configuredCacheRoot)
            ? configuredCacheRoot
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fhir",
                "packages");
    }

    private static DateTimeOffset? NormalizePublicationDate(DateTime? publicationDate)
    {
        if (!publicationDate.HasValue)
            return null;

        DateTime value = publicationDate.Value;
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(value);
    }

    private static PackageInstallException CacheInspectionFailure(
        PackageCacheKey cacheKey,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.CorruptCache,
            PackageInstallStage.CacheInspection,
            $"The cache entry for '{cacheKey.MetadataKey}' could not be inspected.",
            cacheKey.DisplayReference.FhirDirective,
            exception);

    private static PackageInstallException CommitFailure(
        string directive,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.CommitFailed,
            PackageInstallStage.Commit,
            $"Package '{directive}' could not be committed to the cache.",
            directive,
            exception);

    /// <summary>
    /// Recursively installs dependencies of a cached package.
    /// </summary>
    private async Task InstallDependenciesAsync(
        PackageRecord record,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        PackageManifest manifest = record.Manifest;
        if (manifest.Dependencies is null || manifest.Dependencies.Count == 0)
        {
            _logger.LogDebug("No dependencies for {Name}#{Version}.", record.Reference.Name, record.Reference.Version);
            return;
        }

        _logger.LogDebug("Installing {Count} dependencies for {Name}#{Version}.",
            manifest.Dependencies.Count, record.Reference.Name, record.Reference.Version);

        foreach ((string? depName, string? depVersion) in manifest.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string depDirective = string.IsNullOrEmpty(depVersion) ? depName : $"{depName}#{depVersion}";
            _logger.LogDebug("Installing dependency: {Directive}.", depDirective);

            try
            {
                await InstallDirectiveAsync(depDirective, policy, cancellationToken).ConfigureAwait(false);
            }
            catch (PackageInstallException exception)
            {
                _logger.LogWarning(exception, "Failed to install dependency '{Directive}' for {Name}#{Version}.",
                    depDirective, record.Reference.Name, record.Reference.Version);
            }
            catch (ArgumentException exception)
            {
                _logger.LogWarning(exception, "Failed to install dependency '{Directive}' for {Name}#{Version}.",
                    depDirective, record.Reference.Name, record.Reference.Version);
            }
        }
    }

    /// <summary>
    /// Installs all packages referenced in a closure that are not already cached.
    /// </summary>
    private async Task InstallClosureAsync(
        PackageClosure closure,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        List<string> directives = closure.Resolved.Values
            .Where(r => r.HasVersion)
            .Select(r => r.FhirDirective)
            .ToList();

        if (directives.Count == 0)
            return;

        _logger.LogDebug("Installing {Count} packages from closure.", directives.Count);

        // The closure already contains all transitive dependencies.
        IReadOnlyList<PackageInstallResult> results = await InstallManyResolvedAsync(
                directives,
                policy.WithoutDependencies(),
                cancellationToken)
            .ConfigureAwait(false);

        PackageInstallResult? failure = results.FirstOrDefault(
            result => result.Status
                is PackageInstallStatus.Failed or PackageInstallStatus.NotFound);
        if (failure is null)
            return;

        PackageInstallErrorCode errorCode = failure.ErrorCode
            ?? (failure.Status == PackageInstallStatus.NotFound
                ? PackageInstallErrorCode.ResolutionFailed
                : PackageInstallErrorCode.CommitFailed);
        PackageInstallStage stage = failure.ErrorStage
            ?? (failure.Status == PackageInstallStatus.NotFound
                ? PackageInstallStage.Resolution
                : PackageInstallStage.Commit);

        throw new PackageInstallException(
            errorCode,
            stage,
            failure.ErrorMessage
                ?? $"Package '{failure.Directive}' could not be restored.",
            failure.Directive);
    }

    /// <summary>
    /// Determines whether an existing lock file covers all dependencies in the manifest.
    /// </summary>
    private static bool IsLockFileCurrent(PackageManifest manifest, PackageLockFile lockFile)
    {
        if (manifest.Dependencies is null || manifest.Dependencies.Count == 0)
            return true;

        // The lock file is stale if any manifest dependency is missing or has a changed version specifier
        foreach ((string? name, string? versionSpecifier) in manifest.Dependencies)
        {
            if (!lockFile.Dependencies.TryGetValue(name, out string? lockedVersion))
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
        if (FhirSemVer.TryParse(lockedVersion, out FhirSemVer? lockedSemVer)
            && FhirSemVer.TryParse(specifier, out FhirSemVer? specifierSemVer))
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
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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
        PackageLockFile lockFile = new PackageLockFile
        {
            Updated = closure.Timestamp,
            Dependencies = closure.Resolved.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Version ?? string.Empty),
            Missing = closure.Missing.Count > 0 ? closure.Missing : null
        };

        string json = JsonSerializer.Serialize(lockFile, s_jsonOptions);
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
        string tempDir = TempDirectory.Create("fhirpkg", _cache.CacheDirectory);
        try
        {
            await using FileStream stream = File.OpenRead(tarballPath);
            await TarballExtractor.ExtractAsync(stream, tempDir, cancellationToken).ConfigureAwait(false);

            string manifestPath = Path.Combine(tempDir, "package", ManifestFileName);
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

            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(json, s_jsonOptions)
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
