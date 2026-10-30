// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseRecoveryVerifierTests
{
    private const string ApprovalAuthor =
        "Release Approver <approver@example.test>";
    private const string ApprovalBlob =
        "1111111111111111111111111111111111111111";
    private const string ApprovalCommit =
        "abcdef0123456789abcdef0123456789abcdef01";
    private const string ApprovalPath =
        "docs/releases/approvals/v2099.101.1.json";
    private const string BundleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string BundleName = "fhir-pkg-2099.101.1-fixture-bundle";
    private const string CandidateDigest =
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string CandidateName = "fhir-pkg-2099.101.1-candidate";
    private const string Commit =
        "0123456789abcdef0123456789abcdef01234567";
    private const string NotEvaluatedDetail =
        "Not evaluated because R2 original-run did not pass.";
    private const string ReleaseUrl =
        "https://github.test/octo-org/dotnet-fhir-packages/releases/tag/v2099.101.1";
    private const string Repository = "octo-org/dotnet-fhir-packages";
    private const long RunId = 4242;
    private const string RunUrl =
        "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/4242";
    private const string SourceCiBuildJob =
        "exact-source-ci / build-and-test-ubuntu-latest";
    private const string SourceCiHardenedJob =
        "exact-source-ci / hardened-ubuntu-latest-net8.0";
    private const string Tag = "v2099.101.1";
    private const string Version = "2099.101.1";

    private static readonly DateTimeOffset s_createdAt =
        new(2026, 10, 16, 14, 17, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifyAsync_PassesEveryRuleForAnEligibleOriginalRun()
    {
        RecoveryScenario scenario = new();

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules.ShouldBe(
            [
                new RecoveryRuleResult(
                    "R1",
                    "release-published",
                    RecoveryRuleOutcome.Passed,
                    $"GitHub Release {Tag} is published: {ReleaseUrl}"),
                new RecoveryRuleResult(
                    "R2",
                    "original-run",
                    RecoveryRuleOutcome.Passed,
                    $"Workflow run 4242 is a completed 'release' run of .github/workflows/nuget-generator.yaml at {Commit}: {RunUrl}"),
                new RecoveryRuleResult(
                    "R3",
                    "source-ci",
                    RecoveryRuleOutcome.Passed,
                    "All 2 'exact-source-ci / *' jobs and 'pack-release-candidate' concluded 'success' in their latest attempts."),
                new RecoveryRuleResult(
                    "R4",
                    "retained",
                    RecoveryRuleOutcome.Passed,
                    $"Candidate '{CandidateName}' (artifact 21, {CandidateDigest}) and qualification inputs '{BundleName}' (artifact 22, {BundleDigest}) are retained."),
                new RecoveryRuleResult(
                    "R5",
                    "approval",
                    RecoveryRuleOutcome.Passed,
                    $"{ApprovalPath} at approval commit {ApprovalCommit} (blob {ApprovalBlob}, authored by {ApprovalAuthor}) is unchanged on origin/main."),
            ]);
        result.Authorized.ShouldBeTrue();
        result.ReleasePublished.ShouldBeTrue();
        result.CandidateArtifact.ShouldBe(scenario.Artifacts[0]);
        result.BundleArtifact.ShouldBe(scenario.Artifacts[1]);
        result.SourceCiJobs.ShouldBe(
            [$"{RunUrl}/job/12", $"{RunUrl}/job/13"]);
        result.ApprovalAuthor.ShouldBe(ApprovalAuthor);
        scenario.GitHubClient.Verify(
            client => client.GetPublishedReleaseAsync(
                Repository,
                Tag,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitHubClient.Verify(
            client => client.GetWorkflowRunAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitHubClient.Verify(
            client => client.ListWorkflowRunJobsAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitHubClient.Verify(
            client => client.ListWorkflowRunArtifactsAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitClient.Verify(
            client => client.FetchOriginMainAsync(
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitClient.Verify(
            client => client.CheckAncestorAsync(
                ApprovalCommit,
                "origin/main",
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitObjectClient.Verify(
            client => client.ResolveBlobIdAsync(
                ApprovalCommit,
                ApprovalPath,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitObjectClient.Verify(
            client => client.ResolveBlobIdAsync(
                "origin/main",
                ApprovalPath,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitObjectClient.Verify(
            client => client.ResolveCommitAuthorAsync(
                ApprovalCommit,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task VerifyAsync_ReportsAnAbsentApprovalWithoutTouchingGit(
        string? approvalCommit)
    {
        RecoveryScenario scenario = new();

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(approvalCommit);

        result.Rules[4].ShouldBe(
            new RecoveryRuleResult(
                "R5",
                "approval",
                RecoveryRuleOutcome.Reported,
                "No approval commit was named; the original qualification inputs are used unchanged."));
        result.Rules.Take(4)
            .Select(static rule => rule.Outcome)
            .ToArray()
            .ShouldAllBe(static outcome =>
                outcome == RecoveryRuleOutcome.Passed);
        result.Authorized.ShouldBeTrue();
        result.ApprovalAuthor.ShouldBeNull();
        scenario.GitClient.VerifyNoOtherCalls();
        scenario.GitObjectClient.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("draft")]
    public async Task VerifyAsync_RefusesAReleaseThatIsNotPublished(
        string releaseState)
    {
        RecoveryScenario scenario = new();
        scenario.Release = releaseState == "draft"
            ? CreateRelease(draft: true)
            : null;
        string expectedDetail = releaseState == "draft"
            ? $"GitHub Release {Tag} is not published (the Release is a draft)."
            : $"GitHub Release {Tag} is not published (no Release exists for the tag).";

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[0].ShouldBe(
            new RecoveryRuleResult(
                "R1",
                "release-published",
                RecoveryRuleOutcome.Failed,
                expectedDetail));
        result.Rules.Skip(1)
            .Select(static rule => rule.Outcome)
            .ToArray()
            .ShouldAllBe(static outcome =>
                outcome == RecoveryRuleOutcome.Passed);
        result.ReleasePublished.ShouldBeFalse();
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_RefusesAReleaseThatCannotBeRead()
    {
        RecoveryScenario scenario = new();
        scenario.GitHubClient
            .Setup(client => client.GetPublishedReleaseAsync(
                Repository,
                Tag,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseValidationException(
                "GitHub API GET /repos/octo-org/dotnet-fhir-packages/releases/tags/v2099.101.1 returned HTTP 500."));

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[0].ShouldBe(
            new RecoveryRuleResult(
                "R1",
                "release-published",
                RecoveryRuleOutcome.Failed,
                $"GitHub Release {Tag} could not be read: GitHub API GET /repos/octo-org/dotnet-fhir-packages/releases/tags/v2099.101.1 returned HTTP 500."));
        result.ReleasePublished.ShouldBeFalse();
        result.Authorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tag-mismatch")]
    [InlineData("non-canonical-version")]
    [InlineData("run-id-below-one")]
    public async Task VerifyAsync_RejectsAnInconsistentRequestBeforeAnyLookup(
        string defect)
    {
        RecoveryRequest request = CreateRequest();
        string expectedMessage;
        switch (defect)
        {
            case "tag-mismatch":
                request = request with { Tag = "v2099.101.2" };
                expectedMessage =
                    "Tag 'v2099.101.2' must exactly match 'v2099.101.1'.";
                break;
            case "non-canonical-version":
                request = request with
                {
                    Version = "2099.0101.1",
                    Tag = "v2099.0101.1",
                };
                expectedMessage =
                    "Version '2099.0101.1' is not in canonical numeric form.";
                break;
            case "run-id-below-one":
                request = request with { RecoverRunId = 0 };
                expectedMessage = "Recover run id 0 must be at least one.";
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown defect '{defect}'.");
        }

        RecoveryScenario scenario = new();

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => scenario.VerifyRequestAsync(request));

        exception.Message.ShouldBe(expectedMessage);
        scenario.GitHubClient.VerifyNoOtherCalls();
        scenario.GitClient.VerifyNoOtherCalls();
        scenario.GitObjectClient.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("workflow-path")]
    [InlineData("status-in-progress")]
    [InlineData("status-missing")]
    [InlineData("head-sha")]
    [InlineData("push-event")]
    [InlineData("pull-request-event")]
    public async Task VerifyAsync_RefusesAnOriginalRunFieldMismatch(
        string mismatch)
    {
        RecoveryScenario scenario = new();
        string expectedProblem;
        switch (mismatch)
        {
            case "repository":
                scenario.Run = scenario.Run with
                {
                    RepositoryFullName = "someone-else/dotnet-fhir-packages",
                };
                expectedProblem =
                    $"repository is 'someone-else/dotnet-fhir-packages', not '{Repository}'";
                break;
            case "workflow-path":
                scenario.Run = scenario.Run with
                {
                    Path = ".github/workflows/fixture-drift.yaml",
                };
                expectedProblem =
                    "workflow is '.github/workflows/fixture-drift.yaml', not '.github/workflows/nuget-generator.yaml'";
                break;
            case "status-in-progress":
                scenario.Run = scenario.Run with { Status = "in_progress" };
                expectedProblem = "status is 'in_progress', not 'completed'";
                break;
            case "status-missing":
                scenario.Run = scenario.Run with { Status = null };
                expectedProblem = "status is missing, not 'completed'";
                break;
            case "head-sha":
                scenario.Run = scenario.Run with
                {
                    HeadSha = "fedcba9876543210fedcba9876543210fedcba98",
                };
                expectedProblem =
                    $"head_sha is 'fedcba9876543210fedcba9876543210fedcba98', not the release commit '{Commit}'";
                break;
            case "push-event":
                scenario.Run = scenario.Run with { Event = "push" };
                expectedProblem = "event is 'push', not 'release'";
                break;
            case "pull-request-event":
                scenario.Run = scenario.Run with { Event = "pull_request" };
                expectedProblem = "event is 'pull_request', not 'release'";
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown mismatch '{mismatch}'.");
        }

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[1].ShouldBe(
            new RecoveryRuleResult(
                "R2",
                "original-run",
                RecoveryRuleOutcome.Failed,
                $"Workflow run 4242 is not an eligible original release run: {expectedProblem}."));
        AssertSourceCiAndRetainedNotEvaluated(scenario, result);
        result.Rules[0].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
        result.Rules[4].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
    }

    [Fact]
    public async Task VerifyAsync_ListsEveryOriginalRunMismatch()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with
        {
            Path = ".github/workflows/build-and-test.yaml",
            Event = "push",
            Status = "queued",
        };

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[1].Detail.ShouldBe(
            "Workflow run 4242 is not an eligible original release run: workflow is '.github/workflows/build-and-test.yaml', not '.github/workflows/nuget-generator.yaml'; status is 'queued', not 'completed'; event is 'push', not 'release'.");
        result.Rules[1].Outcome.ShouldBe(RecoveryRuleOutcome.Failed);
    }

    [Fact]
    public async Task VerifyAsync_RefusesAnOriginalRunThatDoesNotExist()
    {
        RecoveryScenario scenario = new();
        scenario.GitHubClient
            .Setup(client => client.GetWorkflowRunAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseValidationException(
                "GitHub API GET /repos/octo-org/dotnet-fhir-packages/actions/runs/4242 returned HTTP 404."));

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[1].ShouldBe(
            new RecoveryRuleResult(
                "R2",
                "original-run",
                RecoveryRuleOutcome.Failed,
                "Workflow run 4242 could not be read: GitHub API GET /repos/octo-org/dotnet-fhir-packages/actions/runs/4242 returned HTTP 404."));
        AssertSourceCiAndRetainedNotEvaluated(scenario, result);
    }

    [Fact]
    public async Task VerifyAsync_MarksSourceCiAndRetentionNotEvaluatedWhenTheOriginalRunFails()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with { Event = "schedule" };

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules
            .Select(static rule =>
                $"{rule.Id} {ReleaseRecoveryVerifier.FormatOutcome(rule.Outcome)}")
            .ToArray()
            .ShouldBe(
                [
                    "R1 passed",
                    "R2 failed",
                    "R3 not-evaluated",
                    "R4 not-evaluated",
                    "R5 passed",
                ]);
        AssertSourceCiAndRetainedNotEvaluated(scenario, result);
        result.ReleasePublished.ShouldBeTrue();
        result.ApprovalAuthor.ShouldBe(ApprovalAuthor);
    }

    [Fact]
    public async Task VerifyAsync_RefusesASourceCiJobThatFailedAndWasNotRetried()
    {
        RecoveryScenario scenario = new();
        scenario.Jobs =
        [
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob, conclusion: "failure"),
            CreateJob(14, "pack-release-candidate"),
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                $"Source CI of workflow run 4242 did not pass: job '{SourceCiHardenedJob}' (attempt 1) concluded 'failure', not 'success'."));
        result.SourceCiJobs.ShouldBeEmpty();
        result.Rules[3].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_AcceptsASourceCiJobRetriedToSuccess()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with { RunAttempt = 2 };
        scenario.Jobs =
        [
            CreateJob(17, SourceCiHardenedJob, runAttempt: 2),
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob, conclusion: "failure"),
            CreateJob(14, "pack-release-candidate"),
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Passed,
                "All 2 'exact-source-ci / *' jobs and 'pack-release-candidate' concluded 'success' in their latest attempts."));
        result.SourceCiJobs.ShouldBe(
            [$"{RunUrl}/job/12", $"{RunUrl}/job/17"]);
        result.Authorized.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_JudgesOnlyTheLatestAttemptOfEachJob()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with { RunAttempt = 2 };
        scenario.Jobs =
        [
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob),
            CreateJob(17, SourceCiHardenedJob, runAttempt: 2, conclusion: "cancelled"),
            CreateJob(14, "pack-release-candidate"),
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                $"Source CI of workflow run 4242 did not pass: job '{SourceCiHardenedJob}' (attempt 2) concluded 'cancelled', not 'success'."));
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_RefusesARunWithoutSourceCiJobs()
    {
        RecoveryScenario scenario = new();
        scenario.Jobs =
        [
            CreateJob(11, "validate-release"),
            CreateJob(18, "exact-source-ci"),
            CreateJob(14, "pack-release-candidate"),
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                "Source CI of workflow run 4242 did not pass: no job name starts with 'exact-source-ci / '."));
        result.Authorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData(null)]
    public async Task VerifyAsync_RefusesAPackJobThatDidNotSucceed(
        string? packConclusion)
    {
        RecoveryScenario scenario = new();
        scenario.Jobs =
        [
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob),
            CreateJob(14, "pack-release-candidate", conclusion: packConclusion),
        ];
        string expectedProblem = packConclusion is null
            ? "job 'pack-release-candidate' (attempt 1) has not concluded (status 'in_progress')"
            : $"job 'pack-release-candidate' (attempt 1) concluded '{packConclusion}', not 'success'";

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                $"Source CI of workflow run 4242 did not pass: {expectedProblem}."));
        result.SourceCiJobs.ShouldBeEmpty();
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_RefusesARunWithoutAPackJob()
    {
        RecoveryScenario scenario = new();
        scenario.Jobs =
        [
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob),
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                "Source CI of workflow run 4242 did not pass: no job is named 'pack-release-candidate'."));
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_RefusesJobsThatCannotBeRead()
    {
        RecoveryScenario scenario = new();
        scenario.GitHubClient
            .Setup(client => client.ListWorkflowRunJobsAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseValidationException(
                "GitHub API GET /repos/octo-org/dotnet-fhir-packages/actions/runs/4242/jobs?filter=all&per_page=100 returned HTTP 502."));

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.Failed,
                "Jobs of workflow run 4242 could not be read: GitHub API GET /repos/octo-org/dotnet-fhir-packages/actions/runs/4242/jobs?filter=all&per_page=100 returned HTTP 502."));
        result.Rules[3].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
        result.Authorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData("missing-candidate")]
    [InlineData("missing-bundle")]
    [InlineData("expired-candidate")]
    [InlineData("expired-bundle")]
    [InlineData("candidate-without-digest")]
    [InlineData("bundle-without-digest")]
    [InlineData("malformed-candidate-digest")]
    [InlineData("duplicate-candidate")]
    [InlineData("duplicate-bundle")]
    public async Task VerifyAsync_RefusesAnArtifactThatIsNotRetained(
        string defect)
    {
        GitHubArtifact candidate =
            CreateArtifact(21, CandidateName, CandidateDigest);
        GitHubArtifact bundle = CreateArtifact(22, BundleName, BundleDigest);
        List<GitHubArtifact> artifacts = [candidate, bundle];
        string expectedProblem;
        switch (defect)
        {
            case "missing-candidate":
                artifacts.Remove(candidate);
                expectedProblem = $"no artifact is named '{CandidateName}'";
                break;
            case "missing-bundle":
                artifacts.Remove(bundle);
                expectedProblem =
                    $"no artifact is named '{BundleName}' (the original run predates frozen qualification inputs)";
                break;
            case "expired-candidate":
                artifacts[0] = candidate with { Expired = true };
                expectedProblem =
                    $"artifact '{CandidateName}' (21) has expired";
                break;
            case "expired-bundle":
                artifacts[1] = bundle with { Expired = true };
                expectedProblem = $"artifact '{BundleName}' (22) has expired";
                break;
            case "candidate-without-digest":
                artifacts[0] = candidate with { Digest = null };
                expectedProblem =
                    $"artifact '{CandidateName}' (21) has no recorded digest";
                break;
            case "bundle-without-digest":
                artifacts[1] = bundle with { Digest = null };
                expectedProblem =
                    $"artifact '{BundleName}' (22) has no recorded digest";
                break;
            case "malformed-candidate-digest":
                artifacts[0] = candidate with { Digest = "sha256:ABC123" };
                expectedProblem =
                    $"artifact '{CandidateName}' (21) has a malformed digest 'sha256:ABC123'";
                break;
            case "duplicate-candidate":
                artifacts.Add(candidate with { Id = 24 });
                expectedProblem =
                    $"2 artifacts are named '{CandidateName}', not exactly one";
                break;
            case "duplicate-bundle":
                artifacts.Add(bundle with { Id = 25 });
                expectedProblem =
                    $"2 artifacts are named '{BundleName}', not exactly one";
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown defect '{defect}'.");
        }

        RecoveryScenario scenario = new();
        scenario.Artifacts = artifacts;

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[3].ShouldBe(
            new RecoveryRuleResult(
                "R4",
                "retained",
                RecoveryRuleOutcome.Failed,
                $"Workflow run 4242 does not retain the original release inputs: {expectedProblem}."));
        result.CandidateArtifact.ShouldBeNull();
        result.BundleArtifact.ShouldBeNull();
        result.Rules[2].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_ListsEveryRetentionProblem()
    {
        RecoveryScenario scenario = new();
        scenario.Artifacts =
        [
            CreateArtifact(21, CandidateName, null) with { Expired = true },
        ];

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[3].Detail.ShouldBe(
            $"Workflow run 4242 does not retain the original release inputs: artifact '{CandidateName}' (21) has expired; artifact '{CandidateName}' (21) has no recorded digest; no artifact is named '{BundleName}' (the original run predates frozen qualification inputs).");
    }

    [Fact]
    public async Task VerifyAsync_RefusesArtifactsThatCannotBeRead()
    {
        RecoveryScenario scenario = new();
        scenario.GitHubClient
            .Setup(client => client.ListWorkflowRunArtifactsAsync(
                Repository,
                RunId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseValidationException(
                "GITHUB_TOKEN is required for ListWorkflowRunArtifacts."));

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[3].ShouldBe(
            new RecoveryRuleResult(
                "R4",
                "retained",
                RecoveryRuleOutcome.Failed,
                "Artifacts of workflow run 4242 could not be read: GITHUB_TOKEN is required for ListWorkflowRunArtifacts."));
        result.CandidateArtifact.ShouldBeNull();
        result.Authorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("abcdef0")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef012")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0g")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01\n")]
    [InlineData(" ")]
    public async Task VerifyAsync_RefusesAMalformedApprovalCommitWithoutTouchingGit(
        string approvalCommit)
    {
        RecoveryScenario scenario = new();

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(approvalCommit);

        result.Rules[4].ShouldBe(
            new RecoveryRuleResult(
                "R5",
                "approval",
                RecoveryRuleOutcome.Failed,
                $"Approval commit '{approvalCommit}' must be a full 40-character lowercase hexadecimal commit id."));
        result.ApprovalAuthor.ShouldBeNull();
        result.Authorized.ShouldBeFalse();
        scenario.GitClient.VerifyNoOtherCalls();
        scenario.GitObjectClient.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("fetch-fails")]
    [InlineData("not-on-main")]
    [InlineData("ancestry-unverifiable")]
    [InlineData("file-missing")]
    [InlineData("revoked")]
    [InlineData("superseded")]
    [InlineData("author-unresolved")]
    public async Task VerifyAsync_RefusesAnApprovalThatIsNotCurrentOnMain(
        string defect)
    {
        RecoveryScenario scenario = new();
        string expectedDetail;
        switch (defect)
        {
            case "fetch-fails":
                scenario.FetchOriginMainResult =
                    CreateGitResult(128, string.Empty);
                expectedDetail = "Unable to fetch 'origin/main'.";
                break;
            case "not-on-main":
                scenario.AncestorResult = CreateGitResult(1, string.Empty);
                expectedDetail =
                    $"Approval commit {ApprovalCommit} is not an ancestor of origin/main.";
                break;
            case "ancestry-unverifiable":
                scenario.AncestorResult =
                    CreateGitResult(128, string.Empty);
                expectedDetail =
                    $"Unable to verify that approval commit {ApprovalCommit} is an ancestor of origin/main.";
                break;
            case "file-missing":
                scenario.ApprovalBlobResult =
                    CreateGitResult(128, string.Empty);
                expectedDetail =
                    $"Approval commit {ApprovalCommit} does not contain {ApprovalPath}.";
                break;
            case "revoked":
                scenario.MainBlobResult = CreateGitResult(128, string.Empty);
                expectedDetail =
                    $"The approval is revoked: {ApprovalPath} was deleted from origin/main after approval commit {ApprovalCommit}.";
                break;
            case "superseded":
                scenario.MainBlobResult = CreateGitResult(
                    0,
                    "2222222222222222222222222222222222222222\n");
                expectedDetail =
                    $"The approval is superseded: {ApprovalPath} was edited on origin/main after approval commit {ApprovalCommit} (blob {ApprovalBlob} at the approval commit, 2222222222222222222222222222222222222222 on origin/main).";
                break;
            case "author-unresolved":
                scenario.AuthorResult = CreateGitResult(128, string.Empty);
                expectedDetail =
                    $"Unable to resolve the author of approval commit {ApprovalCommit}.";
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown defect '{defect}'.");
        }

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[4].ShouldBe(
            new RecoveryRuleResult(
                "R5",
                "approval",
                RecoveryRuleOutcome.Failed,
                expectedDetail));
        result.ApprovalAuthor.ShouldBeNull();
        result.Rules.Take(4)
            .Select(static rule => rule.Outcome)
            .ToArray()
            .ShouldAllBe(static outcome =>
                outcome == RecoveryRuleOutcome.Passed);
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_RefusedAncestryStopsBeforeReadingTheApproval()
    {
        RecoveryScenario scenario = new();
        scenario.AncestorResult = CreateGitResult(1, string.Empty);

        RecoveryVerificationResult result = await scenario.VerifyAsync();

        result.Rules[4].Outcome.ShouldBe(RecoveryRuleOutcome.Failed);
        scenario.GitObjectClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VerifyAsync_DryRunReportsTheReleaseAndAcceptsADispatchOriginal()
    {
        RecoveryScenario scenario = new();
        scenario.Release = null;
        scenario.Run = scenario.Run with { Event = "workflow_dispatch" };

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(approvalCommit: null, dryRun: true);

        result.Rules[0].ShouldBe(
            new RecoveryRuleResult(
                "R1",
                "release-published",
                RecoveryRuleOutcome.Reported,
                $"GitHub Release {Tag} is not published (no Release exists for the tag). Dry run: reported, not enforced."));
        result.Rules[1].ShouldBe(
            new RecoveryRuleResult(
                "R2",
                "original-run",
                RecoveryRuleOutcome.Passed,
                $"Workflow run 4242 is a completed 'workflow_dispatch' run (accepted in a dry run) of .github/workflows/nuget-generator.yaml at {Commit}: {RunUrl}"));
        result.Rules
            .Select(static rule =>
                ReleaseRecoveryVerifier.FormatOutcome(rule.Outcome))
            .ToArray()
            .ShouldBe(
                ["reported", "passed", "passed", "passed", "reported"]);
        result.Authorized.ShouldBeTrue();
        result.ReleasePublished.ShouldBeFalse();
        result.CandidateArtifact.ShouldBe(scenario.Artifacts[0]);
        result.BundleArtifact.ShouldBe(scenario.Artifacts[1]);
    }

    [Fact]
    public async Task VerifyAsync_RefusesADispatchOriginalOutsideADryRun()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with { Event = "workflow_dispatch" };

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(approvalCommit: null, dryRun: false);

        result.Rules[0].Outcome.ShouldBe(RecoveryRuleOutcome.Passed);
        result.Rules[1].ShouldBe(
            new RecoveryRuleResult(
                "R2",
                "original-run",
                RecoveryRuleOutcome.Failed,
                "Workflow run 4242 is not an eligible original release run: event is 'workflow_dispatch', not 'release' (a workflow_dispatch original is accepted only in a dry run)."));
        AssertSourceCiAndRetainedNotEvaluated(scenario, result);
    }

    [Theory]
    [InlineData("published")]
    [InlineData("draft")]
    [InlineData("unreadable")]
    public async Task VerifyAsync_DryRunOnlyReportsTheRelease(
        string releaseState)
    {
        RecoveryScenario scenario = new();
        string expectedDetail;
        bool expectedPublished = false;
        switch (releaseState)
        {
            case "published":
                expectedDetail =
                    $"GitHub Release {Tag} is published: {ReleaseUrl} Dry run: reported, not enforced.";
                expectedPublished = true;
                break;
            case "draft":
                scenario.Release = CreateRelease(draft: true);
                expectedDetail =
                    $"GitHub Release {Tag} is not published (the Release is a draft). Dry run: reported, not enforced.";
                break;
            case "unreadable":
                scenario.GitHubClient
                    .Setup(client => client.GetPublishedReleaseAsync(
                        Repository,
                        Tag,
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ReleaseValidationException(
                        "GitHub API GET /repos/octo-org/dotnet-fhir-packages/releases/tags/v2099.101.1 returned HTTP 500."));
                expectedDetail =
                    $"GitHub Release {Tag} could not be read: GitHub API GET /repos/octo-org/dotnet-fhir-packages/releases/tags/v2099.101.1 returned HTTP 500. Dry run: reported, not enforced.";
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown release state '{releaseState}'.");
        }

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(dryRun: true);

        result.Rules[0].ShouldBe(
            new RecoveryRuleResult(
                "R1",
                "release-published",
                RecoveryRuleOutcome.Reported,
                expectedDetail));
        result.ReleasePublished.ShouldBe(expectedPublished);
        result.Authorized.ShouldBeTrue();
    }

    [Theory]
    [InlineData("pull-request-event", 1)]
    [InlineData("head-sha", 1)]
    [InlineData("failed-source-ci", 2)]
    [InlineData("missing-bundle", 3)]
    [InlineData("revoked-approval", 4)]
    public async Task VerifyAsync_DryRunRelaxesNothingElse(
        string defect,
        int failedRuleIndex)
    {
        RecoveryScenario scenario = new();
        switch (defect)
        {
            case "pull-request-event":
                scenario.Run = scenario.Run with { Event = "pull_request" };
                break;
            case "head-sha":
                scenario.Run = scenario.Run with
                {
                    HeadSha = "fedcba9876543210fedcba9876543210fedcba98",
                };
                break;
            case "failed-source-ci":
                scenario.Jobs =
                [
                    CreateJob(12, SourceCiBuildJob, conclusion: "failure"),
                    CreateJob(14, "pack-release-candidate"),
                ];
                break;
            case "missing-bundle":
                scenario.Artifacts =
                [
                    CreateArtifact(21, CandidateName, CandidateDigest),
                ];
                break;
            case "revoked-approval":
                scenario.MainBlobResult = CreateGitResult(128, string.Empty);
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown defect '{defect}'.");
        }

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(dryRun: true);

        result.Rules[failedRuleIndex].Outcome.ShouldBe(
            RecoveryRuleOutcome.Failed);
        result.Rules[0].Outcome.ShouldBe(RecoveryRuleOutcome.Reported);
        result.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_DryRunStillRefusesOtherEvents()
    {
        RecoveryScenario scenario = new();
        scenario.Run = scenario.Run with { Event = "schedule" };

        RecoveryVerificationResult result =
            await scenario.VerifyAsync(dryRun: true);

        result.Rules[1].Detail.ShouldBe(
            "Workflow run 4242 is not an eligible original release run: event is 'schedule', not 'release' or 'workflow_dispatch'.");
        AssertSourceCiAndRetainedNotEvaluated(scenario, result);
    }

    [Fact]
    public void FormatOutcome_UsesTheRecoveryOutcomeNames()
    {
        ReleaseRecoveryVerifier.FormatOutcome(RecoveryRuleOutcome.Passed)
            .ShouldBe("passed");
        ReleaseRecoveryVerifier.FormatOutcome(RecoveryRuleOutcome.Failed)
            .ShouldBe("failed");
        ReleaseRecoveryVerifier.FormatOutcome(RecoveryRuleOutcome.Reported)
            .ShouldBe("reported");
        ReleaseRecoveryVerifier.FormatOutcome(
                RecoveryRuleOutcome.NotEvaluated)
            .ShouldBe("not-evaluated");
    }

    private static void AssertSourceCiAndRetainedNotEvaluated(
        RecoveryScenario scenario,
        RecoveryVerificationResult result)
    {
        result.Rules[2].ShouldBe(
            new RecoveryRuleResult(
                "R3",
                "source-ci",
                RecoveryRuleOutcome.NotEvaluated,
                NotEvaluatedDetail));
        result.Rules[3].ShouldBe(
            new RecoveryRuleResult(
                "R4",
                "retained",
                RecoveryRuleOutcome.NotEvaluated,
                NotEvaluatedDetail));
        result.SourceCiJobs.ShouldBeEmpty();
        result.CandidateArtifact.ShouldBeNull();
        result.BundleArtifact.ShouldBeNull();
        result.Authorized.ShouldBeFalse();
        scenario.GitHubClient.Verify(
            client => client.ListWorkflowRunJobsAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        scenario.GitHubClient.Verify(
            client => client.ListWorkflowRunArtifactsAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static RecoveryRequest CreateRequest(
        string? approvalCommit = ApprovalCommit,
        bool dryRun = false) =>
        new(
            Repository,
            Tag,
            Version,
            Commit,
            RunId,
            approvalCommit,
            dryRun);

    private static GitHubRelease CreateRelease(bool draft = false) =>
        new(9001, Tag, draft, ReleaseUrl);

    private static GitHubWorkflowRun CreateRun() =>
        new(
            RunId,
            ".github/workflows/nuget-generator.yaml",
            "release",
            Commit,
            "completed",
            "failure",
            RunAttempt: 1,
            Repository,
            Repository,
            RunUrl,
            s_createdAt);

    private static GitHubWorkflowJob[] CreateJobs() =>
        [
            CreateJob(11, "validate-release"),
            CreateJob(12, SourceCiBuildJob),
            CreateJob(13, SourceCiHardenedJob),
            CreateJob(14, "pack-release-candidate"),
            CreateJob(15, "qualify-ubuntu-latest-net8.0", conclusion: "failure"),
            CreateJob(16, "publish-nuget", conclusion: "skipped"),
        ];

    private static GitHubWorkflowJob CreateJob(
        long id,
        string name,
        int runAttempt = 1,
        string? conclusion = "success") =>
        new(
            id,
            name,
            runAttempt,
            conclusion is null ? "in_progress" : "completed",
            conclusion,
            $"{RunUrl}/job/{id}");

    private static GitHubArtifact[] CreateArtifacts() =>
        [
            CreateArtifact(21, CandidateName, CandidateDigest),
            CreateArtifact(22, BundleName, BundleDigest),
            CreateArtifact(
                23,
                "qualification-ubuntu-latest-net8.0",
                BundleDigest),
        ];

    private static GitHubArtifact CreateArtifact(
        long id,
        string name,
        string? digest) =>
        new(
            id,
            name,
            Expired: false,
            digest,
            SizeInBytes: 4096,
            s_createdAt.AddDays(90),
            RunId);

    private static ReleaseGitCommandResult CreateGitResult(
        int exitCode,
        string standardOutput) =>
        new(exitCode, standardOutput, string.Empty);

    private sealed class RecoveryScenario
    {
        internal RecoveryScenario()
        {
            GitHubClient
                .Setup(client => client.GetPublishedReleaseAsync(
                    Repository,
                    Tag,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Release);
            GitHubClient
                .Setup(client => client.GetWorkflowRunAsync(
                    Repository,
                    RunId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Run);
            GitHubClient
                .Setup(client => client.ListWorkflowRunJobsAsync(
                    Repository,
                    RunId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Jobs);
            GitHubClient
                .Setup(client => client.ListWorkflowRunArtifactsAsync(
                    Repository,
                    RunId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Artifacts);
            GitClient
                .Setup(client => client.FetchOriginMainAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FetchOriginMainResult);
            GitClient
                .Setup(client => client.CheckAncestorAsync(
                    ApprovalCommit,
                    "origin/main",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AncestorResult);
            GitObjectClient
                .Setup(client => client.ResolveBlobIdAsync(
                    ApprovalCommit,
                    ApprovalPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ApprovalBlobResult);
            GitObjectClient
                .Setup(client => client.ResolveBlobIdAsync(
                    "origin/main",
                    ApprovalPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => MainBlobResult);
            GitObjectClient
                .Setup(client => client.ResolveCommitAuthorAsync(
                    ApprovalCommit,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AuthorResult);
        }

        internal Mock<IReleaseGitClient> GitClient { get; } = new();

        internal Mock<IReleaseGitHubClient> GitHubClient { get; } = new();

        internal Mock<IReleaseGitObjectClient> GitObjectClient { get; } = new();

        internal GitHubRelease? Release { get; set; } = CreateRelease();

        internal GitHubWorkflowRun Run { get; set; } = CreateRun();

        internal IReadOnlyList<GitHubWorkflowJob> Jobs { get; set; } =
            CreateJobs();

        internal IReadOnlyList<GitHubArtifact> Artifacts { get; set; } =
            CreateArtifacts();

        internal ReleaseGitCommandResult FetchOriginMainResult { get; set; } =
            CreateGitResult(0, string.Empty);

        internal ReleaseGitCommandResult AncestorResult { get; set; } =
            CreateGitResult(0, string.Empty);

        internal ReleaseGitCommandResult ApprovalBlobResult { get; set; } =
            CreateGitResult(0, $"{ApprovalBlob}\n");

        internal ReleaseGitCommandResult MainBlobResult { get; set; } =
            CreateGitResult(0, $"{ApprovalBlob}\n");

        internal ReleaseGitCommandResult AuthorResult { get; set; } =
            CreateGitResult(0, $"{ApprovalAuthor}\n");

        internal Task<RecoveryVerificationResult> VerifyAsync(
            string? approvalCommit = ApprovalCommit,
            bool dryRun = false) =>
            VerifyRequestAsync(CreateRequest(approvalCommit, dryRun));

        internal Task<RecoveryVerificationResult> VerifyRequestAsync(
            RecoveryRequest request) =>
            new ReleaseRecoveryVerifier(
                    GitClient.Object,
                    GitObjectClient.Object,
                    GitHubClient.Object)
                .VerifyAsync(
                    request,
                    TestContext.Current.CancellationToken);
    }
}
