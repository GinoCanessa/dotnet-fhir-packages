// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.RegularExpressions;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Validation;

internal interface IFixtureBundleLocator
{
    Task<LocatedFixtureBundle> LocateAsync(
        string repository,
        string commit,
        string tag,
        string corpusPath,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed class FixtureBundleLocator : IFixtureBundleLocator
{
    internal const string ArtifactNamePrefix = "fixture-bundle-v1-";
    internal const string DriftJobName = "fixture-drift";
    internal const string DriftWorkflowPath =
        ".github/workflows/fixture-drift.yaml";

    private static readonly TimeSpan s_minimumRemainingRetention =
        TimeSpan.FromHours(24);

    private static readonly Regex DigestRegex = new(
        "^sha256:[0-9a-f]{64}\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ObjectIdRegex = new(
        "^(?:[0-9a-f]{40}|[0-9a-f]{64})\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReleaseGitClient _gitClient;
    private readonly IReleaseGitObjectClient _gitObjectClient;
    private readonly IReleaseGitHubClient _gitHubClient;

    internal FixtureBundleLocator(
        IReleaseGitClient gitClient,
        IReleaseGitObjectClient gitObjectClient,
        IReleaseGitHubClient gitHubClient)
    {
        _gitClient =
            gitClient ??
            throw new ArgumentNullException(nameof(gitClient));
        _gitObjectClient =
            gitObjectClient ??
            throw new ArgumentNullException(nameof(gitObjectClient));
        _gitHubClient =
            gitHubClient ??
            throw new ArgumentNullException(nameof(gitHubClient));
    }

    public async Task<LocatedFixtureBundle> LocateAsync(
        string repository,
        string commit,
        string tag,
        string corpusPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ObjectIdRegex.IsMatch(commit))
        {
            throw new ReleaseValidationException(
                $"Commit '{commit}' must be a full lowercase hexadecimal object id.");
        }

        if (string.IsNullOrWhiteSpace(corpusPath))
        {
            throw new ReleaseValidationException(
                "The corpus path must not be empty.");
        }

        ReleaseGitCommandResult blobResult =
            await _gitObjectClient.ResolveBlobIdAsync(
                commit,
                corpusPath,
                cancellationToken).ConfigureAwait(false);
        string corpusBlob = blobResult.Output;
        if (blobResult.ExitCode != 0 ||
            !ObjectIdRegex.IsMatch(corpusBlob))
        {
            throw new ReleaseValidationException(
                $"Unable to resolve the git blob id of '{corpusPath}' at '{commit}'.");
        }

        string artifactName = ArtifactNamePrefix + corpusBlob;
        IReadOnlyList<GitHubArtifact> artifacts =
            await _gitHubClient.ListArtifactsByNameAsync(
                repository,
                artifactName,
                cancellationToken).ConfigureAwait(false);

        ReleaseGitCommandResult fetchOriginMainResult =
            await _gitClient.FetchOriginMainAsync(
                cancellationToken).ConfigureAwait(false);
        if (fetchOriginMainResult.ExitCode != 0)
        {
            throw new ReleaseValidationException(
                "Unable to fetch 'origin/main'.");
        }

        QualifiedFixtureBundle? newest = null;
        foreach (GitHubArtifact artifact in artifacts)
        {
            QualifiedFixtureBundle? candidate = await QualifyAsync(
                    repository,
                    artifactName,
                    corpusBlob,
                    artifact,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null &&
                (newest is null || IsNewer(candidate, newest)))
            {
                newest = candidate;
            }
        }

        if (newest is null)
        {
            throw new ReleaseValidationException(
                $"No frozen fixture bundle for {corpusPath} at {commit} (corpus blob {corpusBlob}). Run 'gh workflow run fixture-drift.yaml --ref {tag}', wait for it to pass, then re-run this workflow's failed jobs.");
        }

        return newest.Bundle;
    }

    private async Task<QualifiedFixtureBundle?> QualifyAsync(
        string repository,
        string artifactName,
        string corpusBlob,
        GitHubArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                artifact.Name,
                artifactName,
                StringComparison.Ordinal) ||
            artifact.Expired ||
            artifact.ExpiresAt is not DateTimeOffset expiresAt ||
            expiresAt < now.Add(s_minimumRemainingRetention) ||
            artifact.Digest is not string digest ||
            !DigestRegex.IsMatch(digest) ||
            artifact.WorkflowRunId is not long runId)
        {
            return null;
        }

        GitHubWorkflowRun run =
            await _gitHubClient.GetWorkflowRunAsync(
                repository,
                runId,
                cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                run.Path,
                DriftWorkflowPath,
                StringComparison.Ordinal) ||
            !IsTrustedEvent(run.Event) ||
            !string.Equals(
                run.RepositoryFullName,
                repository,
                StringComparison.Ordinal) ||
            !string.Equals(
                run.HeadRepositoryFullName,
                repository,
                StringComparison.Ordinal) ||
            !ObjectIdRegex.IsMatch(run.HeadSha))
        {
            return null;
        }

        // The run's own conclusion is deliberately not consulted, so the
        // separate LIVE job can never disqualify a bundle.
        IReadOnlyList<GitHubWorkflowJob> jobs =
            await _gitHubClient.ListWorkflowRunJobsAsync(
                repository,
                run.Id,
                cancellationToken).ConfigureAwait(false);
        if (!LatestDriftJobSucceeded(jobs))
        {
            return null;
        }

        ReleaseGitCommandResult ancestryResult =
            await _gitClient.CheckAncestorAsync(
                run.HeadSha,
                "origin/main",
                cancellationToken).ConfigureAwait(false);
        if (ancestryResult.ExitCode != 0)
        {
            return null;
        }

        return new QualifiedFixtureBundle(
            new LocatedFixtureBundle(
                artifact.Id,
                artifact.Name,
                digest,
                run.Id,
                run.HtmlUrl,
                corpusBlob,
                expiresAt),
            run.CreatedAt);
    }

    private static bool IsTrustedEvent(string eventName) =>
        string.Equals(
            eventName,
            "schedule",
            StringComparison.Ordinal) ||
        string.Equals(
            eventName,
            "workflow_dispatch",
            StringComparison.Ordinal);

    private static bool LatestDriftJobSucceeded(
        IReadOnlyList<GitHubWorkflowJob> jobs)
    {
        List<GitHubWorkflowJob> driftJobs = [];
        foreach (GitHubWorkflowJob job in jobs)
        {
            if (string.Equals(
                    job.Name,
                    DriftJobName,
                    StringComparison.Ordinal))
            {
                driftJobs.Add(job);
            }
        }

        if (driftJobs.Count == 0)
        {
            return false;
        }

        int latestAttempt = driftJobs.Max(static job => job.RunAttempt);
        foreach (GitHubWorkflowJob job in driftJobs)
        {
            if (job.RunAttempt == latestAttempt &&
                !string.Equals(
                    job.Conclusion,
                    "success",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNewer(
        QualifiedFixtureBundle candidate,
        QualifiedFixtureBundle current)
    {
        int comparison = candidate.RunCreatedAt.CompareTo(
            current.RunCreatedAt);
        if (comparison == 0)
        {
            comparison = candidate.Bundle.RunId.CompareTo(
                current.Bundle.RunId);
        }

        if (comparison == 0)
        {
            comparison = candidate.Bundle.ArtifactId.CompareTo(
                current.Bundle.ArtifactId);
        }

        return comparison > 0;
    }

    private sealed record QualifiedFixtureBundle(
        LocatedFixtureBundle Bundle,
        DateTimeOffset RunCreatedAt);
}

internal sealed record LocatedFixtureBundle(
    long ArtifactId,
    string ArtifactName,
    string Digest,
    long RunId,
    string RunUrl,
    string CorpusBlob,
    DateTimeOffset ExpiresAt);
