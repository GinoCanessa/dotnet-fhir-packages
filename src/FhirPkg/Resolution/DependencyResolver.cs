// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Utilities;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves the active transitive dependency closure for a FHIR package manifest.
/// Handles version-dependent graph replacement, circular dependencies, version
/// conflicts, depth limiting, and known package fixups.
/// </summary>
/// <remarks>
/// <para>
/// The resolver builds an active dependency graph from the root manifest's direct
/// dependencies. At each step:
/// </para>
/// <list type="number">
///   <item><description>Known package fixups (e.g. hl7.fhir.r4.core@4.0.0 → 4.0.1) are applied.</description></item>
///   <item><description>Version specifiers are resolved to exact versions via <see cref="IVersionResolver"/>.</description></item>
///   <item><description>Version conflicts are handled according to the configured <see cref="ConflictResolutionStrategy"/>.</description></item>
///   <item><description>The selected version's exact child edges replace any losing version's child edges.</description></item>
///   <item><description>Shared dependency nodes retain every active parent edge.</description></item>
///   <item><description>Resolution depth is bounded by <see cref="DependencyResolveOptions.MaxDepth"/> and truncation is reported.</description></item>
/// </list>
/// </remarks>
public class DependencyResolver : IDependencyResolver
{
    private static readonly IReadOnlyDictionary<string, string> s_noDependencies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IRegistryClient _registryClient;
    private readonly IVersionResolver _versionResolver;
    private readonly IPackageCache _cache;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PackageFixupPolicy _fixupPolicy;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyResolver"/> class.
    /// </summary>
    /// <param name="registryClient">
    /// The registry client used to query package metadata and transitive dependencies.
    /// May be a <see cref="RedundantRegistryClient"/> wrapping multiple registries.
    /// </param>
    /// <param name="versionResolver">Resolver for converting version specifiers to exact versions.</param>
    /// <param name="cache">The local package cache for reading manifests of already-cached packages.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
    public DependencyResolver(
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IPackageCache cache,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : this(
            registryClient,
            versionResolver,
            cache,
            logger,
            PackageFixupPolicy.Default,
            timeProvider)
    {
    }

    internal DependencyResolver(
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IPackageCache cache,
        ILogger logger,
        PackageFixupPolicy fixupPolicy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fixupPolicy);

        _registryClient = registryClient;
        _versionResolver = versionResolver;
        _cache = cache;
        _logger = logger;
        _fixupPolicy = fixupPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootManifest);

        options ??= new DependencyResolveOptions();
        if (options.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DependencyResolveOptions.MaxDepth),
                options.MaxDepth,
                "Maximum dependency depth cannot be negative.");
        }

        IReadOnlyDictionary<string, string> dependencies = rootManifest.Dependencies ?? new Dictionary<string, string>();
        PackageFixupPolicy fixupPolicy = options.FixupPolicy ?? _fixupPolicy;

        _logger.LogInformation(
            "Resolving dependencies for '{PackageName}@{PackageVersion}' ({DependencyCount} direct dependencies).",
            rootManifest.Name, rootManifest.Version, dependencies.Count);

        ActiveDependencyGraph graph = new(
            this,
            rootManifest,
            options,
            fixupPolicy);
        await graph.ResolveAsync(dependencies, cancellationToken)
            .ConfigureAwait(false);
        DependencyGraphResult result = graph.CreateResult();

        _logger.LogInformation(
            "Dependency resolution complete: {ResolvedCount} resolved, {FailureCount} failures.",
            result.Resolved.Count, result.Failures.Count);

        return new PackageClosure
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            Resolved = result.Resolved,
            Missing = CreateMissingProjection(result.Failures),
            Failures = result.Failures,
            InstallOrder = result.InstallOrder,
            BootstrapInstallOrder = result.BootstrapInstallOrder,
            InstallOrderIsComplete = true,
        };
    }

    /// <inheritdoc />
    public async Task<PackageClosure> RestoreFromLockFileAsync(
        PackageLockFile lockFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockFile);

        Dictionary<string, PackageReference> resolved = new Dictionary<string, PackageReference>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<DependencyResolutionFailure> failures = [];

        _logger.LogInformation(
            "Restoring {Count} locked dependencies from lock file (updated {Updated:O}).",
            lockFile.Dependencies.Count, lockFile.Updated);

        foreach ((string? packageId, string? version) in lockFile.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PackageReference reference = new PackageReference(packageId, version);

            // Check if the package is already in the local cache
            bool isCached = await _cache.IsInstalledAsync(reference, cancellationToken).ConfigureAwait(false);
            if (isCached)
            {
                _logger.LogDebug("Package '{PackageId}@{Version}' found in cache.", packageId, version);
                resolved[packageId] = reference;
                continue;
            }

            // Not cached — try to resolve and mark for download by the orchestrator
            FhirSemVer? resolvedVersion = await _versionResolver.ResolveVersionAsync(
                packageId, version, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (resolvedVersion is not null)
            {
                PackageReference resolvedRef = new PackageReference(packageId, resolvedVersion.ToString());
                resolved[packageId] = resolvedRef;

                _logger.LogDebug(
                    "Locked package '{PackageId}@{Version}' resolved to '{ResolvedVersion}' (not cached, needs download).",
                    packageId, version, resolvedVersion);
            }
            else
            {
                string message = $"Locked version '{version}' could not be resolved from any registry.";
                missing[packageId] = message;
                failures.Add(new DependencyResolutionFailure
                {
                    Code = DependencyResolutionFailureCode.PackageNotFound,
                    PackageId = packageId,
                    VersionSpecifier = version,
                    Message = message,
                });
                _logger.LogWarning(
                    "Locked package '{PackageId}@{Version}' could not be resolved from any configured registry.",
                    packageId, version);
            }
        }

        // Also include any packages that were missing in the original lock file
        if (lockFile.Missing is not null)
        {
            foreach ((string? packageId, string? versionConstraint) in lockFile.Missing)
            {
                if (!resolved.ContainsKey(packageId) && !missing.ContainsKey(packageId))
                {
                    string message = $"Previously missing in lock file: '{versionConstraint}'";
                    missing[packageId] = message;
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code = DependencyResolutionFailureCode.PackageNotFound,
                        PackageId = packageId,
                        VersionSpecifier = versionConstraint,
                        Message = message,
                    });
                }
            }

        }

        _logger.LogInformation(
            "Lock file restore complete: {ResolvedCount} resolved, {MissingCount} missing.",
            resolved.Count, missing.Count);

        return new PackageClosure
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            Resolved = resolved,
            Missing = missing,
            Failures = failures,
            InstallOrder = resolved.Values
                .OrderBy(
                    reference => reference.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    reference => reference.Version,
                    StringComparer.Ordinal)
                .ToArray(),
            InstallOrderIsComplete = true,
        };
    }

    private static IReadOnlyDictionary<string, string> CreateMissingProjection(
        IEnumerable<DependencyResolutionFailure> failures)
    {
        Dictionary<string, List<string>> messages =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (DependencyResolutionFailure failure in failures)
        {
            if (!messages.TryGetValue(
                    failure.PackageId,
                    out List<string>? packageMessages))
            {
                packageMessages = [];
                messages.Add(failure.PackageId, packageMessages);
            }

            if (!packageMessages.Contains(
                    failure.Message,
                    StringComparer.Ordinal))
            {
                packageMessages.Add(failure.Message);
            }
        }

        return messages.ToDictionary(
            pair => pair.Key,
            pair => string.Join(" ", pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DependencyMetadataResult> GetTransitiveDependenciesAsync(
        PackageReference reference,
        VersionResolveOptions versionResolveOptions,
        CancellationToken cancellationToken)
    {
        PackageListing? listing = null;
        IReadOnlyList<RegistryAttemptFailure> registryFailures = [];
        try
        {
            listing = await _registryClient.GetPackageListingAsync(
                    reference.Name,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RegistryOperationException exception)
        {
            registryFailures = exception.Failures;
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or TimeoutException
                or JsonException
                or InvalidDataException
                or FormatException)
        {
            registryFailures =
            [
                RegistryAttemptFailure.Capture(
                    _registryClient.Endpoint,
                    exception)
            ];
        }

        PackageVersionInfo? versionInfo = null;
        IReadOnlyDictionary<string, string>? dependencies = null;
        if (listing is not null && reference.Version is not null)
        {
            versionInfo = PackageVersionSelector.SelectExactSourceCandidate(
                reference.Name,
                reference.Version,
                listing.VersionCandidates,
                versionResolveOptions);
            if (versionInfo is null
                && listing.VersionCandidates.Count == 0)
            {
                versionInfo = listing.Versions
                    .Where(pair => pair.Key.Equals(
                        reference.Version,
                        StringComparison.Ordinal))
                    .Select(pair => pair.Value)
                    .FirstOrDefault();
            }

            dependencies = versionInfo?.Dependencies;
        }

        if (dependencies is null)
        {
            PackageManifest? manifest = await _cache.ReadManifestAsync(
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);
            if (manifest is not null)
            {
                dependencies =
                    manifest.Dependencies ?? s_noDependencies;
            }
        }

        if (dependencies is null && versionInfo is not null)
        {
            dependencies = s_noDependencies;
        }

        IReadOnlyList<RegistryAttemptFailure> incompleteFailures =
            registryFailures;
        if (incompleteFailures.Count == 0
            && listing is { IsComplete: false })
        {
            incompleteFailures =
                listing.QueryFailures.Count > 0
                    ? listing.QueryFailures
                    :
                    [
                        new RegistryAttemptFailure(
                            null,
                            RegistryFailureCategory.Unexpected)
                    ];
        }

        if (dependencies is not null)
        {
            return incompleteFailures.Count > 0
                ? DependencyMetadataResult.Partial(
                    dependencies,
                    $"The registry listing for '{reference.FhirDirective}' was incomplete, so its dependency metadata could not be proven authoritative.",
                    incompleteFailures)
                : DependencyMetadataResult.Available(dependencies);
        }

        if (incompleteFailures.Count > 0)
        {
            return DependencyMetadataResult.Unavailable(
                $"The registry listing for '{reference.FhirDirective}' was incomplete and contained no authoritative metadata for the selected version.",
                incompleteFailures);
        }

        return DependencyMetadataResult.Unavailable(
            $"Dependency metadata for '{reference.FhirDirective}' could not be found.",
            []);
    }

    private sealed class ActiveDependencyGraph
    {
        private readonly DependencyResolver _owner;
        private readonly PackageManifest _rootManifest;
        private readonly PackageReference _rootReference;
        private readonly DependencyResolveOptions _options;
        private readonly PackageFixupPolicy _fixupPolicy;
        private readonly VersionResolveOptions _versionResolveOptions;
        private readonly Dictionary<string, DependencyNode> _nodes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, DependencyEdge> _edges = [];
        private readonly Dictionary<long, InvalidDependencyEdge>
            _invalidEdges = [];
        private readonly Dictionary<long, RootBackEdge>
            _rootBackEdges = [];
        private readonly Queue<string> _pendingPackages = [];
        private readonly HashSet<string> _queuedPackages =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, DependencyVersionResolution>>
            _versionResolutions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, DependencyMetadataResult>>
            _metadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenStates =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _unstablePackageIds =
            new(StringComparer.OrdinalIgnoreCase);
        private long _nextEdgeId;

        internal ActiveDependencyGraph(
            DependencyResolver owner,
            PackageManifest rootManifest,
            DependencyResolveOptions options,
            PackageFixupPolicy fixupPolicy)
        {
            _owner = owner;
            _rootManifest = rootManifest;
            _rootReference = options.RootReference
                ?? new PackageReference(
                    rootManifest.Name,
                    rootManifest.Version);
            _options = options;
            _fixupPolicy = fixupPolicy;
            _versionResolveOptions = new VersionResolveOptions
            {
                AllowPreRelease = options.AllowPreRelease,
                FhirRelease = options.PreferredFhirRelease,
            };
        }

        internal async Task ResolveAsync(
            IReadOnlyDictionary<string, string> rootDependencies,
            CancellationToken cancellationToken)
        {
            int dependencyIndex = 0;
            foreach (KeyValuePair<string, string> dependency in rootDependencies)
            {
                AddEdge(
                    _rootManifest.Name,
                    _rootManifest.Version,
                    dependency.Key,
                    dependency.Value,
                    depth: 0,
                    [dependencyIndex],
                    isRootEdge: true);
                dependencyIndex++;
            }

            _seenStates.Add(CreateStateSignature());
            while (_pendingPackages.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string packageId = _pendingPackages.Dequeue();
                _queuedPackages.Remove(packageId);
                await RecomputeNodeAsync(
                        _nodes[packageId],
                        cancellationToken)
                    .ConfigureAwait(false);

                string stateSignature = CreateStateSignature();
                if (!_seenStates.Add(stateSignature))
                {
                    DisableUnstablePackage(packageId);
                    _seenStates.Clear();
                    _seenStates.Add(CreateStateSignature());
                }
            }

            foreach (RootBackEdge edge in _rootBackEdges.Values)
            {
                if (edge.Depth <= _options.MaxDepth
                    && PackageDirective.ClassifyVersion(
                        edge.VersionSpecifier) == VersionType.Latest)
                {
                    edge.Resolution = await ResolveVersionAsync(
                            edge.PackageId,
                            edge.VersionSpecifier,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        internal DependencyGraphResult CreateResult()
        {
            Dictionary<string, PackageReference> resolved =
                new(StringComparer.OrdinalIgnoreCase);
            List<DependencyResolutionFailure> failures = [];

            foreach (DependencyNode node in _nodes.Values)
            {
                if (node.SelectedReference is PackageReference selected)
                {
                    resolved[node.PackageId] = selected;
                }
            }

            List<DependencyEdge> orderedEdges = _edges.Values.ToList();
            orderedEdges.Sort(CompareEdges);
            IEnumerable<InvalidDependencyEdge> invalidEdges =
                _invalidEdges
                    .Values
                    .OrderBy(
                        edge => edge.TraversalPath,
                        DependencyPathComparer.Instance);
            foreach (InvalidDependencyEdge edge in invalidEdges)
            {
                failures.Add(new DependencyResolutionFailure
                {
                    Code =
                        DependencyResolutionFailureCode.InvalidDirective,
                    PackageId = edge.PackageId,
                    VersionSpecifier = edge.VersionSpecifier,
                    ParentPackageId = edge.ParentPackageId,
                    ParentVersion = edge.ParentVersion,
                    Depth = edge.Depth,
                    Message =
                        $"Dependency '{edge.PackageId}@{edge.VersionSpecifier}' is not a valid package directive.",
                });
            }

            IEnumerable<RootBackEdge> rootBackEdges =
                _rootBackEdges.Values
                    .OrderBy(
                        edge => edge.TraversalPath,
                        DependencyPathComparer.Instance);
            foreach (RootBackEdge edge in rootBackEdges)
            {
                if (edge.Depth > _options.MaxDepth)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code =
                            DependencyResolutionFailureCode.DepthLimitExceeded,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        MaxDepth = _options.MaxDepth,
                        Message =
                            $"Dependency '{edge.PackageId}@{edge.VersionSpecifier}' at depth {edge.Depth} exceeds the maximum depth of {_options.MaxDepth}.",
                    });
                }
                else if (edge.Resolution is
                    {
                        RegistryFailures.Count: > 0
                    } resolution)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code =
                            DependencyResolutionFailureCode.RegistryUnavailable,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        SelectedVersion = _rootManifest.Version,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        RegistryFailures =
                            resolution.RegistryFailures,
                        Message =
                            $"Registry failures prevented root dependency '{edge.PackageId}@{edge.VersionSpecifier}' from being verified against the committed root.",
                    });
                }
                else if (edge.Resolution is
                    {
                        Version: null
                    })
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code =
                            DependencyResolutionFailureCode.PackageNotFound,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        SelectedVersion = _rootManifest.Version,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        Message =
                            $"Root dependency '{edge.PackageId}@{edge.VersionSpecifier}' could not be resolved authoritatively.",
                    });
                }
                else if (!IsRootBackEdgeSatisfied(edge))
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code =
                            DependencyResolutionFailureCode.VersionConflict,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        SelectedVersion = _rootManifest.Version,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        RequestedVersions =
                            [_rootManifest.Version, edge.VersionSpecifier],
                        Message =
                            $"Dependency '{edge.ParentPackageId}@{edge.ParentVersion}' requires root package '{edge.PackageId}@{edge.VersionSpecifier}', but the committed root manifest is '{_rootManifest.Name}#{_rootManifest.Version}'.",
                    });
                }
            }

            foreach (DependencyEdge edge in orderedEdges)
            {
                if (edge.Depth > _options.MaxDepth)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code = DependencyResolutionFailureCode.DepthLimitExceeded,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        MaxDepth = _options.MaxDepth,
                        Message =
                            $"Dependency '{edge.PackageId}@{edge.VersionSpecifier}' at depth {edge.Depth} exceeds the maximum depth of {_options.MaxDepth}.",
                    });
                    continue;
                }

                DependencyVersionResolution resolution =
                    edge.Resolution
                    ?? throw new InvalidOperationException(
                        $"Active dependency edge '{edge.PackageId}@{edge.VersionSpecifier}' was not evaluated.");
                if (resolution.BootstrapRequired)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code =
                            DependencyResolutionFailureCode.MetadataUnavailable,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        SelectedVersion =
                            resolution.Version?.ToString(),
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        Message =
                            $"Dependency '{edge.PackageId}@{edge.VersionSpecifier}' must be installed before its exact manifest identity and dependencies can be proven.",
                    });
                }
                else if (resolution.RegistryFailures.Count > 0)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code = DependencyResolutionFailureCode.RegistryUnavailable,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        RegistryFailures = resolution.RegistryFailures,
                        Message =
                            $"Registry failures prevented version '{edge.VersionSpecifier}' from being resolved.",
                    });
                }
                else if (resolution.Version is null)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code = resolution.InstallationReference.HasValue
                            ? DependencyResolutionFailureCode.MetadataUnavailable
                            : DependencyResolutionFailureCode.PackageNotFound,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        Message =
                            resolution.InstallationReference.HasValue
                                ? $"Dependency '{edge.PackageId}@{edge.VersionSpecifier}' must be installed before its exact manifest identity can be resolved."
                                : $"Could not resolve version '{edge.VersionSpecifier}'.",
                    });
                }
            }

            foreach (DependencyNode node in _nodes.Values)
            {
                if (_options.ConflictStrategy
                        == ConflictResolutionStrategy.Error)
                {
                    IReadOnlyList<string> versions =
                        GetDistinctResolvedVersions(node);
                    if (versions.Count > 1)
                    {
                        failures.Add(new DependencyResolutionFailure
                        {
                            Code = DependencyResolutionFailureCode.VersionConflict,
                            PackageId = node.PackageId,
                            SelectedVersion =
                                node.SelectedReference?.Version,
                            RequestedVersions = versions,
                            Message =
                                $"Version conflict among active requirements: {string.Join(", ", versions.Select(version => $"'{version}'"))}.",
                        });
                    }
                }

                if (node.SelectedReference is PackageReference selected
                    && node.Metadata is
                    {
                        IsComplete: false
                    } metadata)
                {
                    failures.Add(new DependencyResolutionFailure
                    {
                        Code = DependencyResolutionFailureCode.MetadataUnavailable,
                        PackageId = node.PackageId,
                        SelectedVersion = selected.Version,
                        RegistryFailures = metadata.RegistryFailures,
                        Message = metadata.Message,
                    });
                }
            }

            foreach (string packageId in _unstablePackageIds)
            {
                _nodes.TryGetValue(
                    packageId,
                    out DependencyNode? unstableNode);
                failures.Add(new DependencyResolutionFailure
                {
                    Code =
                        DependencyResolutionFailureCode.UnstableResolution,
                    PackageId = packageId,
                    SelectedVersion =
                        unstableNode?.SelectedReference?.Version,
                    Message =
                        $"Dependency resolution for '{packageId}' repeated a prior active graph state and could not reach a stable closure.",
                });
            }

            return new DependencyGraphResult(
                resolved,
                failures,
                CreateInstallOrder(),
                CreateBootstrapInstallOrder());
        }

        private async Task RecomputeNodeAsync(
            DependencyNode node,
            CancellationToken cancellationToken)
        {
            if (_unstablePackageIds.Contains(node.PackageId))
            {
                RemoveOutgoingEdges(node);
                node.SelectedReference = null;
                node.MetadataReference = null;
                node.InstallationReference = null;
                node.RequiresInstallation = true;
                node.BootstrapRequired = false;
                node.ActiveDepth = null;
                node.ActivePath = null;
                node.Metadata = null;
                return;
            }

            List<DependencyEdge> orderedEdges =
                node.IncomingEdges.Values.ToList();
            orderedEdges.Sort(CompareEdges);

            List<ResolvedDependencyRequirement> requirements = [];
            foreach (DependencyEdge edge in orderedEdges)
            {
                if (edge.Depth > _options.MaxDepth)
                    continue;

                DependencyVersionResolution resolution =
                    await ResolveVersionAsync(
                            edge.PackageId,
                            edge.VersionSpecifier,
                            cancellationToken)
                        .ConfigureAwait(false);
                edge.Resolution = resolution;
                if (resolution.Version is FhirSemVer version)
                {
                    requirements.Add(
                        new ResolvedDependencyRequirement(
                            edge,
                            version,
                            resolution.InstallationReference,
                            resolution.MetadataReference,
                            resolution.RequiresInstallation,
                            resolution.BootstrapRequired));
                }
            }

            PackageReference? selectedReference = null;
            PackageReference? metadataReference = null;
            PackageReference? installationReference = null;
            bool requiresInstallation = true;
            bool bootstrapRequired = false;
            int? activeDepth = null;
            int[]? activePath = null;
            if (requirements.Count > 0)
            {
                ResolvedDependencyRequirement winner =
                    SelectWinner(requirements);
                selectedReference = new PackageReference(
                    node.PackageId,
                    winner.Version.ToString());
                List<ResolvedDependencyRequirement>
                    selectedRequirements = requirements
                        .Where(
                            requirement => string.Equals(
                                requirement.Version.ToString(),
                                winner.Version.ToString(),
                                StringComparison.Ordinal))
                        .ToList();
                ResolvedDependencyRequirement source =
                    selectedRequirements.FirstOrDefault(
                        requirement =>
                            !requirement.BootstrapRequired)
                    ?? selectedRequirements.FirstOrDefault(
                        requirement =>
                            !requirement.RequiresInstallation)
                    ?? winner;
                installationReference =
                    source.InstallationReference
                    ?? selectedReference;
                requiresInstallation =
                    selectedRequirements.All(
                        requirement =>
                            requirement.RequiresInstallation);
                bootstrapRequired =
                    selectedRequirements.All(
                        requirement =>
                            requirement.BootstrapRequired);
                metadataReference =
                    source.MetadataReference
                    ?? selectedReference;
                ResolvedDependencyRequirement activeRoute =
                    SelectActiveRoute(requirements);
                activeDepth = activeRoute.Edge.Depth;
                activePath = activeRoute.Edge.TraversalPath;
            }

            bool selectionChanged =
                !SameReference(
                    node.SelectedReference,
                    selectedReference)
                || !SameReference(
                    node.MetadataReference,
                    metadataReference)
                || !SameReference(
                    node.InstallationReference,
                    installationReference)
                || node.RequiresInstallation != requiresInstallation
                || node.BootstrapRequired != bootstrapRequired
                || node.ActiveDepth != activeDepth
                || !SamePath(node.ActivePath, activePath);
            if (!selectionChanged)
                return;

            RemoveOutgoingEdges(node);
            node.SelectedReference = selectedReference;
            node.MetadataReference = metadataReference;
            node.InstallationReference = installationReference;
            node.RequiresInstallation = requiresInstallation;
            node.BootstrapRequired = bootstrapRequired;
            node.ActiveDepth = activeDepth;
            node.ActivePath = activePath;
            node.Metadata = null;

            if (selectedReference is not PackageReference selected
                || metadataReference is not PackageReference metadataSource
                || activeDepth is null
                || activePath is null)
            {
                return;
            }

            if (bootstrapRequired)
                return;

            DependencyMetadataResult metadata =
                await GetMetadataAsync(
                        metadataSource,
                        cancellationToken)
                    .ConfigureAwait(false);
            node.Metadata = metadata;
            if (!metadata.CanTraverse)
                return;

            int dependencyIndex = 0;
            foreach (KeyValuePair<string, string> dependency
                     in metadata.Dependencies)
            {
                DependencyEdgeRegistration registration = AddEdge(
                    node.PackageId,
                    selected.Version,
                    dependency.Key,
                    dependency.Value,
                    activeDepth.Value + 1,
                    AppendPath(activePath, dependencyIndex));
                if (registration.Kind
                    == DependencyEdgeRegistrationKind.Graph)
                {
                    node.OutgoingEdgeIds.Add(registration.Id);
                }
                else if (registration.Kind
                    == DependencyEdgeRegistrationKind.Invalid)
                {
                    node.OutgoingInvalidEdgeIds.Add(
                        registration.Id);
                }
                else
                {
                    node.OutgoingRootBackEdgeIds.Add(
                        registration.Id);
                }
                dependencyIndex++;
            }
        }

        private ResolvedDependencyRequirement SelectWinner(
            IReadOnlyList<ResolvedDependencyRequirement> requirements)
        {
            if (_options.ConflictStrategy
                is ConflictResolutionStrategy.FirstWins
                or ConflictResolutionStrategy.Error)
            {
                return requirements[0];
            }

            ResolvedDependencyRequirement winner = requirements[0];
            for (int index = 1; index < requirements.Count; index++)
            {
                ResolvedDependencyRequirement candidate =
                    requirements[index];
                if (candidate.Version.CompareTo(winner.Version) > 0)
                {
                    winner = candidate;
                }
            }

            return winner;
        }

        private static ResolvedDependencyRequirement SelectActiveRoute(
            IReadOnlyList<ResolvedDependencyRequirement> requirements)
        {
            ResolvedDependencyRequirement route = requirements[0];
            for (int index = 1; index < requirements.Count; index++)
            {
                ResolvedDependencyRequirement candidate =
                    requirements[index];
                if (candidate.Edge.Depth < route.Edge.Depth
                    || candidate.Edge.Depth == route.Edge.Depth
                    && ComparePaths(
                        candidate.Edge.TraversalPath,
                        route.Edge.TraversalPath) < 0)
                {
                    route = candidate;
                }
            }

            return route;
        }

        private async Task<DependencyVersionResolution> ResolveVersionAsync(
            string packageId,
            string versionSpecifier,
            CancellationToken cancellationToken)
        {
            if (!_versionResolutions.TryGetValue(
                    packageId,
                    out Dictionary<string, DependencyVersionResolution>?
                        packageResolutions))
            {
                packageResolutions = new Dictionary<string, DependencyVersionResolution>(
                    StringComparer.Ordinal);
                _versionResolutions.Add(packageId, packageResolutions);
            }

            if (packageResolutions.TryGetValue(
                    versionSpecifier,
                    out DependencyVersionResolution? cached))
            {
                return cached;
            }

            VersionType versionType =
                PackageDirective.ClassifyVersion(versionSpecifier);
            bool allowCachedFallback =
                !_options.InstallCachedPackages
                || (_options.PreferCachedAliases
                    && (versionType
                        is VersionType.CiBuild
                            or VersionType.CiBuildBranch));
            DependencyVersionResolution resolution;
            try
            {
                if (versionType
                    is VersionType.CiBuild
                        or VersionType.CiBuildBranch
                        or VersionType.LocalBuild)
                {
                    if (versionType == VersionType.LocalBuild)
                    {
                        resolution =
                            await TryResolveCachedDependencyAsync(
                                    packageId,
                                    versionSpecifier,
                                    cancellationToken)
                                .ConfigureAwait(false)
                            ?? await ResolveAliasDependencyAsync(
                                    packageId,
                                    versionSpecifier,
                                    versionType,
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }
                    else
                    {
                        DependencyVersionResolution onlineResolution =
                            await ResolveAliasDependencyAsync(
                                packageId,
                                versionSpecifier,
                                versionType,
                                cancellationToken)
                            .ConfigureAwait(false);
                        resolution =
                            (onlineResolution.Version is not null
                                && !(_options.PreferCachedAliases
                                    && onlineResolution.BootstrapRequired))
                            || ((_options.InstallCachedPackages
                                    || onlineResolution.InstallationReference.HasValue)
                                && !_options.PreferCachedAliases)
                                ? onlineResolution
                                : await TryResolveCachedDependencyAsync(
                                        packageId,
                                        versionSpecifier,
                                        cancellationToken)
                                    .ConfigureAwait(false)
                                    ?? onlineResolution;
                    }

                    packageResolutions.Add(
                        versionSpecifier,
                        resolution);
                    return resolution;
                }

                FhirSemVer? version =
                    await _owner._versionResolver.ResolveVersionAsync(
                            packageId,
                            versionSpecifier,
                            _versionResolveOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                resolution = version is null
                        ? (!allowCachedFallback
                            ? null
                            : await TryResolveCachedDependencyAsync(
                                    packageId,
                                    versionSpecifier,
                                    cancellationToken)
                                .ConfigureAwait(false))
                            ?? new DependencyVersionResolution(
                                null,
                                [],
                                null,
                                null,
                                RequiresInstallation: true)
                        : new DependencyVersionResolution(
                            version,
                            [],
                            new PackageReference(
                                packageId,
                                version.ToString()),
                            new PackageReference(
                                packageId,
                                version.ToString()),
                            RequiresInstallation: true);
            }
            catch (RegistryOperationException exception)
            {
                resolution =
                        (!allowCachedFallback
                            ? null
                            : await TryResolveCachedDependencyAsync(
                                    packageId,
                                    versionSpecifier,
                                    cancellationToken)
                                .ConfigureAwait(false))
                        ?? new DependencyVersionResolution(
                            null,
                            exception.Failures,
                            null,
                            null,
                            RequiresInstallation: true);
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or TimeoutException
                    or JsonException
                    or InvalidDataException
                    or FormatException)
            {
                resolution =
                    (!allowCachedFallback
                        ? null
                        : await TryResolveCachedDependencyAsync(
                                packageId,
                                versionSpecifier,
                                cancellationToken)
                            .ConfigureAwait(false))
                    ?? new DependencyVersionResolution(
                        null,
                        [
                            RegistryAttemptFailure.Capture(
                                _owner._registryClient.Endpoint,
                                exception)
                        ],
                        null,
                        null,
                        RequiresInstallation: true);
            }

            packageResolutions.Add(versionSpecifier, resolution);
            return resolution;
        }

        private async Task<DependencyVersionResolution>
            ResolveAliasDependencyAsync(
                string packageId,
                string versionSpecifier,
                VersionType versionType,
                CancellationToken cancellationToken)
        {
            if (versionType == VersionType.LocalBuild)
            {
                return new DependencyVersionResolution(
                    null,
                    [],
                    null,
                    null,
                    RequiresInstallation: true);
            }

            PackageDirective directive =
                PackageDirective.Parse(
                    new PackageReference(
                        packageId,
                        versionSpecifier)
                    .FhirDirective);
            ResolvedDirective? resolved =
                await _owner._registryClient.ResolveAsync(
                        directive,
                        _versionResolveOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (resolved is null
                || !resolved.Reference.Name.Equals(
                    packageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new DependencyVersionResolution(
                    null,
                    [],
                    null,
                    null,
                    RequiresInstallation: true);
            }

            if (!FhirSemVer.TryParse(
                    resolved.Reference.Version,
                    out FhirSemVer? version)
                || version.IsWildcard)
            {
                VersionType resolvedVersionType =
                    PackageDirective.ClassifyVersion(
                        resolved.Reference.Version);
                return new DependencyVersionResolution(
                    null,
                    [],
                    resolvedVersionType
                            is VersionType.CiBuild
                                or VersionType.CiBuildBranch
                        ? new PackageReference(
                            packageId,
                            versionSpecifier)
                        : (PackageReference?)null,
                    null,
                    RequiresInstallation: true,
                    BootstrapRequired: true);
            }

            PackageVersionInfo candidate = new()
            {
                Name = resolved.Reference.Name,
                Version = resolved.Reference.Version!,
                FhirVersion = resolved.FhirVersions?.FirstOrDefault(),
                FhirVersions = resolved.FhirVersions,
                HasExplicitFhirVersionMetadata =
                    resolved.FhirVersions is not null,
                Dependencies = resolved.Dependencies,
            };
            if (PackageVersionSelector.SelectExactSourceCandidate(
                    packageId,
                    resolved.Reference.Version!,
                    [candidate],
                    _versionResolveOptions) is null)
            {
                return new DependencyVersionResolution(
                    null,
                    [],
                    null,
                    null,
                    RequiresInstallation: true);
            }

            PackageReference exactReference =
                new(packageId, version.ToString());
            if (resolved.Dependencies is not null)
            {
                SeedMetadata(
                    exactReference,
                    DependencyMetadataResult.Available(
                        resolved.Dependencies));
            }

            return new DependencyVersionResolution(
                version,
                [],
                new PackageReference(
                    packageId,
                    versionSpecifier),
                exactReference,
                RequiresInstallation: true,
                BootstrapRequired:
                    resolved.Dependencies is null);
        }

        private async Task<DependencyVersionResolution?>
            TryResolveCachedDependencyAsync(
                string packageId,
                string versionSpecifier,
                CancellationToken cancellationToken)
        {
            VersionType versionType =
                PackageDirective.ClassifyVersion(versionSpecifier);
            if (versionType
                is not (
                    VersionType.Exact
                    or VersionType.CiBuild
                    or VersionType.CiBuildBranch
                    or VersionType.LocalBuild))
            {
                return null;
            }

            PackageReference cacheReference =
                new(packageId, versionSpecifier);
            PackageManifest? manifest =
                await _owner._cache.ReadManifestAsync(
                        cacheReference,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (manifest is null
                || !manifest.Name.Equals(
                    packageId,
                    StringComparison.OrdinalIgnoreCase)
                || versionType == VersionType.Exact
                && !manifest.Version.Equals(
                    versionSpecifier,
                    StringComparison.Ordinal)
                || !FhirSemVer.TryParse(
                    manifest.Version,
                    out FhirSemVer? version)
                || version.IsWildcard)
            {
                return null;
            }

            PackageVersionInfo candidate = new()
            {
                Name = manifest.Name,
                Version = manifest.Version,
                FhirVersion = manifest.FhirVersions?.FirstOrDefault(),
                FhirVersions = manifest.FhirVersions,
                HasExplicitFhirVersionMetadata =
                    manifest.FhirVersions is not null,
                Dependencies = manifest.Dependencies,
            };
            if (PackageVersionSelector.SelectExactSourceCandidate(
                    packageId,
                    manifest.Version,
                    [candidate],
                    _versionResolveOptions) is null)
            {
                return null;
            }

            SeedMetadata(
                cacheReference,
                DependencyMetadataResult.Available(
                    manifest.Dependencies
                    ?? s_noDependencies));
            return new DependencyVersionResolution(
                version,
                [],
                cacheReference,
                cacheReference,
                RequiresInstallation: false);
        }

        private void SeedMetadata(
            PackageReference reference,
            DependencyMetadataResult metadata)
        {
            string version = reference.Version
                ?? throw new InvalidOperationException(
                    "Selected dependency versions must be exact.");
            if (!_metadata.TryGetValue(
                    reference.Name,
                    out Dictionary<string, DependencyMetadataResult>?
                        packageMetadata))
            {
                packageMetadata = new Dictionary<string, DependencyMetadataResult>(
                    StringComparer.Ordinal);
                _metadata.Add(reference.Name, packageMetadata);
            }

            packageMetadata.TryAdd(version, metadata);
        }

        private async Task<DependencyMetadataResult> GetMetadataAsync(
            PackageReference reference,
            CancellationToken cancellationToken)
        {
            string version = reference.Version
                ?? throw new InvalidOperationException(
                    "Selected dependency versions must be exact.");
            if (!_metadata.TryGetValue(
                    reference.Name,
                    out Dictionary<string, DependencyMetadataResult>?
                        packageMetadata))
            {
                packageMetadata = new Dictionary<string, DependencyMetadataResult>(
                    StringComparer.Ordinal);
                _metadata.Add(reference.Name, packageMetadata);
            }

            if (packageMetadata.TryGetValue(
                    version,
                    out DependencyMetadataResult? cached))
            {
                return cached;
            }

            DependencyMetadataResult metadata =
                await _owner.GetTransitiveDependenciesAsync(
                        reference,
                        _versionResolveOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            packageMetadata.Add(version, metadata);
            return metadata;
        }

        private DependencyEdgeRegistration AddEdge(
            string parentPackageId,
            string? parentVersion,
            string rawPackageId,
            string? rawVersionSpecifier,
            int depth,
            int[] traversalPath,
            bool isRootEdge = false)
        {
            PackageReference fixedReference;
            try
            {
                fixedReference = PackageFixups.Apply(
                    new PackageReference(
                        rawPackageId,
                        rawVersionSpecifier),
                    _fixupPolicy);
                PackageCacheKey.ValidatePackageName(fixedReference);
                if (string.IsNullOrWhiteSpace(fixedReference.Version))
                {
                    throw new ArgumentException(
                        "A dependency edge must include a version specifier.",
                        nameof(rawVersionSpecifier));
                }

                if (fixedReference.Version.StartsWith(
                        "npm:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "NPM dependency aliases do not identify one canonical package edge.",
                        nameof(rawVersionSpecifier));
                }

                PackageDirective fixedDirective =
                    PackageDirective.Parse(
                        fixedReference.FhirDirective);
                if (fixedDirective.Alias is not null
                    || !fixedDirective.PackageId.Equals(
                        fixedReference.Name,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        fixedDirective.RequestedVersion,
                        fixedReference.Version,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The dependency identity is ambiguous when represented as a package directive.",
                        nameof(rawPackageId));
                }

                if (fixedDirective.VersionType
                    is VersionType.Exact
                        or VersionType.CiBuild
                        or VersionType.CiBuildBranch
                        or VersionType.LocalBuild)
                {
                    _ = PackageCacheKey.Create(fixedReference);
                }
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    or FormatException
                    or PackageInstallException)
            {
                long invalidEdgeId = ++_nextEdgeId;
                _invalidEdges.Add(
                    invalidEdgeId,
                    new InvalidDependencyEdge(
                    invalidEdgeId,
                    parentPackageId,
                    parentVersion,
                    string.IsNullOrWhiteSpace(rawPackageId)
                        ? "(invalid)"
                        : rawPackageId,
                    rawVersionSpecifier ?? "(missing)",
                    depth,
                    traversalPath,
                    isRootEdge));
                return new DependencyEdgeRegistration(
                    invalidEdgeId,
                    DependencyEdgeRegistrationKind.Invalid);
            }

            if (fixedReference.Name.Equals(
                    _rootManifest.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                long rootBackEdgeId = ++_nextEdgeId;
                _rootBackEdges.Add(
                    rootBackEdgeId,
                    new RootBackEdge(
                        rootBackEdgeId,
                        parentPackageId,
                        parentVersion,
                        fixedReference.Name,
                        fixedReference.Version!,
                        depth,
                        traversalPath,
                        isRootEdge));
                return new DependencyEdgeRegistration(
                    rootBackEdgeId,
                    DependencyEdgeRegistrationKind.RootBack);
            }

            DependencyEdge edge = new(
                ++_nextEdgeId,
                parentPackageId,
                parentVersion,
                fixedReference.Name,
                fixedReference.Version ?? "latest",
                depth,
                traversalPath,
                isRootEdge);
            if (_versionResolutions.TryGetValue(
                    edge.PackageId,
                    out Dictionary<string, DependencyVersionResolution>?
                        packageResolutions)
                && packageResolutions.TryGetValue(
                    edge.VersionSpecifier,
                    out DependencyVersionResolution? resolution))
            {
                edge.Resolution = resolution;
            }

            _edges.Add(edge.Id, edge);

            DependencyNode node = GetOrCreateNode(edge.PackageId);
            node.IncomingEdges.Add(edge.Id, edge);
            Enqueue(node.PackageId);
            return new DependencyEdgeRegistration(
                edge.Id,
                DependencyEdgeRegistrationKind.Graph);
        }

        private void RemoveOutgoingEdges(DependencyNode node)
        {
            foreach (long edgeId in node.OutgoingEdgeIds)
            {
                DependencyEdge edge = _edges[edgeId];
                _edges.Remove(edgeId);

                DependencyNode child = _nodes[edge.PackageId];
                child.IncomingEdges.Remove(edgeId);
                Enqueue(child.PackageId);
            }

            node.OutgoingEdgeIds.Clear();
            foreach (long edgeId in node.OutgoingInvalidEdgeIds)
            {
                _invalidEdges.Remove(edgeId);
            }

            node.OutgoingInvalidEdgeIds.Clear();
            foreach (long edgeId in node.OutgoingRootBackEdgeIds)
            {
                _rootBackEdges.Remove(edgeId);
            }

            node.OutgoingRootBackEdgeIds.Clear();
        }

        private DependencyNode GetOrCreateNode(string packageId)
        {
            if (_nodes.TryGetValue(
                    packageId,
                    out DependencyNode? node))
            {
                return node;
            }

            node = new DependencyNode(packageId);
            _nodes.Add(packageId, node);
            return node;
        }

        private void Enqueue(string packageId)
        {
            if (_queuedPackages.Add(packageId))
            {
                _pendingPackages.Enqueue(packageId);
            }
        }

        private void DisableUnstablePackage(string packageId)
        {
            _unstablePackageIds.Add(packageId);
            DependencyNode node = _nodes[packageId];
            RemoveOutgoingEdges(node);
            node.SelectedReference = null;
            node.MetadataReference = null;
            node.InstallationReference = null;
            node.RequiresInstallation = true;
            node.BootstrapRequired = false;
            node.ActiveDepth = null;
            node.ActivePath = null;
            node.Metadata = null;
        }

        private bool IsRootBackEdgeSatisfied(RootBackEdge edge)
        {
            VersionType versionType =
                PackageDirective.ClassifyVersion(edge.VersionSpecifier);
            if (versionType == VersionType.Latest)
            {
                return string.Equals(
                    edge.Resolution?.Version?.ToString(),
                    _rootManifest.Version,
                    StringComparison.Ordinal);
            }

            if ((versionType
                        is VersionType.CiBuild
                            or VersionType.CiBuildBranch
                            or VersionType.LocalBuild)
                && IsSameAliasReference(
                    edge.VersionSpecifier,
                    _rootReference.Version))
            {
                return true;
            }

            if (string.Equals(
                    edge.VersionSpecifier,
                    _rootManifest.Version,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (!FhirSemVer.TryParse(
                    _rootManifest.Version,
                    out FhirSemVer? rootVersion)
                || rootVersion.IsWildcard)
            {
                return false;
            }

            return PackageVersionSelector.Select(
                    _rootManifest.Name,
                    edge.VersionSpecifier,
                    [rootVersion],
                    _versionResolveOptions)
                is not null;
        }

        private static bool IsSameAliasReference(
            string requested,
            string? installed)
        {
            if (installed is null)
                return false;

            VersionType requestedType =
                PackageDirective.ClassifyVersion(requested);
            VersionType installedType =
                PackageDirective.ClassifyVersion(installed);
            if (requestedType != installedType)
                return false;

            if (requestedType == VersionType.CiBuildBranch)
            {
                return requested["current$".Length..].Equals(
                    installed["current$".Length..],
                    StringComparison.Ordinal);
            }

            return requested.Equals(
                installed,
                StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<string> GetDistinctResolvedVersions(
            DependencyNode node)
        {
            List<DependencyEdge> orderedEdges =
                node.IncomingEdges.Values
                    .Where(edge => edge.Depth <= _options.MaxDepth)
                    .ToList();
            orderedEdges.Sort(CompareEdges);

            List<string> versions = [];
            foreach (DependencyEdge edge in orderedEdges)
            {
                string? version = edge.Resolution?.Version?.ToString();
                if (version is not null
                    && !versions.Contains(
                        version,
                        StringComparer.Ordinal))
                {
                    versions.Add(version);
                }
            }

            return versions;
        }

        private IReadOnlyList<PackageReference> CreateInstallOrder()
        {
            List<PackageReference> order = [];
            HashSet<string> visited =
                new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> active =
                new(StringComparer.OrdinalIgnoreCase);
            List<DependencyEdge> rootEdges = _edges.Values
                .Where(edge => edge.IsRootEdge)
                .ToList();
            rootEdges.Sort(CompareEdges);
            foreach (DependencyEdge edge in rootEdges)
            {
                VisitForInstallOrder(
                    edge.PackageId,
                    visited,
                    active,
                    order);
            }

            return order;
        }

        private IReadOnlyList<PackageReference>
            CreateBootstrapInstallOrder()
        {
            List<PackageReference> order = [];
            HashSet<string> visited = new(StringComparer.Ordinal);
            List<DependencyEdge> edges = _edges.Values.ToList();
            edges.Sort(CompareEdges);
            foreach (DependencyEdge edge in edges)
            {
                PackageReference? reference =
                    edge.Resolution?.BootstrapRequired == true
                        ? edge.Resolution?.InstallationReference
                        : null;
                if (reference is not PackageReference bootstrap
                    || edge.Depth > _options.MaxDepth)
                {
                    continue;
                }

                string key =
                    $"{bootstrap.Name.ToLowerInvariant()}\0{bootstrap.Version}";
                if (visited.Add(key))
                {
                    order.Add(bootstrap);
                }
            }

            return order;
        }

        private void VisitForInstallOrder(
            string packageId,
            HashSet<string> visited,
            HashSet<string> active,
            List<PackageReference> order)
        {
            if (visited.Contains(packageId)
                || !_nodes.TryGetValue(
                    packageId,
                    out DependencyNode? node)
                || node.SelectedReference is not PackageReference selected)
            {
                return;
            }

            if (!active.Add(packageId))
                return;

            List<DependencyEdge> childEdges = node.OutgoingEdgeIds
                .Select(edgeId => _edges[edgeId])
                .ToList();
            childEdges.Sort(CompareEdges);
            foreach (DependencyEdge childEdge in childEdges)
            {
                VisitForInstallOrder(
                    childEdge.PackageId,
                    visited,
                    active,
                    order);
            }

            active.Remove(packageId);
            if (visited.Add(packageId))
            {
                if (node.RequiresInstallation
                    && !node.BootstrapRequired)
                {
                    order.Add(
                        node.InstallationReference
                        ?? selected);
                }
            }
        }

        private string CreateStateSignature()
        {
            StringBuilder builder = new();
            IEnumerable<DependencyNode> orderedNodes =
                _nodes.Values
                    .OrderBy(
                        node => node.PackageId,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        node => node.PackageId,
                        StringComparer.Ordinal);
            foreach (DependencyNode node in orderedNodes)
            {
                builder.Append('N');
                AppendComponent(builder, node.PackageId);
                AppendComponent(
                    builder,
                    node.SelectedReference?.Version);
                AppendComponent(
                    builder,
                    node.MetadataReference?.FhirDirective);
                AppendComponent(
                    builder,
                    node.InstallationReference?.FhirDirective);
                builder.Append(
                    node.RequiresInstallation ? 'I' : 'C');
                builder.Append(
                    node.BootstrapRequired ? 'B' : 'R');
                builder.Append(node.ActiveDepth?.ToString() ?? "-");
                builder.Append(':');
                AppendPath(builder, node.ActivePath);
            }

            List<DependencyEdge> orderedEdges =
                _edges.Values.ToList();
            orderedEdges.Sort(CompareSemanticEdges);
            foreach (DependencyEdge edge in orderedEdges)
            {
                builder.Append('E');
                AppendComponent(builder, edge.ParentPackageId);
                AppendComponent(builder, edge.ParentVersion);
                AppendComponent(builder, edge.PackageId);
                AppendComponent(builder, edge.VersionSpecifier);
                builder.Append(edge.Depth);
                builder.Append(edge.IsRootEdge ? 'R' : 'C');
                builder.Append(':');
                AppendPath(builder, edge.TraversalPath);
            }

            IEnumerable<InvalidDependencyEdge> orderedInvalidEdges =
                _invalidEdges.Values
                    .OrderBy(
                        edge => edge.TraversalPath,
                        DependencyPathComparer.Instance);
            foreach (InvalidDependencyEdge edge in orderedInvalidEdges)
            {
                builder.Append('I');
                AppendComponent(builder, edge.ParentPackageId);
                AppendComponent(builder, edge.ParentVersion);
                AppendComponent(builder, edge.PackageId);
                AppendComponent(builder, edge.VersionSpecifier);
                builder.Append(edge.Depth);
                builder.Append(edge.IsRootEdge ? 'R' : 'C');
                builder.Append(':');
                AppendPath(builder, edge.TraversalPath);
            }

            IEnumerable<RootBackEdge> orderedRootBackEdges =
                _rootBackEdges.Values
                    .OrderBy(
                        edge => edge.TraversalPath,
                        DependencyPathComparer.Instance);
            foreach (RootBackEdge edge in orderedRootBackEdges)
            {
                builder.Append('R');
                AppendComponent(builder, edge.ParentPackageId);
                AppendComponent(builder, edge.ParentVersion);
                AppendComponent(builder, edge.PackageId);
                AppendComponent(builder, edge.VersionSpecifier);
                builder.Append(edge.Depth);
                builder.Append(edge.IsRootEdge ? 'R' : 'C');
                builder.Append(':');
                AppendPath(builder, edge.TraversalPath);
            }

            foreach (string packageId in _pendingPackages)
            {
                builder.Append('Q');
                AppendComponent(builder, packageId);
            }

            foreach (string packageId in _unstablePackageIds
                         .OrderBy(
                             value => value,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(
                             value => value,
                             StringComparer.Ordinal))
            {
                builder.Append('D');
                AppendComponent(builder, packageId);
            }

            return builder.ToString();
        }

        private static int CompareSemanticEdges(
            DependencyEdge left,
            DependencyEdge right)
        {
            int comparison = ComparePaths(
                left.TraversalPath,
                right.TraversalPath);
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left.ParentPackageId,
                right.ParentPackageId);
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.Ordinal.Compare(
                left.ParentVersion,
                right.ParentVersion);
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left.PackageId,
                right.PackageId);
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.Ordinal.Compare(
                left.VersionSpecifier,
                right.VersionSpecifier);
            if (comparison != 0)
                return comparison;

            comparison = left.Depth.CompareTo(right.Depth);
            return comparison != 0
                ? comparison
                : left.IsRootEdge.CompareTo(right.IsRootEdge);
        }

        private static void AppendComponent(
            StringBuilder builder,
            string? value)
        {
            if (value is null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length);
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }

        private static void AppendPath(
            StringBuilder builder,
            IReadOnlyList<int>? path)
        {
            if (path is null)
            {
                builder.Append("-;");
                return;
            }

            builder.Append(path.Count);
            builder.Append(':');
            foreach (int segment in path)
            {
                builder.Append(segment);
                builder.Append(',');
            }

            builder.Append(';');
        }

        private static int[] AppendPath(
            IReadOnlyList<int> path,
            int dependencyIndex)
        {
            int[] result = new int[path.Count + 1];
            for (int index = 0; index < path.Count; index++)
            {
                result[index] = path[index];
            }

            result[^1] = dependencyIndex;
            return result;
        }

        private static int CompareEdges(
            DependencyEdge left,
            DependencyEdge right)
        {
            int pathComparison = ComparePaths(
                left.TraversalPath,
                right.TraversalPath);
            return pathComparison != 0
                ? pathComparison
                : left.Id.CompareTo(right.Id);
        }

        internal static int ComparePaths(
            IReadOnlyList<int> left,
            IReadOnlyList<int> right)
        {
            int commonLength = Math.Min(left.Count, right.Count);
            for (int index = 0; index < commonLength; index++)
            {
                int comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                    return comparison;
            }

            return left.Count.CompareTo(right.Count);
        }

        private static bool SamePath(
            IReadOnlyList<int>? left,
            IReadOnlyList<int>? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Count != right.Count)
                return false;

            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                    return false;
            }

            return true;
        }

        private static bool SameReference(
            PackageReference? left,
            PackageReference? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue == right.HasValue;

            return string.Equals(
                    left.Value.Name,
                    right.Value.Name,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    left.Value.Version,
                    right.Value.Version,
                    StringComparison.Ordinal);
        }
    }

    private sealed class DependencyNode
    {
        internal DependencyNode(string packageId)
        {
            PackageId = packageId;
        }

        internal string PackageId { get; }

        internal Dictionary<long, DependencyEdge> IncomingEdges { get; } = [];

        internal List<long> OutgoingEdgeIds { get; } = [];

        internal List<long> OutgoingInvalidEdgeIds { get; } = [];

        internal List<long> OutgoingRootBackEdgeIds { get; } = [];

        internal PackageReference? SelectedReference { get; set; }

        internal PackageReference? MetadataReference { get; set; }

        internal PackageReference? InstallationReference { get; set; }

        internal bool RequiresInstallation { get; set; } = true;

        internal bool BootstrapRequired { get; set; }

        internal int? ActiveDepth { get; set; }

        internal int[]? ActivePath { get; set; }

        internal DependencyMetadataResult? Metadata { get; set; }
    }

    private sealed class DependencyEdge
    {
        internal DependencyEdge(
            long id,
            string parentPackageId,
            string? parentVersion,
            string packageId,
            string versionSpecifier,
            int depth,
            int[] traversalPath,
            bool isRootEdge)
        {
            Id = id;
            ParentPackageId = parentPackageId;
            ParentVersion = parentVersion;
            PackageId = packageId;
            VersionSpecifier = versionSpecifier;
            Depth = depth;
            TraversalPath = traversalPath;
            IsRootEdge = isRootEdge;
        }

        internal long Id { get; }

        internal string ParentPackageId { get; }

        internal string? ParentVersion { get; }

        internal string PackageId { get; }

        internal string VersionSpecifier { get; }

        internal int Depth { get; }

        internal int[] TraversalPath { get; }

        internal bool IsRootEdge { get; }

        internal DependencyVersionResolution? Resolution { get; set; }
    }

    private sealed record ResolvedDependencyRequirement(
        DependencyEdge Edge,
        FhirSemVer Version,
        PackageReference? InstallationReference,
        PackageReference? MetadataReference,
        bool RequiresInstallation,
        bool BootstrapRequired);

    private sealed record DependencyVersionResolution(
        FhirSemVer? Version,
        IReadOnlyList<RegistryAttemptFailure> RegistryFailures,
        PackageReference? InstallationReference,
        PackageReference? MetadataReference,
        bool RequiresInstallation,
        bool BootstrapRequired = false);

    private enum DependencyEdgeRegistrationKind
    {
        Graph,
        Invalid,
        RootBack,
    }

    private readonly record struct DependencyEdgeRegistration(
        long Id,
        DependencyEdgeRegistrationKind Kind);

    private sealed record InvalidDependencyEdge(
        long Id,
        string ParentPackageId,
        string? ParentVersion,
        string PackageId,
        string VersionSpecifier,
        int Depth,
        int[] TraversalPath,
        bool IsRootEdge);

    private sealed record RootBackEdge(
        long Id,
        string ParentPackageId,
        string? ParentVersion,
        string PackageId,
        string VersionSpecifier,
        int Depth,
        int[] TraversalPath,
        bool IsRootEdge)
    {
        internal DependencyVersionResolution? Resolution { get; set; }
    }

    private sealed class DependencyPathComparer :
        IComparer<IReadOnlyList<int>>
    {
        internal static DependencyPathComparer Instance { get; } = new();

        public int Compare(
            IReadOnlyList<int>? left,
            IReadOnlyList<int>? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            return ActiveDependencyGraph.ComparePaths(left, right);
        }
    }

    private sealed record DependencyMetadataResult(
        bool CanTraverse,
        bool IsComplete,
        IReadOnlyDictionary<string, string> Dependencies,
        string Message,
        IReadOnlyList<RegistryAttemptFailure> RegistryFailures)
    {
        internal static DependencyMetadataResult Available(
            IReadOnlyDictionary<string, string> dependencies) =>
            new(true, true, dependencies, string.Empty, []);

        internal static DependencyMetadataResult Partial(
            IReadOnlyDictionary<string, string> dependencies,
            string message,
            IReadOnlyList<RegistryAttemptFailure> registryFailures) =>
            new(true, false, dependencies, message, registryFailures);

        internal static DependencyMetadataResult Unavailable(
            string message,
            IReadOnlyList<RegistryAttemptFailure> registryFailures) =>
            new(
                false,
                false,
                s_noDependencies,
                message,
                registryFailures);
    }

    private sealed record DependencyGraphResult(
        IReadOnlyDictionary<string, PackageReference> Resolved,
        IReadOnlyList<DependencyResolutionFailure> Failures,
        IReadOnlyList<PackageReference> InstallOrder,
        IReadOnlyList<PackageReference> BootstrapInstallOrder);
}
