// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// The subset of GitHub repository metadata that CI build canonical selection needs.
/// </summary>
internal sealed record GitHubRepositoryFacts
{
    /// <summary>Whether the repository is a fork of another repository.</summary>
    public required bool IsFork { get; init; }

    /// <summary>When the repository was created, when reported.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The <c>org/repo</c> name of the upstream repository, when this is a fork.</summary>
    public string? ParentFullName { get; init; }

    /// <summary>The repository's default branch, when reported.</summary>
    public string? DefaultBranch { get; init; }
}

/// <summary>
/// Supplies GitHub repository facts to tier 2 of canonical-repository selection.
/// </summary>
/// <remarks>
/// Implementations must never throw for an unavailable or unreadable repository:
/// a <see langword="null"/> result means <em>facts unavailable</em> and lets
/// selection degrade to the next tier. Only cancellation raised from the caller's
/// own token is allowed to propagate.
/// </remarks>
internal interface IGitHubRepositoryFactsProvider
{
    /// <summary>
    /// Attempts to retrieve facts about a repository.
    /// </summary>
    /// <param name="repository">The repository to describe.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The repository facts, or <see langword="null"/> when they could not be obtained.
    /// </returns>
    Task<GitHubRepositoryFacts?> TryGetFactsAsync(
        CiBuildRepositoryIdentity repository,
        CancellationToken cancellationToken);
}
