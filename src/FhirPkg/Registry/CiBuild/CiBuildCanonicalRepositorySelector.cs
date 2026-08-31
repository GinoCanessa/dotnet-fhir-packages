// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// Identifies which rule settled a canonical-repository choice.
/// </summary>
internal enum CiBuildSelectionTier
{
    /// <summary>Exactly one repository published the package, so no heuristic was needed.</summary>
    SoleRepository,

    /// <summary>The <see cref="CanonicalOrganizationTable"/> named the publishing organization.</summary>
    PrefixTable,

    /// <summary>GitHub reported the selected repository is not a fork.</summary>
    NonForkCheck,

    /// <summary>No stronger signal was available, so the oldest build's repository was taken.</summary>
    Oldest,
}

/// <summary>
/// The repository chosen for a CI build resolution, and the tier that chose it.
/// </summary>
/// <param name="Repository">The selected repository.</param>
/// <param name="Tier">The rule that settled the choice.</param>
internal sealed record CiBuildRepositorySelection(
    CiBuildRepositoryIdentity Repository,
    CiBuildSelectionTier Tier);

/// <summary>
/// Chooses the canonical publishing repository for a FHIR package from the set of
/// repositories that published a CI build for it.
/// </summary>
/// <remarks>
/// <para>Three tiers are applied in order:</para>
/// <list type="number">
///   <item><description>
///     <see cref="CanonicalOrganizationTable"/> names the organization, and that
///     organization is present among the candidates.
///   </description></item>
///   <item><description>
///     GitHub reports a candidate repository is not a fork. Repositories are tried
///     oldest build first; those whose facts are unavailable are skipped.
///   </description></item>
///   <item><description>
///     The repository that owns the oldest build wins.
///   </description></item>
/// </list>
/// <para>
/// A build date of <see langword="null"/> always sorts last, so an unparseable date
/// is treated as neither the newest nor the oldest build.
/// </para>
/// </remarks>
internal sealed class CiBuildCanonicalRepositorySelector
{
    private readonly IGitHubRepositoryFactsProvider _factsProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="CiBuildCanonicalRepositorySelector"/>.
    /// </summary>
    /// <param name="factsProvider">The provider consulted by tier 2.</param>
    /// <param name="logger">The logger instance.</param>
    public CiBuildCanonicalRepositorySelector(
        IGitHubRepositoryFactsProvider factsProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factsProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _factsProvider = factsProvider;
        _logger = logger;
    }

    /// <summary>
    /// Selects the canonical repository for a package from its CI build candidates.
    /// </summary>
    /// <param name="packageId">The FHIR package identifier being resolved.</param>
    /// <param name="candidates">The candidate builds; may span several repositories.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The selected repository and the tier that chose it, or <see langword="null"/>
    /// when <paramref name="candidates"/> is empty.
    /// </returns>
    public async Task<CiBuildRepositorySelection?> SelectAsync(
        string packageId,
        IReadOnlyList<CiBuildCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return null;

        List<RepositoryGroup> groups = GroupByRepository(candidates);

        if (groups.Count == 1)
        {
            return new CiBuildRepositorySelection(groups[0].Repository, CiBuildSelectionTier.SoleRepository);
        }

        if (CanonicalOrganizationTable.TryGetOrganization(packageId, out string canonicalOrganization))
        {
            // Organization-level canonicality is settled by the table; the only open
            // question is which of that organization's repositories is current.
            RepositoryGroup? tableMatch = groups
                .Where(g => string.Equals(g.Repository.Org, canonicalOrganization, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.NewestBuild is null)
                .ThenByDescending(g => g.NewestBuild ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (tableMatch is not null)
            {
                _logger.LogDebug(
                    "Canonical repository for {PackageId} resolved to {Repository} by the prefix table",
                    packageId, tableMatch.Repository);

                return new CiBuildRepositorySelection(tableMatch.Repository, CiBuildSelectionTier.PrefixTable);
            }

            _logger.LogDebug(
                "Prefix table names organization {Organization} for {PackageId}, but it published no candidate build",
                canonicalOrganization, packageId);
        }

        List<RepositoryGroup> oldestFirst = groups
            .OrderBy(g => g.OldestBuild is null)
            .ThenBy(g => g.OldestBuild ?? DateTimeOffset.MaxValue)
            .ToList();

        foreach (RepositoryGroup group in oldestFirst)
        {
            GitHubRepositoryFacts? facts = await _factsProvider
                .TryGetFactsAsync(group.Repository, cancellationToken)
                .ConfigureAwait(false);

            if (facts is null)
            {
                _logger.LogDebug(
                    "GitHub facts unavailable for {Repository}; skipping it for the non-fork check",
                    group.Repository);
                continue;
            }

            if (!facts.IsFork)
            {
                _logger.LogDebug(
                    "Canonical repository for {PackageId} resolved to {Repository} by the non-fork check",
                    packageId, group.Repository);

                return new CiBuildRepositorySelection(group.Repository, CiBuildSelectionTier.NonForkCheck);
            }
        }

        RepositoryGroup oldest = oldestFirst[0];

        _logger.LogDebug(
            "Canonical repository for {PackageId} fell through to the oldest build, {Repository}",
            packageId, oldest.Repository);

        return new CiBuildRepositorySelection(oldest.Repository, CiBuildSelectionTier.Oldest);
    }

    private static List<RepositoryGroup> GroupByRepository(IReadOnlyList<CiBuildCandidate> candidates) =>
        candidates
            .GroupBy(c => c.Repository)
            .Select(g => new RepositoryGroup(
                g.Key,
                g.Min(c => c.BuildDate),
                g.Max(c => c.BuildDate)))
            .ToList();

    private sealed record RepositoryGroup(
        CiBuildRepositoryIdentity Repository,
        DateTimeOffset? OldestBuild,
        DateTimeOffset? NewestBuild);
}
