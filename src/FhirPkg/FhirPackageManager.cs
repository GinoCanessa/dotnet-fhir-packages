// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class FhirPackageManager :
    IHardenedFhirPackageManager,
    IFhirPackageResourceManager,
    IDisposable
{
    private readonly IPackageCache _cache;
    private readonly IRegistryClient _registryClient;
    private readonly IVersionResolver _versionResolver;
    private readonly IDependencyResolver _dependencyResolver;
    private readonly IPackageIndexer _packageIndexer;
    private readonly IManagedPackageIndexer? _managedPackageBuilder;
    private readonly IManagedPackageIndexer _managedPackageIndexer;
    private readonly MemoryResourceCache? _memoryCache;
    private readonly IDisposable? _cacheMutationSubscription;
    private readonly object _resourceStateLock = new();
    private readonly Dictionary<
        string,
        RegisteredPackageState> _registeredPackages =
        new(StringComparer.Ordinal);
    private readonly FhirPackageManagerOptions _options;
    private readonly PackageInstallLimits _managerInstallLimits;
    private readonly PackageFixupPolicy _fixupPolicy;
    private readonly ILogger<FhirPackageManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _httpClient;
    private readonly HttpClient? _ownedHttpClient;
    private readonly RegistryHttpTransport _registryTransport;
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
        : this(
            FhirPackageManagerConfiguration.Create(options),
            loggerFactory,
            NullPackageCacheContentionObserver.Instance)
    {
    }

    private FhirPackageManager(
        FhirPackageManagerConfiguration configuration,
        ILoggerFactory? loggerFactory,
        IPackageCacheContentionObserver contentionObserver)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentionObserver);
        FhirPackageManagerOptions options = configuration.Options;
        PackageInstallLimits managerInstallLimits = configuration.InstallLimits;
        _options = options;
        _managerInstallLimits = managerInstallLimits;
        _fixupPolicy = configuration.FixupPolicy;
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        _loggerFactory = factory;
        _logger = factory.CreateLogger<FhirPackageManager>();

        // Build cache
        _cache = new DiskPackageCache(
            options.CachePath,
            logger: null,
            timeProvider: null,
            installLimits: managerInstallLimits,
            contentionObserver: contentionObserver);

        // Build HTTP client with configured timeout and redirect policy
        HttpClientHandler handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = options.MaxRedirects
        };
        _ownedHttpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _httpClient = _ownedHttpClient;
        _registryTransport = RegistryHttpTransport.CreateRedirectControlled(
            _ownedHttpClient,
            options.HttpTimeout,
            options.MaxRedirects);

        // Build registry client chain
        _registryClient = RegistryClientFactory.BuildRegistryClient(
            options,
            _registryTransport,
            factory);

        // Build resolvers
        _versionResolver = new VersionResolver(_registryClient, _logger);
        _dependencyResolver = new DependencyResolver(
            _registryClient,
            _versionResolver,
            _cache,
            _logger,
            _fixupPolicy);

        // Build indexer
        _packageIndexer = new PackageIndexer(factory.CreateLogger<PackageIndexer>());
        _managedPackageBuilder =
            _packageIndexer as IManagedPackageIndexer;
        _managedPackageIndexer =
            _managedPackageBuilder
            ?? new PackageIndexer(
                factory.CreateLogger<PackageIndexer>());

        // Build optional memory cache
        if (options.ResourceCacheSize > 0)
        {
            _memoryCache = new MemoryResourceCache(options.ResourceCacheSize, options.ResourceCacheSafeMode);
        }

        _cacheMutationSubscription =
            _cache is IPackageCacheMutationPublisher mutationPublisher
                ? mutationPublisher.Subscribe(
                    InvalidatePackageResources,
                    ClearResourceState)
                : null;
    }

    internal static FhirPackageManager CreateWithContentionObserver(
        FhirPackageManagerOptions options,
        IPackageCacheContentionObserver contentionObserver,
        ILoggerFactory? loggerFactory = null) =>
        new(
            FhirPackageManagerConfiguration.Create(options),
            loggerFactory,
            contentionObserver);

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
            FhirPackageManagerConfiguration.Create(options),
            logger,
            memoryCache)
    {
    }

    private FhirPackageManager(
        IPackageCache cache,
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IDependencyResolver dependencyResolver,
        IPackageIndexer packageIndexer,
        FhirPackageManagerConfiguration configuration,
        ILogger<FhirPackageManager> logger,
        MemoryResourceCache? memoryCache)
        : this(
            cache,
            registryClient,
            versionResolver,
            dependencyResolver,
            packageIndexer,
            configuration,
            logger,
            memoryCache,
            CreateDirectHttpClient(configuration.Options),
            ownsHttpClient: true,
            loggerFactory: null,
            redirectsControlled: true)
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
        : this(
            cache,
            registryClient,
            versionResolver,
            dependencyResolver,
            packageIndexer,
            FhirPackageManagerConfiguration.Create(options, managerInstallLimits),
            logger,
            memoryCache,
            CreateDirectHttpClient(options),
            ownsHttpClient: true,
            loggerFactory: null,
            redirectsControlled: true)
    {
    }

    internal static FhirPackageManager CreateWithHttpClient(
        IPackageCache cache,
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IDependencyResolver dependencyResolver,
        IPackageIndexer packageIndexer,
        FhirPackageManagerOptions options,
        ILogger<FhirPackageManager> logger,
        MemoryResourceCache? memoryCache,
        PackageInstallLimits managerInstallLimits,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        bool redirectsControlled = false)
    {
        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.Create(options, managerInstallLimits);

        return new FhirPackageManager(
            cache,
            registryClient,
            versionResolver,
            dependencyResolver,
            packageIndexer,
            configuration,
            logger,
            memoryCache,
            httpClient,
            ownsHttpClient: false,
            loggerFactory,
            redirectsControlled);
    }

    private FhirPackageManager(
        IPackageCache cache,
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IDependencyResolver dependencyResolver,
        IPackageIndexer packageIndexer,
        FhirPackageManagerConfiguration configuration,
        ILogger<FhirPackageManager> logger,
        MemoryResourceCache? memoryCache,
        HttpClient httpClient,
        bool ownsHttpClient,
        ILoggerFactory? loggerFactory,
        bool redirectsControlled)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(dependencyResolver);
        ArgumentNullException.ThrowIfNull(packageIndexer);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClient);

        FhirPackageManagerOptions options = configuration.Options;
        PackageInstallLimits managerInstallLimits = configuration.InstallLimits;

        _cache = cache;
        _registryClient = registryClient;
        _versionResolver = versionResolver;
        _dependencyResolver = dependencyResolver;
        _packageIndexer = packageIndexer;
        _managedPackageBuilder =
            packageIndexer as IManagedPackageIndexer;
        _managedPackageIndexer =
            _managedPackageBuilder
            ?? new PackageIndexer(logger);
        _options = options;
        _managerInstallLimits = managerInstallLimits;
        _fixupPolicy = configuration.FixupPolicy;
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _memoryCache = memoryCache;
        _httpClient = httpClient;
        _ownedHttpClient = ownsHttpClient ? httpClient : null;
        _registryTransport = redirectsControlled
            ? RegistryHttpTransport.CreateRedirectControlled(
                httpClient,
                options.HttpTimeout,
                options.MaxRedirects)
            : RegistryHttpTransport.CreateUnverified(httpClient);
        _cacheMutationSubscription =
            cache is IPackageCacheMutationPublisher mutationPublisher
                ? mutationPublisher.Subscribe(
                    InvalidatePackageResources,
                    ClearResourceState)
                : null;
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
        _ = RequireHardenedCache();

        return await InstallDirectiveAsync(directive, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                _options,
                _managerInstallLimits,
                options);
        IHardenedPackageCache hardenedCache = RequireHardenedCache();
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                expectedReference,
                expectedReference.FhirDirective);
        ValidatePackageUri(packageUri);

        return await ExecuteUriSourceAsync(
                packageUri,
                expectedReference.Name,
                policy.Progress,
                async (Stream stream, long? contentLength) =>
                    await InstallExpectedSourceAsync(
                            hardenedCache,
                            expectation,
                            stream,
                            contentLength,
                            options,
                            policy,
                            reportFailure: false,
                            cancellationToken)
                        .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                _options,
                _managerInstallLimits,
                options);
        IHardenedPackageCache hardenedCache = RequireHardenedCache();
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                expectedReference,
                expectedReference.FhirDirective);
        return await InstallExpectedSourceAsync(
                hardenedCache,
                expectation,
                packageStream,
                reportedContentLength: null,
                options,
                policy,
                reportFailure: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageRecord> ImportAsync(
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                _options,
                _managerInstallLimits,
                options);
        IHardenedPackageCache hardenedCache = RequireHardenedCache();
        ValidatePackageUri(packageUri);
        return await ExecuteUriSourceAsync(
                packageUri,
                "package import",
                policy.Progress,
                async (Stream stream, long? contentLength) =>
                    await ImportSourceAsync(
                            hardenedCache,
                            stream,
                            contentLength,
                            options,
                            policy,
                            reportFailure: false,
                            cancellationToken)
                        .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageRecord> ImportAsync(
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                _options,
                _managerInstallLimits,
                options);
        IHardenedPackageCache hardenedCache = RequireHardenedCache();
        return await ImportSourceAsync(
                hardenedCache,
                packageStream,
                reportedContentLength: null,
                options,
                policy,
                reportFailure: true,
                cancellationToken)
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
        _ = RequireHardenedCache();

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
        _ = RequireHardenedCache();

        string fullProjectPath = Path.GetFullPath(projectPath);
        _logger.LogInformation(
            "RestoreAsync starting for project at '{ProjectPath}'.",
            fullProjectPath);

        // Step 1: Read project manifest (package.json)
        string manifestPath = Path.Combine(
            fullProjectPath,
            ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Package manifest not found at '{manifestPath}'.", manifestPath);
        }

        string manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest at '{manifestPath}'.");

        string lockFilePath = ResolveLockFilePath(
            fullProjectPath,
            effectiveOptions.LockFilePath);
        await using PackageCacheLease? lockFileLease =
            effectiveOptions.WriteLockFile
                ? await AcquireLockFileLeaseAsync(
                        lockFilePath,
                        cancellationToken)
                    .ConfigureAwait(false)
                : null;
        if (lockFileLease is not null)
        {
            string leasedManifestJson =
                await File.ReadAllTextAsync(
                        manifestPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!string.Equals(
                    manifestJson,
                    leasedManifestJson,
                    StringComparison.Ordinal))
            {
                manifestJson = leasedManifestJson;
                manifest =
                    JsonSerializer.Deserialize<PackageManifest>(
                        manifestJson,
                        s_jsonOptions)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize manifest at '{manifestPath}'.");
            }
        }

        _logger.LogDebug("Read manifest: {Name}@{Version} with {DepCount} dependencies.",
            manifest.Name, manifest.Version, manifest.Dependencies?.Count ?? 0);

        // Step 2: Check for existing lock file
        PackageClosure closure;

        if (File.Exists(lockFilePath))
        {
            _logger.LogDebug("Found existing lock file at '{LockFilePath}'.", lockFilePath);

            PackageLockFile lockFile =
                await PackageLockFile.LoadAsync(
                        lockFilePath,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (IsLockFileCurrent(
                    manifest,
                    lockFile,
                    installPolicy,
                    _fixupPolicy))
            {
                _logger.LogInformation("Lock file is current. Restoring from lock file.");
                closure = await _dependencyResolver.RestoreFromLockFileAsync(lockFile, cancellationToken)
                    .ConfigureAwait(false);

                // Install all resolved packages from the lock file
                closure = await InstallClosureAsync(
                        closure,
                        installPolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
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
            PreferredFhirRelease = installPolicy.PreferredFhirRelease,
            FixupPolicy = _fixupPolicy,
            RootReference =
                new PackageReference(
                    manifest.Name,
                    manifest.Version),
            InstallCachedPackages =
                installPolicy.OverwriteExisting,
        };

        closure = await _dependencyResolver.ResolveAsync(manifest, resolveOptions, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Dependency resolution complete: {Resolved} resolved, {Missing} missing.",
            closure.Resolved.Count, closure.Missing.Count);

        // Step 4: Install all resolved packages
        closure = await InstallClosureAsync(
                closure,
                installPolicy,
                cancellationToken,
                manifest,
                resolveOptions)
            .ConfigureAwait(false);

        // Step 5: Write lock file if configured
        if (effectiveOptions.WriteLockFile && closure.IsComplete)
        {
            PackageLockFile lockFile = CreateLockFile(
                manifest,
                closure,
                installPolicy,
                _fixupPolicy);
            await lockFile.SaveAsync(
                    lockFilePath,
                    async commitCancellationToken =>
                    {
                        string currentManifestJson =
                            await File.ReadAllTextAsync(
                                    manifestPath,
                                    commitCancellationToken)
                                .ConfigureAwait(false);
                        if (!string.Equals(
                                manifestJson,
                                currentManifestJson,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "The project manifest changed during restore; the lock file was not written.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Lock file written to '{LockFilePath}'.", lockFilePath);
        }
        else if (effectiveOptions.WriteLockFile)
        {
            _logger.LogWarning(
                "The dependency closure is incomplete; the existing lock file was not modified.");
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
    public async Task<PackageIndex?> IndexPackageAsync(
        PackageReference reference,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = PackageCacheKey.Create(reference);
        return await IndexPackageCoreAsync(
                reference,
                options?.ForceReindex == true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(
        ResourceSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureResourceIndexesAsync(
                criteria.PackageScope,
                cancellationToken)
            .ConfigureAwait(false);
        return _managedPackageIndexer.FindManagedResources(
            criteria);
    }

    /// <inheritdoc />
    public async Task<ResourceInfo?> FindByCanonicalUrlAsync(
        string canonicalUrl,
        string? packageScope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUrl);
        IReadOnlyList<ResourceInfo> matches =
            await FindResourcesAsync(
                    new ResourceSearchCriteria
                    {
                        Key = canonicalUrl,
                        PackageScope = packageScope,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        return matches.FirstOrDefault(
            resource => string.Equals(
                resource.Url,
                canonicalUrl,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResourceInfo>> FindByResourceTypeAsync(
        string resourceType,
        string? packageScope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        await EnsureResourceIndexesAsync(
                packageScope,
                cancellationToken)
            .ConfigureAwait(false);
        return _managedPackageIndexer.FindManagedByResourceType(
            resourceType,
            packageScope);
    }

    /// <inheritdoc />
    public async Task<JsonNode?> ReadResourceAsync(
        ResourceInfo resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(resource.PackageName)
            || string.IsNullOrWhiteSpace(resource.PackageVersion)
            || string.IsNullOrWhiteSpace(resource.FilePath))
        {
            throw new ArgumentException(
                "Resource information must include package name, package version, and file path.",
                nameof(resource));
        }

        PackageReference reference = new(
            resource.PackageName,
            resource.PackageVersion);
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(reference);
        PortableArchivePath resourcePath =
            PortableArchivePath.Create(
                resource.FilePath,
                isDirectory: false,
                reference.FhirDirective);
        string cacheKeyText = CreateResourceCacheKey(
            cacheKey,
            resourcePath.CanonicalPath);
        if (_cache is IPackageCacheResourceStore resourceStore)
        {
            JsonNode? storedResource =
                await resourceStore.ReadFileAsync(
                        reference,
                        resourcePath.ExactSpelling,
                        generation =>
                        {
                            CachedResource? cached =
                                _memoryCache?.Get<CachedResource>(
                                    cacheKeyText);
                            if (cached is null)
                                return null;

                            if (string.Equals(
                                    cached.ContentGeneration,
                                    generation,
                                    StringComparison.Ordinal))
                            {
                                return cached.Resource;
                            }

                            _memoryCache?.Remove(cacheKeyText);
                            return null;
                        },
                        (generation, content) =>
                        {
                            JsonNode? parsed =
                                JsonNode.Parse(content);
                            if (parsed is not null)
                            {
                                _memoryCache?.Set(
                                    cacheKeyText,
                                    new CachedResource(
                                        generation,
                                        parsed));
                            }

                            return parsed;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            if (storedResource is null)
                _memoryCache?.Remove(cacheKeyText);
            return storedResource;
        }

        string? content =
            await _cache.GetFileContentAsync(
                    reference,
                    resourcePath.ExactSpelling,
                    cancellationToken)
                .ConfigureAwait(false);
        if (content is null)
            return null;

        JsonNode? parsed = JsonNode.Parse(content);
        return parsed;
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
            InvalidatePackageResources(reference);
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
        ClearResourceState();

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
        PackageReference fixedReference = PackageFixups.Apply(
            parsedDirective.ToReference(),
            _fixupPolicy);
        if (!fixedReference.Equals(parsedDirective.ToReference()))
        {
            parsedDirective = DirectiveParser.Parse(fixedReference.FhirDirective);
        }

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

        await using FileStream tarballStream = new FileStream(
            tarballPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PackageReference reference =
            await ExtractReferenceFromTarballAsync(
                    tarballStream,
                    cancellationToken)
                .ConfigureAwait(false);

        _logger.LogDebug("Publishing as {Name}#{Version}.", reference.Name, reference.Version);

        IRegistryClient targetClient =
            RegistryClientFactory.CreateClientForEndpoint(
                registry,
                _registryTransport,
                _loggerFactory,
                _managerInstallLimits);

        tarballStream.Position = 0;
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

        _cacheMutationSubscription?.Dispose();
        _ownedHttpClient?.Dispose();

        try { _logger.LogDebug("FhirPackageManager disposed."); }
        catch { /* Logger may already be disposed during DI container teardown */ }
    }

    #region Private helpers

    private async Task<PackageIndex?> IndexPackageCoreAsync(
        PackageReference reference,
        bool forceReindex,
        CancellationToken cancellationToken)
    {
        PackageRecord? package =
            await _cache.GetPackageAsync(
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);
        if (package is null)
            return null;

        IndexedPackageResult indexed =
            await IndexPackageRecordAsync(
                    package,
                    forceReindex,
                    cancellationToken)
                .ConfigureAwait(false);
        return indexed.Index;
    }

    private async Task<IndexedPackageResult>
        IndexPackageRecordAsync(
        PackageRecord package,
        bool forceReindex,
        CancellationToken cancellationToken)
    {
        if (_cache is IPackageCacheIndexStore indexStore)
        {
            IndexedPackageResult? readyResult = null;
            PackageIndex? persistedIndex =
                await indexStore.GetOrCreateIndexAsync(
                        package.Reference,
                        forceReindex,
                        BuildPackageIndexAsync,
                        cancellationToken,
                        (currentPackage, currentIndex) =>
                        {
                            RegisterPersistedIndex(
                                currentPackage,
                                currentIndex);
                            readyResult =
                                new IndexedPackageResult(
                                    currentPackage,
                                    currentIndex);
                        })
                    .ConfigureAwait(false);
            if (persistedIndex is null)
            {
                throw new DirectoryNotFoundException(
                    $"Cached package '{package.Reference.FhirDirective}' is not available for indexing.");
            }

            if (forceReindex)
            {
                RemovePackageResourceCache(
                    package.Reference);
            }

            return readyResult
                ?? throw new InvalidOperationException(
                    "The package cache did not publish the persisted index generation.");
        }

        if (!forceReindex
            && package.Index is not null)
        {
            RegisterPersistedIndex(
                package,
                package.Index);
            return new IndexedPackageResult(
                package,
                package.Index);
        }

        PackageIndex generatedIndex =
            await BuildPackageIndexAsync(
                    package,
                    cancellationToken)
                .ConfigureAwait(false);
        if (forceReindex)
        {
            RemovePackageResourceCache(
                package.Reference);
        }

        RegisterPersistedIndex(
            package,
            generatedIndex);
        return new IndexedPackageResult(
            package,
            generatedIndex);
    }

    private async Task<PackageIndex> BuildPackageIndexAsync(
        PackageRecord package,
        CancellationToken cancellationToken)
    {
        if (_managedPackageBuilder is not null)
        {
            return await _managedPackageBuilder.BuildIndexAsync(
                    package,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PackageIndex index =
            await _packageIndexer.IndexPackageAsync(
                    package.ContentPath,
                    new IndexingOptions
                    {
                        ForceReindex = true,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        return index
            ?? throw new InvalidDataException(
                "The package indexer returned null.");
    }

    private async Task EnsureResourceIndexesAsync(
        string? packageScope,
        CancellationToken cancellationToken)
    {
        GetPackageScopeFilters(
            packageScope,
            out string? packageIdFilter,
            out string? versionFilter);
        IReadOnlyList<PackageRecord> packages;
        if (_cache is IPackageCacheIndexStore indexStore)
        {
            packages =
                await indexStore.ListPackagesForIndexingAsync(
                        packageIdFilter,
                        versionFilter,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        else
        {
            packages =
                await _cache.ListPackagesAsync(
                        packageIdFilter,
                        versionFilter,
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        List<PackageRecord> scopedPackages = packages
            .Where(package =>
                PackageScopeMatches(
                    package.Reference,
                    packageScope))
            .ToList();
        HashSet<string> currentIdentities =
            scopedPackages
                .Select(package =>
                    PackageCacheKey.Create(
                            package.Reference)
                        .CanonicalIdentity)
                .ToHashSet(StringComparer.Ordinal);
        ReconcileMissingPackages(
            packageScope,
            currentIdentities);

        foreach (PackageRecord package in scopedPackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPackageRegistrationCurrent(
                    package))
            {
                continue;
            }

            _ = await IndexPackageRecordAsync(
                    package,
                    forceReindex: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<PackageRecord> TryIndexInstalledPackageAsync(
        PackageRecord package,
        IProgress<PackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            ReportProgress(
                progress,
                package.Reference.Name,
                PackageProgressPhase.Indexing);
            InvalidatePackageResources(
                package.Reference);
            IndexedPackageResult indexed =
                await IndexPackageRecordAsync(
                        package,
                        forceReindex: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            return indexed.Package with
            {
                Index = indexed.Index,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Package {Directive} was installed, but its derivative resource index could not be generated.",
                package.Reference.FhirDirective);
            return package;
        }
    }

    private void InvalidatePackageResources(
        PackageReference reference)
    {
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(reference);
        lock (_resourceStateLock)
        {
            _registeredPackages.Remove(
                cacheKey.CanonicalIdentity);
            _managedPackageIndexer.Unregister(
                cacheKey.CanonicalReference);
            RemovePackageResourceCache(
                cacheKey);
        }
    }

    private void ClearResourceState()
    {
        lock (_resourceStateLock)
        {
            _registeredPackages.Clear();
            _managedPackageIndexer.Clear();
            _memoryCache?.Clear();
        }
    }

    private void RegisterPersistedIndex(
        PackageRecord package,
        PackageIndex index)
    {
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(package.Reference);
        string contentGeneration =
            package.ContentGeneration
            ?? string.Empty;
        lock (_resourceStateLock)
        {
            if (_registeredPackages.TryGetValue(
                    cacheKey.CanonicalIdentity,
                    out RegisteredPackageState? existing)
                && !string.Equals(
                    existing.ContentGeneration,
                    contentGeneration,
                    StringComparison.Ordinal))
            {
                _memoryCache?.RemoveByPrefix(
                    CreateResourceCachePrefix(cacheKey));
            }

            _managedPackageIndexer.RegisterPersistedIndex(
                cacheKey.CanonicalReference,
                index);
            _registeredPackages[cacheKey.CanonicalIdentity] =
                new RegisteredPackageState(
                    cacheKey.CanonicalReference,
                    contentGeneration);
        }
    }

    private bool IsPackageRegistrationCurrent(
        PackageRecord package)
    {
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(package.Reference);
        string contentGeneration =
            package.ContentGeneration
            ?? string.Empty;
        lock (_resourceStateLock)
        {
            return _registeredPackages.TryGetValue(
                    cacheKey.CanonicalIdentity,
                    out RegisteredPackageState? existing)
                && string.Equals(
                    existing.ContentGeneration,
                    contentGeneration,
                    StringComparison.Ordinal);
        }
    }

    private void ReconcileMissingPackages(
        string? packageScope,
        IReadOnlySet<string> currentIdentities)
    {
        lock (_resourceStateLock)
        {
            List<KeyValuePair<
                string,
                RegisteredPackageState>> removedRegistrations =
                _registeredPackages
                    .Where(registration =>
                        PackageScopeMatches(
                            registration.Value.Reference,
                            packageScope)
                        && !currentIdentities.Contains(
                            registration.Key))
                    .ToList();
            foreach (KeyValuePair<
                         string,
                         RegisteredPackageState> registration
                     in removedRegistrations)
            {
                _registeredPackages.Remove(
                    registration.Key);
                _managedPackageIndexer.Unregister(
                    registration.Value.Reference);
                _memoryCache?.RemoveByPrefix(
                    CreateResourceCachePrefix(
                        PackageCacheKey.Create(
                            registration.Value.Reference)));
            }
        }
    }

    private static bool PackageScopeMatches(
        PackageReference reference,
        string? packageScope)
    {
        if (string.IsNullOrWhiteSpace(packageScope))
            return true;

        int separatorIndex = packageScope.LastIndexOf('#');
        if (separatorIndex <= 0)
        {
            return reference.Name.Equals(
                packageScope,
                StringComparison.OrdinalIgnoreCase);
        }

        return reference.Name.Equals(
                packageScope[..separatorIndex],
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                reference.Version,
                packageScope[(separatorIndex + 1)..],
                StringComparison.Ordinal);
    }

    private static void GetPackageScopeFilters(
        string? packageScope,
        out string? packageIdFilter,
        out string? versionFilter)
    {
        if (string.IsNullOrWhiteSpace(packageScope))
        {
            packageIdFilter = null;
            versionFilter = null;
            return;
        }

        int separatorIndex =
            packageScope.LastIndexOf('#');
        if (separatorIndex <= 0)
        {
            packageIdFilter = packageScope;
            versionFilter = null;
            return;
        }

        packageIdFilter =
            packageScope[..separatorIndex];
        versionFilter =
            packageScope[(separatorIndex + 1)..];
    }

    private static string CreateResourceCachePrefix(
        PackageCacheKey cacheKey) =>
        $"{cacheKey.CanonicalIdentity}\0";

    private void RemovePackageResourceCache(
        PackageReference reference) =>
        RemovePackageResourceCache(
            PackageCacheKey.Create(reference));

    private void RemovePackageResourceCache(
        PackageCacheKey cacheKey) =>
        _memoryCache?.RemoveByPrefix(
            CreateResourceCachePrefix(cacheKey));

    private static string CreateResourceCacheKey(
        PackageCacheKey cacheKey,
        string resourcePath) =>
        $"{CreateResourceCachePrefix(cacheKey)}{resourcePath}";

    private sealed record RegisteredPackageState(
        PackageReference Reference,
        string ContentGeneration);

    private sealed record IndexedPackageResult(
        PackageRecord Package,
        PackageIndex Index);

    private sealed class CachedResource(
        string contentGeneration,
        JsonNode resource) :
        ICloneable
    {
        internal string ContentGeneration { get; } =
            contentGeneration;

        internal JsonNode Resource { get; } =
            resource;

        public object Clone() =>
            new CachedResource(
                ContentGeneration,
                Resource.DeepClone());
    }

    private static HttpClient CreateDirectHttpClient(
        FhirPackageManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = options.MaxRedirects
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    private IHardenedPackageCache RequireHardenedCache()
    {
        if (_cache is IHardenedPackageCache hardenedCache)
            return hardenedCache;

        throw new PackageInstallException(
            PackageInstallErrorCode.UnsupportedCacheCapability,
            PackageInstallStage.CacheInspection,
            "The configured package cache does not advertise the hardened installation contract.");
    }

    private async Task<PackageRecord> InstallExpectedSourceAsync(
        IHardenedPackageCache hardenedCache,
        PackageIdentityExpectation expectation,
        Stream packageStream,
        long? reportedContentLength,
        PackageSourceInstallOptions? options,
        ResolvedPackageInstallPolicy policy,
        bool reportFailure,
        CancellationToken cancellationToken)
    {
        string directive = expectation.Reference.FhirDirective;
        try
        {
            ReportProgress(
                policy.Progress,
                expectation.Reference.Name,
                PackageProgressPhase.Acquiring);
            PackageRecord record = await hardenedCache.InstallAsync(
                    expectation.Reference,
                    packageStream,
                    CreateSourceCacheOptions(
                        expectation,
                        reportedContentLength,
                        options,
                        policy),
                    cancellationToken)
                .ConfigureAwait(false);
            if (policy.IncludeDependencies)
            {
                await InstallDependenciesAsync(
                        record,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            record = await TryIndexInstalledPackageAsync(
                    record,
                    policy.Progress,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportProgress(
                policy.Progress,
                expectation.Reference.Name,
                PackageProgressPhase.Complete);
            return record;
        }
        catch (PackageInstallException)
        {
            if (reportFailure)
            {
                ReportProgress(
                    policy.Progress,
                    expectation.Reference.Name,
                    PackageProgressPhase.Failed);
            }

            throw;
        }
    }

    private async Task<PackageRecord> ImportSourceAsync(
        IHardenedPackageCache hardenedCache,
        Stream packageStream,
        long? reportedContentLength,
        PackageSourceInstallOptions? options,
        ResolvedPackageInstallPolicy policy,
        bool reportFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            ReportProgress(
                policy.Progress,
                "package import",
                PackageProgressPhase.Acquiring);
            PackageRecord record = await hardenedCache.ImportAsync(
                    packageStream,
                    CreateSourceCacheOptions(
                        expectation: null,
                        reportedContentLength,
                        options,
                        policy),
                    cancellationToken)
                .ConfigureAwait(false);
            if (policy.IncludeDependencies)
            {
                await InstallDependenciesAsync(
                        record,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            record = await TryIndexInstalledPackageAsync(
                    record,
                    policy.Progress,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportProgress(
                policy.Progress,
                record.Reference.Name,
                PackageProgressPhase.Complete);
            return record;
        }
        catch (PackageInstallException)
        {
            if (reportFailure)
            {
                ReportProgress(
                    policy.Progress,
                    "package import",
                    PackageProgressPhase.Failed);
            }

            throw;
        }
    }

    private static InstallCacheOptions CreateSourceCacheOptions(
        PackageIdentityExpectation? expectation,
        long? reportedContentLength,
        PackageSourceInstallOptions? options,
        ResolvedPackageInstallPolicy policy) =>
        new()
        {
            OverwriteExisting = policy.OverwriteExisting,
            VerifyChecksum = policy.VerifyChecksums,
            Limits = policy.Limits,
            ReportedContentLength = reportedContentLength,
            ExpectedSha256Sum = options?.ExpectedSha256,
            ExpectedShaSum = options?.ExpectedSha1,
            IdentityExpectation = expectation,
            CorruptCacheBehavior = policy.CorruptCacheBehavior,
            Progress = policy.Progress
        };

    private async Task<PackageRecord> ExecuteUriSourceAsync(
        Uri packageUri,
        string progressPackageId,
        IProgress<PackageProgress>? progress,
        Func<Stream, long?, Task<PackageRecord>> install,
        CancellationToken cancellationToken)
    {
        ReportProgress(
            progress,
            progressPackageId,
            PackageProgressPhase.Downloading);
        try
        {
            await using DeadlineAwareHttpStream packageStream =
                await OpenHttpPackageStreamAsync(
                        packageUri,
                        cancellationToken)
                    .ConfigureAwait(false);
            return await install(
                    packageStream,
                    packageStream.ContentLength)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            ReportProgress(
                progress,
                progressPackageId,
                PackageProgressPhase.Failed);
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                "The package URI did not complete before the configured timeout.",
                innerException: exception);
        }
        catch (OperationCanceledException)
        {
            ReportProgress(
                progress,
                progressPackageId,
                PackageProgressPhase.Failed);
            throw;
        }
        catch (PackageInstallException)
        {
            ReportProgress(
                progress,
                progressPackageId,
                PackageProgressPhase.Failed);
            throw;
        }
        catch (HttpRequestException exception)
        {
            ReportProgress(
                progress,
                progressPackageId,
                PackageProgressPhase.Failed);
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                "The package URI could not be acquired.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            ReportProgress(
                progress,
                progressPackageId,
                PackageProgressPhase.Failed);
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                "The package URI could not be acquired.",
                innerException: exception);
        }
    }

    private async Task<DeadlineAwareHttpStream> OpenHttpPackageStreamAsync(
        Uri packageUri,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource timeoutSource = new();
        timeoutSource.CancelAfter(_options.HttpTimeout);
        HttpResponseMessage? response = null;
        bool completed = false;
        Uri currentUri = packageUri;
        int redirectsFollowed = 0;
        try
        {
            while (true)
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Get,
                    currentUri);
                using CancellationTokenSource linkedSource =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        timeoutSource.Token);
                response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedSource.Token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode)
                    && response.Headers.Location is Uri location)
                {
                    if (redirectsFollowed >= _options.MaxRedirects)
                    {
                        throw new HttpRequestException(
                            $"The package URI exceeded the configured redirect limit of {_options.MaxRedirects}.",
                            inner: null,
                            response.StatusCode);
                    }

                    Uri nextUri = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentUri, location);
                    ValidatePackageUri(nextUri);
                    response.Dispose();
                    response = null;
                    currentUri = nextUri;
                    redirectsFollowed++;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                Stream content = await response.Content.ReadAsStreamAsync(
                        linkedSource.Token)
                    .ConfigureAwait(false);
                DeadlineAwareHttpStream result = new(
                    content,
                    response,
                    timeoutSource,
                    cancellationToken,
                    () => new OperationCanceledException(
                        "The package URI did not complete before the configured timeout.",
                        timeoutSource.Token),
                    response.Content.Headers.ContentLength);
                completed = true;
                return result;
            }
        }
        finally
        {
            if (!completed)
            {
                response?.Dispose();
                timeoutSource.Dispose();
            }
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode) =>
        statusCode is
            System.Net.HttpStatusCode.MultipleChoices
            or System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static void ValidatePackageUri(Uri packageUri)
    {
        if (!packageUri.IsAbsoluteUri
            || (packageUri.Scheme != Uri.UriSchemeHttp
                && packageUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                "Package URIs must be absolute HTTP or HTTPS URIs.");
        }
    }

    private async Task<PackageRecord?> InstallDirectiveAsync(
        string directive,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken,
        bool applyFixups = true,
        PackageReference? expectedResolvedReference = null)
    {
        try
        {
            return await InstallDirectiveCoreAsync(
                    directive,
                    policy,
                    cancellationToken,
                    applyFixups,
                    expectedResolvedReference)
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
        CancellationToken cancellationToken,
        bool applyFixups,
        PackageReference? expectedResolvedReference)
    {
        _logger.LogDebug("Installing directive '{Directive}' through the unified install contract.", directive);

        PackageDirective parsedDirective = DirectiveParser.Parse(directive);
        PackageReference parsedReference = parsedDirective.ToReference();
        PackageReference requestedReference = applyFixups
            ? PackageFixups.Apply(
                parsedReference,
                _fixupPolicy)
            : parsedReference;
        if (applyFixups
            && !requestedReference.Equals(parsedReference))
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

        if (requestedCacheKey is not null)
        {
            await ThrowIfCacheCorruptAsync(
                    requestedCacheKey,
                    policy,
                    directive,
                    cancellationToken)
                .ConfigureAwait(false);
        }

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
                PackageRecord? cachedRecord = await GetCachedPackageAsync(
                        localKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cachedRecord is not null
                    && expectedResolvedReference
                        is PackageReference expectedCachedReference)
                {
                    _ = PackageIdentityValidator.ValidateExpected(
                        cachedRecord.Manifest,
                        new PackageIdentityExpectation
                        {
                            Kind =
                                PackageIdentityExpectationKind.Alias,
                            Reference = requestedReference,
                            ExpectedManifestReference =
                                expectedCachedReference,
                        },
                        directive);
                }

                await InstallCachedDependenciesIfRequestedAsync(
                        cachedRecord,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
                return cachedRecord;
            }

            _logger.LogWarning(
                "Local package alias '{Directive}' is not present and cannot be resolved from registries.",
                directive);
            ReportProgress(policy.Progress, requestedReference.Name, PackageProgressPhase.Failed);
            return null;
        }

        if (!policy.OverwriteExisting
            && parsedDirective.VersionType
                is VersionType.CiBuild
                    or VersionType.CiBuildBranch
            && expectedResolvedReference
                is PackageReference expectedCachedAlias)
        {
            PackageCacheKey aliasKey = requestedCacheKey!;
            if (await IsInstalledAsync(
                    aliasKey,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                PackageRecord? cachedRecord =
                    await GetCachedPackageAsync(
                            aliasKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (cachedRecord is not null
                    && cachedRecord.Manifest.Name.Equals(
                        expectedCachedAlias.Name,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        cachedRecord.Manifest.Version,
                        expectedCachedAlias.Version,
                        StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Cached package alias {Alias} matches locked identity {Identity}.",
                        requestedReference.FhirDirective,
                        expectedCachedAlias.FhirDirective);
                    await InstallCachedDependenciesIfRequestedAsync(
                            cachedRecord,
                            policy,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return cachedRecord;
                }
            }
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
                PackageRecord? cachedRecord = await GetCachedPackageAsync(
                        requestedKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                await InstallCachedDependenciesIfRequestedAsync(
                        cachedRecord,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
                return cachedRecord;
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
        bool resolvedIdentityIsExact =
            PackageDirective.ClassifyVersion(
                resolved.Reference.Version)
            == VersionType.Exact;
        if (expectedResolvedReference is PackageReference expected
            && (!resolved.Reference.Name.Equals(
                    expected.Name,
                    StringComparison.OrdinalIgnoreCase)
                || (!preservesAlias
                        || resolvedIdentityIsExact)
                    && !string.Equals(
                        resolved.Reference.Version,
                        expected.Version,
                        StringComparison.Ordinal)))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                $"Resolved dependency '{resolved.Reference.FhirDirective}' did not match the active closure identity '{expected.FhirDirective}'.",
                requestedReference.FhirDirective);
        }

        if (!applyFixups
            && expectedResolvedReference is null
            && (!resolved.Reference.Name.Equals(
                    requestedReference.Name,
                    StringComparison.OrdinalIgnoreCase)
                || parsedDirective.VersionType == VersionType.Exact
                && !string.Equals(
                    resolved.Reference.Version,
                    requestedReference.Version,
                    StringComparison.Ordinal)))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                $"Resolved dependency '{resolved.Reference.FhirDirective}' did not match the active closure node '{requestedReference.FhirDirective}'.",
                requestedReference.FhirDirective);
        }

        PackageReference? expectedManifestReference =
            expectedResolvedReference;
        if (preservesAlias
            && expectedManifestReference is null
            && PackageDirective.ClassifyVersion(
                resolved.Reference.Version) == VersionType.Exact)
        {
            expectedManifestReference = resolved.Reference;
        }

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
            Reference = preservesAlias ? requestedReference : resolved.Reference,
            ExpectedManifestReference =
                preservesAlias
                    ? expectedManifestReference
                    : null,
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
        catch (RegistryResponseTimeoutException exception)
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

    private async Task ThrowIfCacheCorruptAsync(
        PackageCacheKey cacheKey,
        ResolvedPackageInstallPolicy policy,
        string directive,
        CancellationToken cancellationToken)
    {
        IHardenedPackageCache hardenedCache = RequireHardenedCache();
        HardenedPackageCacheInspection inspection =
            await hardenedCache.InspectAsync(
                    cacheKey.DisplayReference,
                    cancellationToken)
                .ConfigureAwait(false);
        if (inspection.State == HardenedPackageCacheState.Corrupt
            && (policy.CorruptCacheBehavior == CorruptCacheBehavior.Strict
                || !inspection.IsRepairable))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.CorruptCache,
                PackageInstallStage.CacheInspection,
                $"Cached package '{cacheKey.DisplayReference.FhirDirective}' is corrupt: " +
                $"{inspection.CorruptionReason}",
                directive);
        }
    }

    private async Task<PackageRecord> InstallResolvedAsync(
        PackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        PackageReference cacheReference = request.CacheKey.DisplayReference;
        ResolvedDirective resolved = request.Source.ResolvedDirective;
        await ThrowIfCacheCorruptAsync(
                request.CacheKey,
                request.Policy,
                request.Directive,
                cancellationToken)
            .ConfigureAwait(false);

        bool isInstalled = await IsInstalledAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
        PackageRecord? cachedRecord = isInstalled
            ? await GetCachedPackageAsync(request.CacheKey, cancellationToken).ConfigureAwait(false)
            : null;
        bool cachedMatchesExpectedIdentity =
            !isInstalled
            || request.IdentityExpectation.ExpectedManifestReference
                is not PackageReference expectedManifest
            || cachedRecord is not null
            && cachedRecord.Manifest.Name.Equals(
                expectedManifest.Name,
                StringComparison.OrdinalIgnoreCase)
            && cachedRecord.Manifest.Version.Equals(
                expectedManifest.Version,
                StringComparison.Ordinal);
        CacheMetadataEntry? existingMetadata = null;

        if (request.Freshness == PackageInstallFreshness.Immutable
            && isInstalled
            && !request.Policy.OverwriteExisting
            && cachedRecord is not null)
        {
            await InstallCachedDependenciesIfRequestedAsync(
                    cachedRecord,
                    request.Policy,
                    cancellationToken)
                .ConfigureAwait(false);
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
                && cachedMatchesExpectedIdentity
                && sourcePublicationDate.HasValue
                && existingMetadata?.SourcePublicationDate is DateTimeOffset cachedPublicationDate
                && cachedPublicationDate >= sourcePublicationDate.Value)
            {
                _logger.LogInformation(
                    "Mutable alias {CacheKey} is current according to source publication metadata.",
                    request.CacheKey.MetadataKey);
                await InstallCachedDependenciesIfRequestedAsync(
                        cachedRecord,
                        request.Policy,
                        cancellationToken)
                    .ConfigureAwait(false);
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
            IdentityExpectation = request.IdentityExpectation,
            CorruptCacheBehavior = request.Policy.CorruptCacheBehavior,
            SkipIfArchiveUnchanged =
                request.Freshness
                    == PackageInstallFreshness.RefreshableAlias
                && cachedMatchesExpectedIdentity,
            Progress = request.Policy.Progress
        };

        PackageRecord record;
        try
        {
            record = await _cache.InstallAsync(
                    cacheReference,
                    downloadResult.Content,
                    installCacheOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (RegistryResponseTimeoutException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                $"Package '{request.Directive}' download timed out while reading the response body.",
                request.Directive,
                exception);
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

        record = await TryIndexInstalledPackageAsync(
                record,
                request.Policy.Progress,
                cancellationToken)
            .ConfigureAwait(false);
        ReportProgress(
            request.Policy.Progress,
            cacheReference.Name,
            PackageProgressPhase.Complete);
        return record;
    }

    private async Task InstallCachedDependenciesIfRequestedAsync(
        PackageRecord? record,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken)
    {
        if (record is not null && policy.IncludeDependencies)
        {
            await InstallDependenciesAsync(
                    record,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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
                results[index] = await InstallResultAsync(
                        directive,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
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

    private async Task<PackageInstallResult> InstallResultAsync(
        string directive,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken,
        bool applyFixups = true,
        PackageReference? expectedResolvedReference = null)
    {
        try
        {
            PackageRecord? record = await InstallDirectiveAsync(
                    directive,
                    policy,
                    cancellationToken,
                    applyFixups,
                    expectedResolvedReference)
                .ConfigureAwait(false);
            return record is not null
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
                    ErrorMessage =
                        $"Package '{directive}' could not be resolved.",
                    ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                    ErrorStage = PackageInstallStage.Resolution
                };
        }
        catch (DependencyInstallationException exception)
        {
            _logger.LogError(
                exception,
                "Package '{Directive}' committed, but dependency installation failed.",
                directive);
            return new PackageInstallResult
            {
                Directive = directive,
                Package = exception.RootPackage,
                Status = PackageInstallStatus.Failed,
                ErrorMessage = exception.Message,
                ErrorCode = exception.ErrorCode,
                ErrorStage = exception.Stage,
                DependencyFailures = exception.DependencyFailures
            };
        }
        catch (PackageInstallException exception)
        {
            _logger.LogError(
                exception,
                "Failed to install '{Directive}'.",
                directive);
            return new PackageInstallResult
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
            _logger.LogError(
                exception,
                "Invalid package directive '{Directive}'.",
                directive);
            return new PackageInstallResult
            {
                Directive = directive,
                Status = PackageInstallStatus.Failed,
                ErrorMessage = exception.Message,
                ErrorCode =
                    PackageInstallErrorCode.InvalidPackageIdentity,
                ErrorStage = PackageInstallStage.IdentityValidation
            };
        }
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
        catch (RegistryResponseTimeoutException exception)
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

    private static PackageInstallFreshness GetFreshness(VersionType versionType) =>
        versionType switch
        {
            VersionType.CiBuild or VersionType.CiBuildBranch =>
                PackageInstallFreshness.RefreshableAlias,
            VersionType.LocalBuild => PackageInstallFreshness.LocalAuthoritative,
            _ => PackageInstallFreshness.Immutable
        };

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
    /// Resolves and installs the active dependency closure of a committed package.
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

        PackageClosure closure =
            await ResolveDependencyClosureAsync(
                    record,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
        ResolvedPackageInstallPolicy childPolicy =
            policy.WithoutDependencies();
        Dictionary<string, PackageInstallResult> bootstrapFailures =
            new(StringComparer.Ordinal);
        List<string> bootstrapFailureOrder = [];
        HashSet<string> failedBootstrapPackages =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> failedBootstrapReferences =
            new(StringComparer.Ordinal);
        HashSet<string> bootstrapStates =
            new(StringComparer.Ordinal);
        HashSet<string> bootstrappedReferences =
            new(StringComparer.Ordinal);
        while (closure.BootstrapInstallOrder.Count > 0)
        {
            List<PackageReference> candidates =
                closure.BootstrapInstallOrder
                    .Where(reference =>
                    {
                        string key =
                            CreateDependencyReferenceKey(reference);
                        return !bootstrappedReferences.Contains(key)
                            && !failedBootstrapReferences.Contains(key);
                    })
                    .ToList();
            if (candidates.Count == 0)
                break;

            string bootstrapState = string.Join(
                "\n",
                candidates.Select(
                    reference =>
                        reference.FhirDirective));
            if (!bootstrapStates.Add(bootstrapState))
            {
                break;
            }

            bool bootstrapSucceeded = false;
            foreach (PackageReference reference in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageInstallResult result = await InstallResultAsync(
                        reference.FhirDirective,
                        childPolicy,
                        cancellationToken,
                        applyFixups: false,
                        expectedResolvedReference:
                            GetExpectedClosureIdentity(
                                closure,
                                reference))
                    .ConfigureAwait(false);
                if (result.Status
                    is not (
                        PackageInstallStatus.Failed
                        or PackageInstallStatus.NotFound))
                {
                    bootstrappedReferences.Add(
                        CreateDependencyReferenceKey(reference));
                    bootstrapSucceeded = true;
                    continue;
                }

                string failureKey =
                    CreateDependencyReferenceKey(reference);
                if (bootstrapFailures.TryAdd(
                        failureKey,
                        result))
                {
                    bootstrapFailureOrder.Add(failureKey);
                }
                failedBootstrapPackages.Add(reference.Name);
                failedBootstrapReferences.Add(
                    failureKey);
            }

            if (!bootstrapSucceeded)
                break;

            closure = await ResolveDependencyClosureAsync(
                    record,
                    policy,
                    cancellationToken,
                    preferCachedAliases: true)
                .ConfigureAwait(false);
        }

        HashSet<string> activeBootstrapReferences =
            closure.BootstrapInstallOrder
                .Select(CreateDependencyReferenceKey)
                .ToHashSet(StringComparer.Ordinal);
        failedBootstrapReferences.IntersectWith(
            activeBootstrapReferences);
        failedBootstrapPackages.Clear();
        foreach (PackageReference reference
                 in closure.BootstrapInstallOrder)
        {
            if (failedBootstrapReferences.Contains(
                    CreateDependencyReferenceKey(reference)))
            {
                failedBootstrapPackages.Add(reference.Name);
            }
        }

        List<PackageInstallResult> failures =
            bootstrapFailureOrder
                .Where(activeBootstrapReferences.Contains)
                .Select(key => bootstrapFailures[key])
                .ToList();
        failures.AddRange(
            closure.Failures
                .Where(
                    failure =>
                        !failedBootstrapReferences.Contains(
                            CreateDependencyReferenceKey(
                                failure.PackageId,
                                failure.VersionSpecifier)))
                .Select(CreateClosureFailureResult)
                .ToList());
        HashSet<string> structuredFailurePackages =
            closure.Failures
                .Select(failure => failure.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        structuredFailurePackages.UnionWith(
            failedBootstrapPackages);
        foreach (KeyValuePair<string, string> missing in closure.Missing)
        {
            if (structuredFailurePackages.Add(missing.Key))
            {
                failures.Add(new PackageInstallResult
                {
                    Directive = missing.Key,
                    Status = PackageInstallStatus.NotFound,
                    ErrorMessage = missing.Value,
                    ErrorCode =
                        PackageInstallErrorCode.DependencyInstallationFailed,
                    ErrorStage =
                        PackageInstallStage.DependencyInstallation
                });
            }
        }

        IEnumerable<PackageReference> installOrder =
            closure.InstallOrderIsComplete
                ? closure.InstallOrder
                : closure.Resolved.Values
                    .OrderBy(
                        reference => reference.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        reference => reference.Version,
                        StringComparer.Ordinal);
        HashSet<string> attemptedPackages =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageReference reference in installOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reference.Name.Equals(
                    record.Manifest.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(
                        reference.Version,
                        record.Manifest.Version,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                failures.Add(new PackageInstallResult
                {
                    Directive = reference.FhirDirective,
                    Status = PackageInstallStatus.Failed,
                    ErrorMessage =
                        $"The active dependency graph requires '{reference.FhirDirective}', but the committed root is '{record.Reference.FhirDirective}'.",
                    ErrorCode =
                        PackageInstallErrorCode.DependencyInstallationFailed,
                    ErrorStage =
                        PackageInstallStage.DependencyInstallation
                });
                continue;
            }

            if (!attemptedPackages.Add(reference.Name))
            {
                continue;
            }

            if (!reference.HasVersion)
            {
                failures.Add(new PackageInstallResult
                {
                    Directive = reference.Name,
                    Status = PackageInstallStatus.Failed,
                    ErrorMessage =
                        "The dependency closure did not contain an exact version.",
                    ErrorCode =
                        PackageInstallErrorCode.DependencyInstallationFailed,
                    ErrorStage =
                        PackageInstallStage.DependencyInstallation
                });
                continue;
            }

            string directive = reference.FhirDirective;
            PackageInstallResult result = await InstallResultAsync(
                    directive,
                    childPolicy,
                    cancellationToken,
                    applyFixups: false,
                    expectedResolvedReference:
                        GetExpectedClosureIdentity(
                            closure,
                            reference))
                .ConfigureAwait(false);
            if (result.Status
                is PackageInstallStatus.Failed
                    or PackageInstallStatus.NotFound)
            {
                failures.Add(result);
                _logger.LogWarning(
                    "Failed to install dependency '{Directive}' for {Name}#{Version}: {Failure}.",
                    directive,
                    record.Reference.Name,
                    record.Reference.Version,
                    result.GetFailureDescription()
                        ?? "Unknown dependency installation failure.");
            }
        }

        if (failures.Count > 0)
        {
            throw new DependencyInstallationException(
                record,
                failures,
                closure.Failures);
        }
    }

    private async Task<PackageClosure> ResolveDependencyClosureAsync(
        PackageRecord record,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken,
        bool preferCachedAliases = false)
    {
        try
        {
            return await _dependencyResolver.ResolveAsync(
                    record.Manifest,
                    new DependencyResolveOptions
                    {
                        ConflictStrategy = policy.ConflictStrategy,
                        MaxDepth = policy.MaxDepth,
                        AllowPreRelease = policy.AllowPreRelease,
                        PreferredFhirRelease =
                            policy.PreferredFhirRelease,
                        FixupPolicy = _fixupPolicy,
                        RootReference = record.Reference,
                        InstallCachedPackages =
                            policy.OverwriteExisting,
                        PreferCachedAliases =
                            preferCachedAliases,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageInstallException exception)
        {
            throw new DependencyInstallationException(
                record,
                [CreateResolverExceptionResult(exception)]);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or FormatException
                or InvalidDataException
                or JsonException
                or IOException
                or HttpRequestException)
        {
            throw new DependencyInstallationException(
                record,
                [
                    new PackageInstallResult
                    {
                        Directive = record.Reference.FhirDirective,
                        Status = PackageInstallStatus.Failed,
                        ErrorMessage = exception.Message,
                        ErrorCode =
                            PackageInstallErrorCode.DependencyInstallationFailed,
                        ErrorStage =
                            PackageInstallStage.DependencyInstallation
                    }
                ]);
        }
    }

    private static string CreateDependencyReferenceKey(
        PackageReference reference) =>
        CreateDependencyReferenceKey(
            reference.Name,
            reference.Version);

    private static string CreateDependencyReferenceKey(
        string packageId,
        string? version) =>
        $"{packageId.ToLowerInvariant()}\0{version}";

    private static PackageInstallResult CreateClosureFailureResult(
        DependencyResolutionFailure failure)
    {
        string version = failure.VersionSpecifier
            ?? failure.SelectedVersion
            ?? "latest";
        string directive = $"{failure.PackageId}#{version}";
        return new PackageInstallResult
        {
            Directive = directive,
            Status =
                failure.Code
                    == DependencyResolutionFailureCode.PackageNotFound
                    ? PackageInstallStatus.NotFound
                    : PackageInstallStatus.Failed,
            ErrorMessage = failure.Message,
            ErrorCode =
                PackageInstallErrorCode.DependencyInstallationFailed,
            ErrorStage = PackageInstallStage.DependencyInstallation
        };
    }

    private static PackageInstallResult CreateResolverExceptionResult(
        PackageInstallException exception) =>
        new()
        {
            Directive = exception.Directive
                ?? "dependency closure",
            Status = PackageInstallStatus.Failed,
            ErrorMessage = exception.Message,
            ErrorCode = exception.ErrorCode,
            ErrorStage = exception.Stage
        };

    /// <summary>
    /// Installs all packages referenced in a closure that are not already cached.
    /// </summary>
    private async Task<PackageClosure> InstallClosureAsync(
        PackageClosure closure,
        ResolvedPackageInstallPolicy policy,
        CancellationToken cancellationToken,
        PackageManifest? manifest = null,
        DependencyResolveOptions? resolveOptions = null)
    {
        List<PackageInstallResult> results = [];
        Dictionary<string, PackageInstallResult> bootstrapFailures =
            new(StringComparer.Ordinal);
        List<string> bootstrapFailureOrder = [];
        ResolvedPackageInstallPolicy childPolicy =
            policy.WithoutDependencies();
        if (manifest is not null
            && resolveOptions is not null
            && closure.BootstrapInstallOrder.Count > 0)
        {
            HashSet<string> completed =
                new(StringComparer.Ordinal);
            HashSet<string> failed =
                new(StringComparer.Ordinal);
            HashSet<string> states =
                new(StringComparer.Ordinal);
            while (closure.BootstrapInstallOrder.Count > 0)
            {
                List<PackageReference> candidates =
                    closure.BootstrapInstallOrder
                        .Where(reference =>
                        {
                            string key =
                                CreateDependencyReferenceKey(
                                    reference);
                            return !completed.Contains(key)
                                && !failed.Contains(key);
                        })
                        .ToList();
                if (candidates.Count == 0)
                    break;

                string state = string.Join(
                    "\n",
                    candidates.Select(
                        reference =>
                            reference.FhirDirective));
                if (!states.Add(state))
                    break;

                bool bootstrapSucceeded = false;
                foreach (PackageReference reference in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PackageInstallResult result =
                        await InstallResultAsync(
                                reference.FhirDirective,
                                childPolicy,
                                cancellationToken,
                                applyFixups: false,
                                expectedResolvedReference:
                                    GetExpectedClosureIdentity(
                                        closure,
                                        reference))
                            .ConfigureAwait(false);
                    if (result.Status
                        is PackageInstallStatus.Failed
                            or PackageInstallStatus.NotFound)
                    {
                        string failureKey =
                            CreateDependencyReferenceKey(
                                reference);
                        if (bootstrapFailures.TryAdd(
                                failureKey,
                                result))
                        {
                            bootstrapFailureOrder.Add(
                                failureKey);
                        }

                        failed.Add(failureKey);
                    }
                    else
                    {
                        completed.Add(
                            CreateDependencyReferenceKey(
                                reference));
                        bootstrapSucceeded = true;
                    }
                }

                if (!bootstrapSucceeded)
                    break;

                resolveOptions.PreferCachedAliases = true;
                closure = await _dependencyResolver.ResolveAsync(
                        manifest,
                        resolveOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        HashSet<string> activeBootstrapReferences =
            closure.BootstrapInstallOrder
                .Select(CreateDependencyReferenceKey)
                .ToHashSet(StringComparer.Ordinal);
        results.AddRange(
            bootstrapFailureOrder
                .Where(activeBootstrapReferences.Contains)
                .Select(key => bootstrapFailures[key]));

        IEnumerable<PackageReference> installOrder =
            closure.InstallOrderIsComplete
                ? closure.InstallOrder
                : closure.Resolved.Values
                    .OrderBy(
                        reference => reference.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        reference => reference.Version,
                        StringComparer.Ordinal);
        List<PackageReference> references =
            installOrder
                .Where(reference => reference.HasVersion)
                .ToList();
        _logger.LogDebug(
            "Installing {Count} packages from closure.",
            references.Count);

        foreach (PackageReference reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(
                await InstallResultAsync(
                        reference.FhirDirective,
                        childPolicy,
                        cancellationToken,
                        applyFixups: false,
                        expectedResolvedReference:
                            GetExpectedClosureIdentity(
                                closure,
                                reference))
                    .ConfigureAwait(false));
        }

        PackageInstallResult? failure = results.FirstOrDefault(
            result => result.Status
                is PackageInstallStatus.Failed or PackageInstallStatus.NotFound);
        if (failure is null)
            return closure;

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

    private static PackageReference? GetExpectedClosureIdentity(
        PackageClosure closure,
        PackageReference installationReference)
    {
        VersionType referenceType =
            PackageDirective.ClassifyVersion(
                installationReference.Version);
        return (referenceType
                    is VersionType.CiBuild
                        or VersionType.CiBuildBranch
                        or VersionType.LocalBuild)
            && closure.Resolved.TryGetValue(
                installationReference.Name,
                out PackageReference selectedReference)
                ? selectedReference
                : (PackageReference?)null;
    }

    /// <summary>
    /// Determines whether an existing lock file covers all dependencies in the manifest.
    /// </summary>
    private static bool IsLockFileCurrent(
        PackageManifest manifest,
        PackageLockFile lockFile,
        ResolvedPackageInstallPolicy policy,
        PackageFixupPolicy fixupPolicy)
    {
        if (lockFile.SchemaVersion
                != PackageLockFile.CurrentSchemaVersion
            || !RootPackageMatches(
                manifest,
                lockFile.RootPackage)
            || lockFile.Roots is null
            || lockFile.Dependencies is null
            || lockFile.InstallOrder is null
            || lockFile.Policy is null
            || lockFile.Missing is { Count: > 0 }
            || lockFile.Failures is null
            || lockFile.Failures.Count > 0
            || lockFile.Policy.ConflictStrategy
                != policy.ConflictStrategy
            || lockFile.Policy.AllowPreRelease
                != policy.AllowPreRelease
            || lockFile.Policy.PreferredFhirRelease
                != policy.PreferredFhirRelease
            || lockFile.Policy.MaxDepth != policy.MaxDepth
            || !string.Equals(
                lockFile.Policy.VersionFixupHash,
                fixupPolicy.IdentityHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyDictionary<string, string?> expectedRoots =
            CreateRootMap(manifest.Dependencies);
        if (lockFile.Roots.Count != expectedRoots.Count)
            return false;

        Dictionary<string, string?> lockedRoots =
            new(StringComparer.OrdinalIgnoreCase);
        List<PackageDirective> lockedRootDirectives = [];
        try
        {
            foreach (string root in lockFile.Roots)
            {
                PackageDirective directive =
                    PackageDirective.Parse(root);
                lockedRootDirectives.Add(directive);
                if (!lockedRoots.TryAdd(
                        directive.PackageId,
                        directive.RequestedVersion))
                {
                    return false;
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        if (lockedRoots.Count != expectedRoots.Count)
            return false;

        foreach (KeyValuePair<string, string?> root in expectedRoots)
        {
            if (!lockedRoots.TryGetValue(
                    root.Key,
                    out string? lockedVersion)
                || !string.Equals(
                    root.Value,
                    lockedVersion,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (policy.ConflictStrategy
                == ConflictResolutionStrategy.FirstWins
            && !RootOrderMatches(
                expectedRoots,
                lockedRootDirectives))
        {
            return false;
        }

        return HasValidLockedDependencies(
            lockedRootDirectives,
            lockFile.Dependencies,
            lockFile.InstallOrder,
            fixupPolicy,
            lockFile.Policy.AllowPreRelease,
            lockFile.Policy.ConflictStrategy);
    }

    private static string ResolveLockFilePath(
        string fullProjectPath,
        string? configuredPath)
    {
        string resolvedPath;
        if (configuredPath is null)
        {
            resolvedPath =
                Path.Combine(
                    fullProjectPath,
                    LockFileName);
        }
        else if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException(
                "LockFilePath cannot be empty.",
                nameof(configuredPath));
        }
        else if (Path.IsPathFullyQualified(configuredPath))
        {
            resolvedPath =
                Path.GetFullPath(configuredPath);
        }
        else if (Path.IsPathRooted(configuredPath))
        {
            throw new ArgumentException(
                "LockFilePath must be fully qualified or relative to the project directory.",
                nameof(configuredPath));
        }
        else
        {
            resolvedPath = Path.GetFullPath(
                Path.Combine(
                    fullProjectPath,
                    configuredPath));
        }

        string coordinationPath = Path.Combine(
            Path.GetDirectoryName(resolvedPath)
                ?? throw new ArgumentException(
                    "LockFilePath must have a parent directory.",
                    nameof(configuredPath)),
            ".fhirpkg-restore.lock");
        if (string.Equals(
                resolvedPath,
                coordinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "LockFilePath cannot use the reserved '.fhirpkg-restore.lock' coordination filename.",
                nameof(configuredPath));
        }

        return resolvedPath;
    }

    private static Task<PackageCacheLease>
        AcquireLockFileLeaseAsync(
            string lockFilePath,
            CancellationToken cancellationToken)
    {
        string directoryPath =
            Path.GetDirectoryName(lockFilePath)
            ?? throw new InvalidOperationException(
                "The lock file path does not have a parent directory.");
        PackageCacheCoordinator coordinator =
            new(directoryPath);
        return coordinator.AcquireFileMutationAsync(
            lockFilePath,
            cancellationToken);
    }

    private static bool HasValidLockedDependencies(
        IReadOnlyList<PackageDirective> roots,
        IReadOnlyDictionary<string, string> dependencies,
        IReadOnlyList<string> installOrder,
        PackageFixupPolicy fixupPolicy,
        bool allowPreRelease,
        ConflictResolutionStrategy conflictStrategy)
    {
        Dictionary<string, string> dependencyPins =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (KeyValuePair<string, string> dependency
                     in dependencies)
            {
                if (!dependencyPins.TryAdd(
                        dependency.Key,
                        dependency.Value))
                    return false;

                if (!FhirSemVer.TryParse(
                        dependency.Value,
                        out FhirSemVer? parsedVersion)
                    || parsedVersion.IsWildcard
                    || (!allowPreRelease
                        && parsedVersion.IsPreRelease))
                {
                    return false;
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        HashSet<string> installedNames =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string directiveText in installOrder)
            {
                PackageDirective directive =
                    PackageDirective.Parse(
                        directiveText);
                if (!installedNames.Add(
                        directive.PackageId)
                    || !dependencyPins.TryGetValue(
                        directive.PackageId,
                        out string? exactVersion))
                {
                    return false;
                }

                if (directive.VersionType == VersionType.Exact)
                {
                    if (!string.Equals(
                            directive.RequestedVersion,
                            exactVersion,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                else if (directive.VersionType
                    is not VersionType.CiBuild
                    and not VersionType.CiBuildBranch
                    and not VersionType.LocalBuild)
                {
                    return false;
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        if (installedNames.Count != dependencyPins.Count)
            return false;
        if (roots.Count == 0)
            return dependencyPins.Count == 0;

        bool hasEarlierEffectiveRoot = false;
        foreach (PackageDirective root in roots)
        {
            if (root.ExpandedPackageIds is { Count: > 0 })
            {
                foreach (string candidate
                         in root.ExpandedPackageIds)
                {
                    if (!LockedRootIsRepresented(
                            root,
                            candidate,
                            dependencyPins,
                            fixupPolicy,
                            conflictStrategy,
                            allowPreRelease,
                            hasEarlierEffectiveRoot))
                    {
                        return false;
                    }

                    hasEarlierEffectiveRoot = true;
                }

                continue;
            }

            if (!LockedRootIsRepresented(
                    root,
                    root.PackageId,
                    dependencyPins,
                    fixupPolicy,
                    conflictStrategy,
                    allowPreRelease,
                    hasEarlierEffectiveRoot))
            {
                return false;
            }

            hasEarlierEffectiveRoot = true;
        }

        return true;
    }

    private static bool RootPackageMatches(
        PackageManifest manifest,
        string? lockedRootPackage)
    {
        if (string.IsNullOrWhiteSpace(lockedRootPackage))
            return false;

        try
        {
            PackageReference lockedReference =
                PackageReference.Parse(lockedRootPackage);
            return lockedReference.Name.Equals(
                    manifest.Name,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    lockedReference.Version,
                    manifest.Version,
                    StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool RootOrderMatches(
        IReadOnlyDictionary<string, string?> expectedRoots,
        IReadOnlyList<PackageDirective> lockedRoots)
    {
        int index = 0;
        foreach (KeyValuePair<string, string?> expected
                 in expectedRoots)
        {
            PackageDirective locked = lockedRoots[index++];
            if (!locked.PackageId.Equals(
                    expected.Key,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    locked.RequestedVersion,
                    expected.Value,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LockedRootIsRepresented(
        PackageDirective root,
        string candidateName,
        IReadOnlyDictionary<string, string> dependencyPins,
        PackageFixupPolicy fixupPolicy,
        ConflictResolutionStrategy conflictStrategy,
        bool allowPreRelease,
        bool allowFirstWinsSupersession)
    {
        PackageReference requestedReference =
            PackageFixups.Apply(
                new PackageReference(
                    candidateName,
                    root.RequestedVersion),
                fixupPolicy);
        if (!dependencyPins.TryGetValue(
                requestedReference.Name,
                out string? lockedVersion))
        {
            return false;
        }

        if (!FhirSemVer.TryParse(
                lockedVersion,
                out FhirSemVer? lockedSemanticVersion)
            || lockedSemanticVersion.IsWildcard)
        {
            return false;
        }

        if (root.VersionType == VersionType.Exact
            && requestedReference.Version is string exactVersion)
        {
            if (!FhirSemVer.TryParse(
                    exactVersion,
                    out FhirSemVer? requestedSemanticVersion)
                || requestedSemanticVersion.IsWildcard
                || (!allowPreRelease
                    && requestedSemanticVersion.IsPreRelease))
            {
                return false;
            }

            return string.Equals(
                    exactVersion,
                    lockedVersion,
                    StringComparison.Ordinal)
                || (conflictStrategy
                        == ConflictResolutionStrategy.HighestWins
                    && lockedSemanticVersion
                        > requestedSemanticVersion)
                || (conflictStrategy
                        == ConflictResolutionStrategy.FirstWins
                    && allowFirstWinsSupersession);
        }

        if (root.VersionType
                is VersionType.Range
                    or VersionType.Wildcard
            && root.RequestedVersion is string rangeExpression
            && FhirSemVerRange.TryParse(
                rangeExpression,
                out FhirSemVerRange? range))
        {
            return range.IsSatisfiedBy(
                    lockedSemanticVersion)
                || (conflictStrategy
                        == ConflictResolutionStrategy.HighestWins
                    && range.HasSatisfyingVersionAtOrBelow(
                        lockedSemanticVersion,
                        allowPreRelease))
                || (conflictStrategy
                        == ConflictResolutionStrategy.FirstWins
                    && allowFirstWinsSupersession);
        }

        return true;
    }

    private static PackageLockFile CreateLockFile(
        PackageManifest manifest,
        PackageClosure closure,
        ResolvedPackageInstallPolicy policy,
        PackageFixupPolicy fixupPolicy) =>
        new()
        {
            SchemaVersion =
                PackageLockFile.CurrentSchemaVersion,
            Updated = closure.Timestamp,
            RootPackage = new PackageReference(
                    manifest.Name,
                    manifest.Version)
                .FhirDirective,
            Roots = CreateRootDirectives(
                manifest.Dependencies),
            Policy = new PackageLockPolicy
            {
                ConflictStrategy =
                    policy.ConflictStrategy,
                AllowPreRelease =
                    policy.AllowPreRelease,
                PreferredFhirRelease =
                    policy.PreferredFhirRelease,
                MaxDepth = policy.MaxDepth,
                VersionFixupHash =
                    fixupPolicy.IdentityHash,
            },
            Dependencies = closure.Resolved
                .OrderBy(
                    pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    pair => pair.Key,
                    StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Version
                        ?? throw new InvalidOperationException(
                            $"Resolved package '{pair.Key}' did not contain an exact version."),
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = CreateLockInstallOrder(
                closure),
            Missing = null,
            Failures = [],
        };

    private static IReadOnlyList<string> CreateLockInstallOrder(
        PackageClosure closure)
    {
        List<PackageReference> order = [];
        HashSet<string> included =
            new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PackageReference> preferredOrder =
            closure.ReplayOrder.Count > 0
                ? closure.ReplayOrder
                : closure.InstallOrder;
        foreach (PackageReference reference in preferredOrder)
        {
            if (included.Add(reference.Name))
                order.Add(reference);
        }

        IEnumerable<PackageReference> missing =
            closure.Resolved.Values
                .Where(reference =>
                    !included.Contains(
                        reference.Name))
                .OrderBy(
                    reference => reference.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    reference => reference.Name,
                    StringComparer.Ordinal);
        order.AddRange(missing);
        return order.Select(
                reference => reference.FhirDirective)
            .ToArray();
    }

    private static IReadOnlyList<string> CreateRootDirectives(
        IReadOnlyDictionary<string, string>? dependencies) =>
        CreateRootMap(dependencies)
            .Select(
                pair => new PackageReference(
                    pair.Key,
                    pair.Value)
                .FhirDirective)
            .ToArray();

    private static IReadOnlyDictionary<string, string?>
        CreateRootMap(
            IReadOnlyDictionary<string, string>? dependencies)
    {
        Dictionary<string, string?> roots =
            new(StringComparer.OrdinalIgnoreCase);
        if (dependencies is null)
            return roots;

        foreach (KeyValuePair<string, string> dependency
                 in dependencies)
        {
            roots.Add(
                dependency.Key,
                dependency.Value);
        }

        return roots;
    }

    /// <summary>
    /// Extracts a <see cref="PackageReference"/> from a tarball by reading its manifest.
    /// </summary>
    private async Task<PackageReference> ExtractReferenceFromTarballAsync(
        FileStream tarballStream,
        CancellationToken cancellationToken)
    {
        long archiveLength = tarballStream.Length;
        if (archiveLength > _managerInstallLimits.MaxCompressedBytes)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.CompressedSizeLimitExceeded,
                PackageInstallStage.Acquisition,
                $"Package publish exceeds the configured compressed size limit of {_managerInstallLimits.MaxCompressedBytes} bytes.",
                "package publish");
        }

        // Create a temporary directory (prefer system temp, fall back to cache)
        string tempDir = TempDirectory.Create("fhirpkg", _cache.CacheDirectory);
        try
        {
            ArchiveExtractionMetrics metrics =
                await TarballExtractor.ExtractAsync(
                        tarballStream,
                        tempDir,
                        _managerInstallLimits,
                        "package publish",
                        cancellationToken)
                    .ConfigureAwait(false);
            PackageArchiveInventory inventory = metrics.Inventory
                ?? throw new PackageInstallException(
                    PackageInstallErrorCode.InvalidArchive,
                    PackageInstallStage.ArchiveValidation,
                    "Package archive inventory was not produced.",
                    "package publish");
            PackageArchiveLayoutResult layout =
                TarballExtractor.ValidateAndNormalizePackageStructure(
                    tempDir,
                    inventory,
                    "package publish");
            PackageIdentityValidationResult identity =
                await PackageIdentityValidator.DiscoverAsync(
                        layout.ManifestPath,
                        "package publish",
                        cancellationToken)
                    .ConfigureAwait(false);
            return identity.ManifestReference;
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up temp directory '{TempDir}'", tempDir); }
        }
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
