// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using FhirPkg.Cache;
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
                listing.Versions.TryGetValue(
                    reference.Version,
                    out versionInfo);
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
        private readonly DependencyResolveOptions _options;
        private readonly PackageFixupPolicy _fixupPolicy;
        private readonly VersionResolveOptions _versionResolveOptions;
        private readonly Dictionary<string, DependencyNode> _nodes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, DependencyEdge> _edges = [];
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
                    [dependencyIndex]);
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
                if (resolution.RegistryFailures.Count > 0)
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
                        Code = DependencyResolutionFailureCode.PackageNotFound,
                        PackageId = edge.PackageId,
                        VersionSpecifier = edge.VersionSpecifier,
                        ParentPackageId = edge.ParentPackageId,
                        ParentVersion = edge.ParentVersion,
                        Depth = edge.Depth,
                        Message =
                            $"Could not resolve version '{edge.VersionSpecifier}'.",
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

            return new DependencyGraphResult(resolved, failures);
        }

        private async Task RecomputeNodeAsync(
            DependencyNode node,
            CancellationToken cancellationToken)
        {
            if (_unstablePackageIds.Contains(node.PackageId))
            {
                RemoveOutgoingEdges(node);
                node.SelectedReference = null;
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
                            version));
                }
            }

            PackageReference? selectedReference = null;
            int? activeDepth = null;
            int[]? activePath = null;
            if (requirements.Count > 0)
            {
                ResolvedDependencyRequirement winner =
                    SelectWinner(requirements);
                selectedReference = new PackageReference(
                    node.PackageId,
                    winner.Version.ToString());
                ResolvedDependencyRequirement activeRoute =
                    SelectActiveRoute(requirements);
                activeDepth = activeRoute.Edge.Depth;
                activePath = activeRoute.Edge.TraversalPath;
            }

            bool selectionChanged =
                !SameReference(
                    node.SelectedReference,
                    selectedReference)
                || node.ActiveDepth != activeDepth
                || !SamePath(node.ActivePath, activePath);
            if (!selectionChanged)
                return;

            RemoveOutgoingEdges(node);
            node.SelectedReference = selectedReference;
            node.ActiveDepth = activeDepth;
            node.ActivePath = activePath;
            node.Metadata = null;

            if (selectedReference is not PackageReference selected
                || activeDepth is null
                || activePath is null)
            {
                return;
            }

            DependencyMetadataResult metadata =
                await GetMetadataAsync(
                        selected,
                        cancellationToken)
                    .ConfigureAwait(false);
            node.Metadata = metadata;
            if (!metadata.CanTraverse)
                return;

            int dependencyIndex = 0;
            foreach (KeyValuePair<string, string> dependency
                     in metadata.Dependencies)
            {
                long edgeId = AddEdge(
                    node.PackageId,
                    selected.Version,
                    dependency.Key,
                    dependency.Value,
                    activeDepth.Value + 1,
                    AppendPath(activePath, dependencyIndex));
                node.OutgoingEdgeIds.Add(edgeId);
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

            DependencyVersionResolution resolution;
            try
            {
                FhirSemVer? version =
                    await _owner._versionResolver.ResolveVersionAsync(
                            packageId,
                            versionSpecifier,
                            _versionResolveOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                resolution = new DependencyVersionResolution(version, []);
            }
            catch (RegistryOperationException exception)
            {
                resolution = new DependencyVersionResolution(
                    null,
                    exception.Failures);
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or TimeoutException
                    or JsonException
                    or InvalidDataException
                    or FormatException)
            {
                resolution = new DependencyVersionResolution(
                    null,
                    [
                        RegistryAttemptFailure.Capture(
                            _owner._registryClient.Endpoint,
                            exception)
                    ]);
            }

            packageResolutions.Add(versionSpecifier, resolution);
            return resolution;
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
                    StringComparer.OrdinalIgnoreCase);
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

        private long AddEdge(
            string parentPackageId,
            string? parentVersion,
            string rawPackageId,
            string? rawVersionSpecifier,
            int depth,
            int[] traversalPath)
        {
            PackageReference fixedReference = PackageFixups.Apply(
                new PackageReference(
                    rawPackageId,
                    rawVersionSpecifier),
                _fixupPolicy);
            DependencyEdge edge = new(
                ++_nextEdgeId,
                parentPackageId,
                parentVersion,
                fixedReference.Name,
                fixedReference.Version ?? "latest",
                depth,
                traversalPath);
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
            return edge.Id;
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
            node.ActiveDepth = null;
            node.ActivePath = null;
            node.Metadata = null;
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
                        StringComparer.OrdinalIgnoreCase))
                {
                    versions.Add(version);
                }
            }

            return versions;
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

            comparison = StringComparer.OrdinalIgnoreCase.Compare(
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
            return comparison != 0
                ? comparison
                : left.Depth.CompareTo(right.Depth);
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

        private static int ComparePaths(
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
                    StringComparison.OrdinalIgnoreCase);
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

        internal PackageReference? SelectedReference { get; set; }

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
            int[] traversalPath)
        {
            Id = id;
            ParentPackageId = parentPackageId;
            ParentVersion = parentVersion;
            PackageId = packageId;
            VersionSpecifier = versionSpecifier;
            Depth = depth;
            TraversalPath = traversalPath;
        }

        internal long Id { get; }

        internal string ParentPackageId { get; }

        internal string? ParentVersion { get; }

        internal string PackageId { get; }

        internal string VersionSpecifier { get; }

        internal int Depth { get; }

        internal int[] TraversalPath { get; }

        internal DependencyVersionResolution? Resolution { get; set; }
    }

    private sealed record ResolvedDependencyRequirement(
        DependencyEdge Edge,
        FhirSemVer Version);

    private sealed record DependencyVersionResolution(
        FhirSemVer? Version,
        IReadOnlyList<RegistryAttemptFailure> RegistryFailures);

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
        IReadOnlyList<DependencyResolutionFailure> Failures);
}
