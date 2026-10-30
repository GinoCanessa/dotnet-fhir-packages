// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Validation;

internal interface IReleaseRecoveryVerifier
{
    Task<RecoveryVerificationResult> VerifyAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseRecoveryVerifier : IReleaseRecoveryVerifier
{
    internal const string ApprovalDirectory = "docs/releases/approvals";
    internal const string PackJobName = "pack-release-candidate";
    internal const string ReleaseWorkflowPath =
        ".github/workflows/nuget-generator.yaml";
    internal const string SourceCiJobPrefix = "exact-source-ci / ";

    private const string OriginMain = "origin/main";

    private static readonly RecoveryRule s_releasePublishedRule =
        new("R1", "release-published");
    private static readonly RecoveryRule s_originalRunRule =
        new("R2", "original-run");
    private static readonly RecoveryRule s_sourceCiRule =
        new("R3", "source-ci");
    private static readonly RecoveryRule s_retainedRule =
        new("R4", "retained");
    private static readonly RecoveryRule s_approvalRule =
        new("R5", "approval");

    private static readonly Regex ApprovalCommitRegex = new(
        "^[0-9a-f]{40}\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DigestRegex = new(
        "^sha256:[0-9a-f]{64}\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ObjectIdRegex = new(
        "^(?:[0-9a-f]{40}|[0-9a-f]{64})\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReleaseGitClient _gitClient;
    private readonly IReleaseGitObjectClient _gitObjectClient;
    private readonly IReleaseGitHubClient _gitHubClient;

    internal ReleaseRecoveryVerifier(
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

    internal static bool IsPublished(
        [NotNullWhen(true)] GitHubRelease? release) =>
        release is { Draft: false };

    internal static string FormatOutcome(RecoveryRuleOutcome outcome) =>
        outcome switch
        {
            RecoveryRuleOutcome.Passed => "passed",
            RecoveryRuleOutcome.Failed => "failed",
            RecoveryRuleOutcome.Reported => "reported",
            RecoveryRuleOutcome.NotEvaluated => "not-evaluated",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown recovery rule outcome."),
        };

    public async Task<RecoveryVerificationResult> VerifyAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        ReleaseEvaluation release = await EvaluateReleaseAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        RecoveryRuleResult originalRun = await EvaluateOriginalRunAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);

        // R3 and R4 read the original run's own records, which say nothing
        // about this release unless R2 bound that run to it.
        bool originalRunPassed =
            originalRun.Outcome == RecoveryRuleOutcome.Passed;
        SourceCiEvaluation sourceCi = originalRunPassed
            ? await EvaluateSourceCiAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : new SourceCiEvaluation(
                CreateNotEvaluated(s_sourceCiRule, s_originalRunRule),
                []);
        RetainedEvaluation retained = originalRunPassed
            ? await EvaluateRetainedAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : new RetainedEvaluation(
                CreateNotEvaluated(s_retainedRule, s_originalRunRule),
                null,
                null);
        ApprovalEvaluation approval = await EvaluateApprovalAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return new RecoveryVerificationResult(
            [
                release.Rule,
                originalRun,
                sourceCi.Rule,
                retained.Rule,
                approval.Rule,
            ],
            release.Published,
            retained.Candidate,
            retained.Bundle,
            sourceCi.JobUrls,
            approval.Author);
    }

    private static void ValidateRequest(RecoveryRequest request)
    {
        ReleaseVersionValidationCommon.ParseCanonicalThreeComponentVersion(
            request.Version);

        // R1 and R5 read the tag while R4 reads the version, so the two must
        // name the same release.
        string expectedTag = $"v{request.Version}";
        if (!string.Equals(
                request.Tag,
                expectedTag,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"Tag '{request.Tag}' must exactly match '{expectedTag}'.");
        }

        if (request.RecoverRunId < 1)
        {
            throw new ReleaseValidationException(
                $"Recover run id {FormatId(request.RecoverRunId)} must be at least one.");
        }
    }

    private async Task<ReleaseEvaluation> EvaluateReleaseAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        bool published = false;
        string detail;
        try
        {
            GitHubRelease? release =
                await _gitHubClient.GetPublishedReleaseAsync(
                    request.Repository,
                    request.Tag,
                    cancellationToken).ConfigureAwait(false);
            published = IsPublished(release);
            detail = release is null
                ? $"GitHub Release {request.Tag} is not published (no Release exists for the tag)."
                : release.Draft
                    ? $"GitHub Release {request.Tag} is not published (the Release is a draft)."
                    : $"GitHub Release {request.Tag} is published: {release.HtmlUrl}";
        }
        catch (ReleaseValidationException ex)
        {
            detail =
                $"GitHub Release {request.Tag} could not be read: {ex.Message}";
        }

        // A dry run can never reach publication, so R1 only reports there.
        if (request.DryRun)
        {
            return new ReleaseEvaluation(
                Create(
                    s_releasePublishedRule,
                    RecoveryRuleOutcome.Reported,
                    $"{detail} Dry run: reported, not enforced."),
                published);
        }

        return new ReleaseEvaluation(
            Create(
                s_releasePublishedRule,
                published
                    ? RecoveryRuleOutcome.Passed
                    : RecoveryRuleOutcome.Failed,
                detail),
            published);
    }

    private async Task<RecoveryRuleResult> EvaluateOriginalRunAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string runId = FormatId(request.RecoverRunId);
        GitHubWorkflowRun run;
        try
        {
            run = await _gitHubClient.GetWorkflowRunAsync(
                    request.Repository,
                    request.RecoverRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReleaseValidationException ex)
        {
            // The client reports a run that does not exist (HTTP 404) by
            // throwing, like every other failed request.
            return Create(
                s_originalRunRule,
                RecoveryRuleOutcome.Failed,
                $"Workflow run {runId} could not be read: {ex.Message}");
        }

        List<string> problems = [];
        if (!string.Equals(
                run.RepositoryFullName,
                request.Repository,
                StringComparison.Ordinal))
        {
            problems.Add(
                $"repository is '{run.RepositoryFullName}', not '{request.Repository}'");
        }

        if (!string.Equals(
                run.Path,
                ReleaseWorkflowPath,
                StringComparison.Ordinal))
        {
            problems.Add(
                $"workflow is '{run.Path}', not '{ReleaseWorkflowPath}'");
        }

        if (!string.Equals(
                run.Status,
                "completed",
                StringComparison.Ordinal))
        {
            problems.Add(
                $"status is {Describe(run.Status)}, not 'completed'");
        }

        if (!string.Equals(
                run.HeadSha,
                request.Commit,
                StringComparison.Ordinal))
        {
            problems.Add(
                $"head_sha is '{run.HeadSha}', not the release commit '{request.Commit}'");
        }

        bool isRelease = string.Equals(
            run.Event,
            "release",
            StringComparison.Ordinal);
        bool isDispatch = string.Equals(
            run.Event,
            "workflow_dispatch",
            StringComparison.Ordinal);
        if (!isRelease && !(request.DryRun && isDispatch))
        {
            problems.Add(
                request.DryRun
                    ? $"event is '{run.Event}', not 'release' or 'workflow_dispatch'"
                    : isDispatch
                        ? "event is 'workflow_dispatch', not 'release' (a workflow_dispatch original is accepted only in a dry run)"
                        : $"event is '{run.Event}', not 'release'");
        }

        if (problems.Count > 0)
        {
            return Create(
                s_originalRunRule,
                RecoveryRuleOutcome.Failed,
                $"Workflow run {runId} is not an eligible original release run: {string.Join("; ", problems)}.");
        }

        string dryRunNote = isDispatch
            ? " (accepted in a dry run)"
            : string.Empty;
        return Create(
            s_originalRunRule,
            RecoveryRuleOutcome.Passed,
            $"Workflow run {runId} is a completed '{run.Event}' run{dryRunNote} of {ReleaseWorkflowPath} at {run.HeadSha}: {run.HtmlUrl}");
    }

    private async Task<SourceCiEvaluation> EvaluateSourceCiAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string runId = FormatId(request.RecoverRunId);
        IReadOnlyList<GitHubWorkflowJob> jobs;
        try
        {
            jobs = await _gitHubClient.ListWorkflowRunJobsAsync(
                    request.Repository,
                    request.RecoverRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReleaseValidationException ex)
        {
            return new SourceCiEvaluation(
                Create(
                    s_sourceCiRule,
                    RecoveryRuleOutcome.Failed,
                    $"Jobs of workflow run {runId} could not be read: {ex.Message}"),
                []);
        }

        List<GitHubWorkflowJob> sourceCiJobs = [];
        List<GitHubWorkflowJob> packJobs = [];
        foreach (GitHubWorkflowJob job in SelectLatestAttempts(jobs))
        {
            if (job.Name.StartsWith(
                    SourceCiJobPrefix,
                    StringComparison.Ordinal))
            {
                sourceCiJobs.Add(job);
            }
            else if (string.Equals(
                         job.Name,
                         PackJobName,
                         StringComparison.Ordinal))
            {
                packJobs.Add(job);
            }
        }

        List<string> problems = [];
        if (sourceCiJobs.Count == 0)
        {
            problems.Add(
                $"no job name starts with '{SourceCiJobPrefix}'");
        }

        AddUnsuccessfulJobs(sourceCiJobs, problems);
        if (packJobs.Count == 0)
        {
            problems.Add($"no job is named '{PackJobName}'");
        }

        AddUnsuccessfulJobs(packJobs, problems);
        if (problems.Count > 0)
        {
            return new SourceCiEvaluation(
                Create(
                    s_sourceCiRule,
                    RecoveryRuleOutcome.Failed,
                    $"Source CI of workflow run {runId} did not pass: {string.Join("; ", problems)}."),
                []);
        }

        List<string> jobUrls = [];
        foreach (GitHubWorkflowJob job in sourceCiJobs)
        {
            if (!string.IsNullOrWhiteSpace(job.HtmlUrl))
            {
                jobUrls.Add(job.HtmlUrl);
            }
        }

        return new SourceCiEvaluation(
            Create(
                s_sourceCiRule,
                RecoveryRuleOutcome.Passed,
                $"All {sourceCiJobs.Count.ToString(CultureInfo.InvariantCulture)} '{SourceCiJobPrefix}*' jobs and '{PackJobName}' concluded 'success' in their latest attempts."),
            jobUrls);
    }

    private async Task<RetainedEvaluation> EvaluateRetainedAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string runId = FormatId(request.RecoverRunId);
        IReadOnlyList<GitHubArtifact> artifacts;
        try
        {
            artifacts = await _gitHubClient.ListWorkflowRunArtifactsAsync(
                    request.Repository,
                    request.RecoverRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReleaseValidationException ex)
        {
            return new RetainedEvaluation(
                Create(
                    s_retainedRule,
                    RecoveryRuleOutcome.Failed,
                    $"Artifacts of workflow run {runId} could not be read: {ex.Message}"),
                null,
                null);
        }

        string candidateName = $"fhir-pkg-{request.Version}-candidate";
        string bundleName = $"fhir-pkg-{request.Version}-fixture-bundle";
        List<string> problems = [];
        GitHubArtifact? candidate = SelectRetainedArtifact(
            artifacts,
            candidateName,
            null,
            problems);
        GitHubArtifact? bundle = SelectRetainedArtifact(
            artifacts,
            bundleName,
            "the original run predates frozen qualification inputs",
            problems);
        if (candidate is null || bundle is null)
        {
            return new RetainedEvaluation(
                Create(
                    s_retainedRule,
                    RecoveryRuleOutcome.Failed,
                    $"Workflow run {runId} does not retain the original release inputs: {string.Join("; ", problems)}."),
                null,
                null);
        }

        return new RetainedEvaluation(
            Create(
                s_retainedRule,
                RecoveryRuleOutcome.Passed,
                $"Candidate '{candidateName}' (artifact {FormatId(candidate.Id)}, {candidate.Digest}) and qualification inputs '{bundleName}' (artifact {FormatId(bundle.Id)}, {bundle.Digest}) are retained."),
            candidate,
            bundle);
    }

    private async Task<ApprovalEvaluation> EvaluateApprovalAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string? approvalCommit = request.ApprovalCommit;
        if (string.IsNullOrEmpty(approvalCommit))
        {
            // Absent is not a failed prerequisite, so the rule reports rather
            // than being not-evaluated.
            return new ApprovalEvaluation(
                Create(
                    s_approvalRule,
                    RecoveryRuleOutcome.Reported,
                    "No approval commit was named; the original qualification inputs are used unchanged."),
                null);
        }

        if (!ApprovalCommitRegex.IsMatch(approvalCommit))
        {
            return RefuseApproval(
                $"Approval commit '{approvalCommit}' must be a full 40-character lowercase hexadecimal commit id.");
        }

        ReleaseGitCommandResult fetchOriginMainResult =
            await _gitClient.FetchOriginMainAsync(
                cancellationToken).ConfigureAwait(false);
        if (fetchOriginMainResult.ExitCode != 0)
        {
            return RefuseApproval("Unable to fetch 'origin/main'.");
        }

        ReleaseGitCommandResult ancestryResult =
            await _gitClient.CheckAncestorAsync(
                approvalCommit,
                OriginMain,
                cancellationToken).ConfigureAwait(false);
        if (ancestryResult.ExitCode == 1)
        {
            return RefuseApproval(
                $"Approval commit {approvalCommit} is not an ancestor of origin/main.");
        }

        if (ancestryResult.ExitCode != 0)
        {
            return RefuseApproval(
                $"Unable to verify that approval commit {approvalCommit} is an ancestor of origin/main.");
        }

        string approvalPath = $"{ApprovalDirectory}/{request.Tag}.json";
        string? approvalBlob = await ResolveBlobIdAsync(
                approvalCommit,
                approvalPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (approvalBlob is null)
        {
            return RefuseApproval(
                $"Approval commit {approvalCommit} does not contain {approvalPath}.");
        }

        // origin/main is authoritative: a later deletion revokes the approval
        // and a later edit supersedes it, so an older commit cannot resurrect
        // a withdrawn approval.
        string? mainBlob = await ResolveBlobIdAsync(
                OriginMain,
                approvalPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (mainBlob is null)
        {
            return RefuseApproval(
                $"The approval is revoked: {approvalPath} was deleted from origin/main after approval commit {approvalCommit}.");
        }

        if (!string.Equals(
                approvalBlob,
                mainBlob,
                StringComparison.Ordinal))
        {
            return RefuseApproval(
                $"The approval is superseded: {approvalPath} was edited on origin/main after approval commit {approvalCommit} (blob {approvalBlob} at the approval commit, {mainBlob} on origin/main).");
        }

        ReleaseGitCommandResult authorResult =
            await _gitObjectClient.ResolveCommitAuthorAsync(
                approvalCommit,
                cancellationToken).ConfigureAwait(false);
        string author = authorResult.Output;
        if (authorResult.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(author))
        {
            return RefuseApproval(
                $"Unable to resolve the author of approval commit {approvalCommit}.");
        }

        return new ApprovalEvaluation(
            Create(
                s_approvalRule,
                RecoveryRuleOutcome.Passed,
                $"{approvalPath} at approval commit {approvalCommit} (blob {approvalBlob}, authored by {author}) is unchanged on origin/main."),
            author);
    }

    private async Task<string?> ResolveBlobIdAsync(
        string revision,
        string path,
        CancellationToken cancellationToken)
    {
        ReleaseGitCommandResult result =
            await _gitObjectClient.ResolveBlobIdAsync(
                revision,
                path,
                cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && ObjectIdRegex.IsMatch(result.Output)
            ? result.Output
            : null;
    }

    private static List<GitHubWorkflowJob> SelectLatestAttempts(
        IReadOnlyList<GitHubWorkflowJob> jobs)
    {
        // A re-run attempt lists only the jobs it re-ran, so the latest
        // attempt is chosen per job name rather than per run.
        Dictionary<string, int> latestAttempts = new(StringComparer.Ordinal);
        foreach (GitHubWorkflowJob job in jobs)
        {
            if (!latestAttempts.TryGetValue(
                    job.Name,
                    out int latestAttempt) ||
                job.RunAttempt > latestAttempt)
            {
                latestAttempts[job.Name] = job.RunAttempt;
            }
        }

        return
        [
            .. jobs
                .Where(job => job.RunAttempt == latestAttempts[job.Name])
                .OrderBy(static job => job.Name, StringComparer.Ordinal)
                .ThenBy(static job => job.Id),
        ];
    }

    private static void AddUnsuccessfulJobs(
        List<GitHubWorkflowJob> jobs,
        List<string> problems)
    {
        foreach (GitHubWorkflowJob job in jobs)
        {
            if (string.Equals(
                    job.Conclusion,
                    "success",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string attempt =
                job.RunAttempt.ToString(CultureInfo.InvariantCulture);
            problems.Add(
                job.Conclusion is null
                    ? $"job '{job.Name}' (attempt {attempt}) has not concluded (status '{job.Status}')"
                    : $"job '{job.Name}' (attempt {attempt}) concluded '{job.Conclusion}', not 'success'");
        }
    }

    private static GitHubArtifact? SelectRetainedArtifact(
        IReadOnlyList<GitHubArtifact> artifacts,
        string name,
        string? missingReason,
        List<string> problems)
    {
        GitHubArtifact[] matches =
        [
            .. artifacts.Where(
                artifact => string.Equals(
                    artifact.Name,
                    name,
                    StringComparison.Ordinal)),
        ];
        if (matches.Length == 0)
        {
            problems.Add(
                missingReason is null
                    ? $"no artifact is named '{name}'"
                    : $"no artifact is named '{name}' ({missingReason})");
            return null;
        }

        if (matches.Length > 1)
        {
            problems.Add(
                $"{matches.Length.ToString(CultureInfo.InvariantCulture)} artifacts are named '{name}', not exactly one");
            return null;
        }

        GitHubArtifact artifact = matches[0];
        string artifactId = FormatId(artifact.Id);
        bool retained = true;
        if (artifact.Expired)
        {
            problems.Add($"artifact '{name}' ({artifactId}) has expired");
            retained = false;
        }

        if (artifact.Digest is null)
        {
            problems.Add(
                $"artifact '{name}' ({artifactId}) has no recorded digest");
            retained = false;
        }
        else if (!DigestRegex.IsMatch(artifact.Digest))
        {
            problems.Add(
                $"artifact '{name}' ({artifactId}) has a malformed digest '{artifact.Digest}'");
            retained = false;
        }

        return retained ? artifact : null;
    }

    private static ApprovalEvaluation RefuseApproval(string detail) =>
        new(
            Create(s_approvalRule, RecoveryRuleOutcome.Failed, detail),
            null);

    private static RecoveryRuleResult CreateNotEvaluated(
        RecoveryRule rule,
        RecoveryRule prerequisite) =>
        Create(
            rule,
            RecoveryRuleOutcome.NotEvaluated,
            $"Not evaluated because {prerequisite.Id} {prerequisite.Name} did not pass.");

    private static RecoveryRuleResult Create(
        RecoveryRule rule,
        RecoveryRuleOutcome outcome,
        string detail) =>
        new(rule.Id, rule.Name, outcome, detail);

    private static string Describe(string? value) =>
        value is null ? "missing" : $"'{value}'";

    private static string FormatId(long id) =>
        id.ToString(CultureInfo.InvariantCulture);

    private sealed record RecoveryRule(
        string Id,
        string Name);

    private sealed record ReleaseEvaluation(
        RecoveryRuleResult Rule,
        bool Published);

    private sealed record SourceCiEvaluation(
        RecoveryRuleResult Rule,
        IReadOnlyList<string> JobUrls);

    private sealed record RetainedEvaluation(
        RecoveryRuleResult Rule,
        GitHubArtifact? Candidate,
        GitHubArtifact? Bundle);

    private sealed record ApprovalEvaluation(
        RecoveryRuleResult Rule,
        string? Author);
}

internal sealed record RecoveryRequest(
    string Repository,
    string Tag,
    string Version,
    string Commit,
    long RecoverRunId,
    string? ApprovalCommit,
    bool DryRun);

internal sealed record RecoveryVerificationResult(
    IReadOnlyList<RecoveryRuleResult> Rules,
    bool ReleasePublished,
    GitHubArtifact? CandidateArtifact,
    GitHubArtifact? BundleArtifact,
    IReadOnlyList<string> SourceCiJobs,
    string? ApprovalAuthor)
{
    // A not-evaluated rule never authorizes recovery.
    internal bool Authorized =>
        Rules.Count > 0 &&
        Rules.All(static rule =>
            rule.Outcome is RecoveryRuleOutcome.Passed or
                RecoveryRuleOutcome.Reported);
}

internal sealed record RecoveryRuleResult(
    string Id,
    string Name,
    RecoveryRuleOutcome Outcome,
    string Detail);

internal enum RecoveryRuleOutcome
{
    Passed,
    Failed,
    Reported,
    NotEvaluated,
}
