// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// Supplies the <c>package.manifest.json</c> published for a repository's default build.
/// </summary>
/// <remarks>
/// Implementations return <see langword="null"/> when the manifest is unavailable for
/// any reason, letting the resolver take its documented branch-qualified fallback.
/// </remarks>
internal interface ICiBuildManifestSource
{
    /// <summary>
    /// Attempts to fetch the default build manifest for a repository.
    /// </summary>
    /// <param name="repository">The repository to fetch the manifest for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The manifest, or <see langword="null"/> when it is unavailable.</returns>
    Task<CiBuildManifest?> TryGetDefaultBuildManifestAsync(
        CiBuildRepositoryIdentity repository,
        CancellationToken cancellationToken);
}

/// <summary>
/// The artifact location and metadata chosen for a CI build resolution.
/// </summary>
internal sealed record CiBuildArtifactLocation
{
    /// <summary>The tarball URI to download.</summary>
    public required Uri TarballUri { get; init; }

    /// <summary>The version reported for the artifact, when known.</summary>
    public string? Version { get; init; }

    /// <summary>The publication date reported for the artifact, when known.</summary>
    public DateTimeOffset? PublicationDate { get; init; }

    /// <summary>The FHIR versions declared for the artifact, when known.</summary>
    public IReadOnlyList<string>? FhirVersions { get; init; }

    /// <summary>The branch the artifact was taken from, when it is branch-qualified.</summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Whether this is the repository's manifest-backed default build.
    /// </summary>
    /// <remarks>
    /// This is the single signal callers key resolution warnings off, so any future
    /// fallback added to the resolver is warned about automatically rather than
    /// returning a non-default build silently.
    /// </remarks>
    public required bool IsDefaultBuild { get; init; }
}

/// <summary>
/// Turns a chosen repository into the artifact location that actually serves its
/// content, plus the metadata that describes what is served there.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>@current</c> request resolves to the short
/// <c>{baseUrl}/ig/{org}/{repo}/package.tgz</c> form described by the repository's
/// <c>package.manifest.json</c>. When that manifest is unavailable or carries no
/// version, the repository's newest branch build is used instead and
/// <see cref="CiBuildArtifactLocation.IsDefaultBuild"/> reports <see langword="false"/>.
/// </para>
/// <para>
/// A <c>@current$branch</c> request always resolves to the branch-qualified
/// <c>{baseUrl}/ig/{org}/{repo}/branches/{branch}/package.tgz</c> form.
/// </para>
/// </remarks>
internal sealed class CiBuildArtifactResolver
{
    private readonly string _baseUrl;
    private readonly ICiBuildManifestSource _manifestSource;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="CiBuildArtifactResolver"/>.
    /// </summary>
    /// <param name="baseUrl">The CI build server base URL, without a trailing slash.</param>
    /// <param name="manifestSource">The source of default-build manifests.</param>
    /// <param name="logger">The logger instance.</param>
    public CiBuildArtifactResolver(
        string baseUrl,
        ICiBuildManifestSource manifestSource,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(manifestSource);
        ArgumentNullException.ThrowIfNull(logger);

        _baseUrl = baseUrl.TrimEnd('/');
        _manifestSource = manifestSource;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the artifact location for a repository.
    /// </summary>
    /// <param name="repository">The canonical repository chosen for the package.</param>
    /// <param name="candidates">The candidate builds; may span several repositories.</param>
    /// <param name="requestedBranch">
    /// The branch named by a <c>@current$branch</c> request, or <see langword="null"/>
    /// for a plain <c>@current</c> request.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The artifact location, or <see langword="null"/> when no candidate in the
    /// repository satisfies the request.
    /// </returns>
    public async Task<CiBuildArtifactLocation?> ResolveAsync(
        CiBuildRepositoryIdentity repository,
        IReadOnlyList<CiBuildCandidate> candidates,
        string? requestedBranch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (requestedBranch is not null)
            return ResolveBranchBuild(repository, candidates, requestedBranch);

        CiBuildManifest? manifest = await _manifestSource
            .TryGetDefaultBuildManifestAsync(repository, cancellationToken)
            .ConfigureAwait(false);

        if (manifest?.Version is not null)
        {
            return new CiBuildArtifactLocation
            {
                TarballUri = new Uri($"{_baseUrl}/ig/{repository.ToUrlPath()}/package.tgz"),
                Version = manifest.Version,
                PublicationDate = CiBuildDate.TryParse(manifest.Date, out DateTimeOffset manifestDate)
                    ? manifestDate
                    : null,
                FhirVersions = manifest.EffectiveFhirVersions,
                Branch = null,
                IsDefaultBuild = true,
            };
        }

        _logger.LogDebug(
            "No usable package.manifest.json for {Repository}; falling back to its newest branch build",
            repository);

        CiBuildCandidate? newest = NewestIn(candidates, repository, branch: null);
        if (newest is null)
            return null;

        return new CiBuildArtifactLocation
        {
            TarballUri = BuildBranchUri(repository, newest.Branch),
            Version = newest.Record.IgVersion,
            PublicationDate = newest.BuildDate,
            FhirVersions = newest.Record.FhirVersion is string fhirVersion ? [fhirVersion] : null,
            Branch = newest.Branch,
            IsDefaultBuild = false,
        };
    }

    private CiBuildArtifactLocation? ResolveBranchBuild(
        CiBuildRepositoryIdentity repository,
        IReadOnlyList<CiBuildCandidate> candidates,
        string requestedBranch)
    {
        CiBuildCandidate? newest = NewestIn(candidates, repository, requestedBranch);
        if (newest is null)
        {
            _logger.LogDebug(
                "No CI build candidate in {Repository} on branch {Branch}", repository, requestedBranch);
            return null;
        }

        return new CiBuildArtifactLocation
        {
            TarballUri = BuildBranchUri(repository, requestedBranch),
            Version = newest.Record.IgVersion,
            PublicationDate = newest.BuildDate,
            FhirVersions = newest.Record.FhirVersion is string fhirVersion ? [fhirVersion] : null,
            Branch = requestedBranch,
            IsDefaultBuild = false,
        };
    }

    private Uri BuildBranchUri(CiBuildRepositoryIdentity repository, string branch) =>
        new($"{_baseUrl}/ig/{repository.ToUrlPath()}/branches/{Uri.EscapeDataString(branch)}/package.tgz");

    private static CiBuildCandidate? NewestIn(
        IReadOnlyList<CiBuildCandidate> candidates,
        CiBuildRepositoryIdentity repository,
        string? branch) =>
        candidates
            .Where(c => c.Repository.Equals(repository)
                && (branch is null || string.Equals(c.Branch, branch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.BuildDate is null)
            .ThenByDescending(c => c.BuildDate ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
}
