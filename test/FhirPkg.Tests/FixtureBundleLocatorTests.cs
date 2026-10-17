// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class FixtureBundleLocatorTests
{
    private const string ArtifactName = "fixture-bundle-v1-" + CorpusBlob;
    private const string Commit =
        "0123456789abcdef0123456789abcdef01234567";
    private const string CorpusBlob =
        "89abcdef0123456789abcdef0123456789abcdef";
    private const string CorpusPath =
        "test/FhirPkg.Qualification/qualification-corpus.json";
    private const string Digest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DriftHeadSha =
        "fedcba9876543210fedcba9876543210fedcba98";
    private const string Repository = "octo-org/dotnet-fhir-packages";
    private const string Tag = "v2099.101.1";

    private const string NoBundleMessage =
        $"No frozen fixture bundle for {CorpusPath} at {Commit} (corpus blob {CorpusBlob}). Run 'gh workflow run fixture-drift.yaml --ref {Tag}', wait for it to pass, then re-run this workflow's failed jobs.";

    private static readonly DateTimeOffset s_now =
        new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("schedule")]
    [InlineData("workflow_dispatch")]
    public async Task LocateAsync_AcceptsAQualifyingDriftRun(string eventName)
    {
        LocatorScenario scenario = new();
        scenario.AddBundle(
            CreateArtifact(101, 501),
            CreateRun(501) with { Event = eventName },
            CreateJobs());

        LocatedFixtureBundle bundle = await scenario.LocateAsync();

        bundle.ShouldBe(
            new LocatedFixtureBundle(
                101,
                ArtifactName,
                Digest,
                501,
                CreateRunUrl(501),
                CorpusBlob,
                s_now.AddDays(80)));
        scenario.GitObjectClient.Verify(
            client => client.ResolveBlobIdAsync(
                Commit,
                CorpusPath,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitHubClient.Verify(
            client => client.ListArtifactsByNameAsync(
                Repository,
                ArtifactName,
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitClient.Verify(
            client => client.FetchOriginMainAsync(
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.GitClient.Verify(
            client => client.CheckAncestorAsync(
                DriftHeadSha,
                "origin/main",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LocateAsync_AcceptsExactly24HoursOfRemainingRetention()
    {
        LocatorScenario scenario = new();
        scenario.AddBundle(
            CreateArtifact(101, 501) with { ExpiresAt = s_now.AddHours(24) },
            CreateRun(501),
            CreateJobs());

        LocatedFixtureBundle bundle = await scenario.LocateAsync();

        bundle.ExpiresAt.ShouldBe(s_now.AddHours(24));
    }

    [Theory]
    [InlineData("wrong-workflow-path")]
    [InlineData("pull-request-event")]
    [InlineData("other-base-repository")]
    [InlineData("fork-head-repository")]
    [InlineData("failed-drift-job")]
    [InlineData("missing-drift-job")]
    [InlineData("head-not-on-main")]
    [InlineData("expired-artifact")]
    [InlineData("expires-within-24-hours")]
    [InlineData("missing-digest")]
    public async Task LocateAsync_RejectsADisqualifiedBundle(string disqualifier)
    {
        GitHubArtifact artifact = CreateArtifact(101, 501);
        GitHubWorkflowRun run = CreateRun(501);
        GitHubWorkflowJob[] jobs = CreateJobs();
        int ancestorExitCode = 0;
        switch (disqualifier)
        {
            case "wrong-workflow-path":
                run = run with
                {
                    Path = ".github/workflows/nuget-generator.yaml",
                };
                break;
            case "pull-request-event":
                run = run with { Event = "pull_request" };
                break;
            case "other-base-repository":
                run = run with
                {
                    RepositoryFullName = "someone-else/dotnet-fhir-packages",
                };
                break;
            case "fork-head-repository":
                run = run with
                {
                    HeadRepositoryFullName = "fork-owner/dotnet-fhir-packages",
                };
                break;
            case "failed-drift-job":
                jobs = CreateJobs(driftConclusion: "failure");
                break;
            case "missing-drift-job":
                jobs =
                [
                    .. jobs.Where(
                        static job => !string.Equals(
                            job.Name,
                            "fixture-drift",
                            StringComparison.Ordinal)),
                ];
                break;
            case "head-not-on-main":
                ancestorExitCode = 1;
                break;
            case "expired-artifact":
                artifact = artifact with { Expired = true };
                break;
            case "expires-within-24-hours":
                artifact = artifact with { ExpiresAt = s_now.AddHours(23) };
                break;
            case "missing-digest":
                artifact = artifact with { Digest = null };
                break;
            default:
                throw new ShouldAssertException(
                    $"Unknown disqualifier '{disqualifier}'.");
        }

        LocatorScenario scenario = new();
        scenario.AddBundle(artifact, run, jobs, ancestorExitCode);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => scenario.LocateAsync());

        exception.Message.ShouldBe(NoBundleMessage);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task LocateAsync_AcceptsARunWhoseLiveJobDidNotSucceed(
        string liveConclusion)
    {
        LocatorScenario scenario = new();
        scenario.AddBundle(
            CreateArtifact(101, 501),
            CreateRun(501) with { Conclusion = liveConclusion },
            CreateJobs(liveConclusion: liveConclusion));

        LocatedFixtureBundle bundle = await scenario.LocateAsync();

        bundle.ArtifactId.ShouldBe(101);
        bundle.RunId.ShouldBe(501);
    }

    [Theory]
    [InlineData("failure", "success", true)]
    [InlineData("success", "failure", false)]
    [InlineData("success", "cancelled", false)]
    public async Task LocateAsync_JudgesOnlyTheLatestDriftJobAttempt(
        string firstAttemptConclusion,
        string secondAttemptConclusion,
        bool accepted)
    {
        LocatorScenario scenario = new();
        scenario.AddBundle(
            CreateArtifact(101, 501),
            CreateRun(501) with { RunAttempt = 2 },
            [
                CreateJob(7003, "fixture-drift", 2, secondAttemptConclusion),
                CreateJob(7001, "fixture-drift", 1, firstAttemptConclusion),
                CreateJob(7002, "live-registry-interop", 1, "success"),
            ]);

        if (accepted)
        {
            LocatedFixtureBundle bundle = await scenario.LocateAsync();
            bundle.ArtifactId.ShouldBe(101);
        }
        else
        {
            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                    () => scenario.LocateAsync());
            exception.Message.ShouldBe(NoBundleMessage);
        }
    }

    [Fact]
    public async Task LocateAsync_ChoosesTheNewestQualifyingRun()
    {
        LocatorScenario scenario = new();
        scenario.AddBundle(
            CreateArtifact(101, 501),
            CreateRun(
                501,
                "1111111111111111111111111111111111111111",
                s_now.AddDays(-9)),
            CreateJobs());
        scenario.AddBundle(
            CreateArtifact(103, 503),
            CreateRun(
                503,
                "3333333333333333333333333333333333333333",
                s_now.AddDays(-1)),
            CreateJobs());
        scenario.AddBundle(
            CreateArtifact(102, 502),
            CreateRun(
                502,
                "2222222222222222222222222222222222222222",
                s_now.AddDays(-4)),
            CreateJobs());
        scenario.AddBundle(
            CreateArtifact(104, 504),
            CreateRun(
                504,
                "4444444444444444444444444444444444444444",
                s_now.AddHours(-1)) with
            {
                Event = "pull_request",
            },
            CreateJobs());

        LocatedFixtureBundle bundle = await scenario.LocateAsync();

        bundle.ArtifactId.ShouldBe(103);
        bundle.RunId.ShouldBe(503);
        bundle.RunUrl.ShouldBe(CreateRunUrl(503));
        scenario.GitClient.Verify(
            client => client.FetchOriginMainAsync(
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LocateAsync_NamesCorpusCommitBlobAndTagWhenNoBundleExists()
    {
        LocatorScenario scenario = new();

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => scenario.LocateAsync());

        exception.Message.ShouldBe(NoBundleMessage);
        exception.Message.ShouldContain(CorpusPath);
        exception.Message.ShouldContain($" at {Commit} ");
        exception.Message.ShouldContain($"(corpus blob {CorpusBlob})");
        exception.Message.ShouldContain(
            $"'gh workflow run fixture-drift.yaml --ref {Tag}'");
    }

    [Fact]
    public async Task LocateAsync_FailsWhenTheCorpusBlobCannotBeResolved()
    {
        LocatorScenario scenario = new();
        scenario.GitObjectClient
            .Setup(client => client.ResolveBlobIdAsync(
                Commit,
                CorpusPath,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseGitCommandResult(
                128,
                string.Empty,
                "fatal: path does not exist"));

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => scenario.LocateAsync());

        exception.Message.ShouldBe(
            $"Unable to resolve the git blob id of '{CorpusPath}' at '{Commit}'.");
        scenario.GitHubClient.Verify(
            client => client.ListArtifactsByNameAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReleaseGitClient_PassesExactGitObjectArguments()
    {
        List<string> invocations = [];
        Mock<IReleaseProcessRunner> processRunner = new();
        processRunner
            .Setup(runner => runner.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback(
                (string fileName,
                 IReadOnlyList<string> arguments,
                 CancellationToken _) =>
                    invocations.Add(
                        $"{fileName}|{string.Join('|', arguments)}"))
            .ReturnsAsync(new ReleaseProcessResult(
                0,
                $"{CorpusBlob}\n",
                string.Empty));
        IReleaseGitObjectClient gitClient =
            new ReleaseGitClient(processRunner.Object);

        ReleaseGitCommandResult blobResult =
            await gitClient.ResolveBlobIdAsync(
                Commit,
                CorpusPath,
                TestContext.Current.CancellationToken);
        await gitClient.ResolveCommitAuthorAsync(
            Commit,
            TestContext.Current.CancellationToken);

        blobResult.Output.ShouldBe(CorpusBlob);
        invocations.ToArray().ShouldBe(
            [
                $"git|rev-parse|{Commit}:{CorpusPath}",
                $"git|log|-1|--format=%an <%ae>|{Commit}",
            ]);
    }

    private static GitHubArtifact CreateArtifact(
        long id,
        long runId) =>
        new(
            id,
            ArtifactName,
            Expired: false,
            Digest,
            SizeInBytes: 4096,
            s_now.AddDays(80),
            runId);

    private static GitHubWorkflowRun CreateRun(
        long id,
        string headSha = DriftHeadSha,
        DateTimeOffset? createdAt = null) =>
        new(
            id,
            ".github/workflows/fixture-drift.yaml",
            "schedule",
            headSha,
            "completed",
            "success",
            RunAttempt: 1,
            Repository,
            Repository,
            CreateRunUrl(id),
            createdAt ?? s_now.AddDays(-2));

    private static GitHubWorkflowJob[] CreateJobs(
        string driftConclusion = "success",
        string liveConclusion = "success") =>
        [
            CreateJob(7001, "fixture-drift", 1, driftConclusion),
            CreateJob(7002, "live-registry-interop", 1, liveConclusion),
        ];

    private static GitHubWorkflowJob CreateJob(
        long id,
        string name,
        int runAttempt,
        string conclusion) =>
        new(
            id,
            name,
            runAttempt,
            "completed",
            conclusion,
            $"https://github.test/{Repository}/actions/runs/501/job/{id}");

    private static string CreateRunUrl(long runId) =>
        $"https://github.test/{Repository}/actions/runs/{runId}";

    private static ReleaseGitCommandResult CreateGitResult(
        int exitCode,
        string standardOutput) =>
        new(exitCode, standardOutput, string.Empty);

    private sealed class LocatorScenario
    {
        private readonly List<GitHubArtifact> _artifacts = [];

        internal LocatorScenario()
        {
            GitObjectClient
                .Setup(client => client.ResolveBlobIdAsync(
                    Commit,
                    CorpusPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateGitResult(0, $"{CorpusBlob}\n"));
            GitClient
                .Setup(client => client.FetchOriginMainAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateGitResult(0, string.Empty));
            GitHubClient
                .Setup(client => client.ListArtifactsByNameAsync(
                    Repository,
                    ArtifactName,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _artifacts.ToArray());
        }

        internal Mock<IReleaseGitClient> GitClient { get; } = new();

        internal Mock<IReleaseGitHubClient> GitHubClient { get; } = new();

        internal Mock<IReleaseGitObjectClient> GitObjectClient { get; } = new();

        internal void AddBundle(
            GitHubArtifact artifact,
            GitHubWorkflowRun run,
            IReadOnlyList<GitHubWorkflowJob> jobs,
            int ancestorExitCode = 0)
        {
            _artifacts.Add(artifact);
            GitHubClient
                .Setup(client => client.GetWorkflowRunAsync(
                    Repository,
                    run.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(run);
            GitHubClient
                .Setup(client => client.ListWorkflowRunJobsAsync(
                    Repository,
                    run.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(jobs);
            GitClient
                .Setup(client => client.CheckAncestorAsync(
                    run.HeadSha,
                    "origin/main",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateGitResult(ancestorExitCode, string.Empty));
        }

        internal Task<LocatedFixtureBundle> LocateAsync() =>
            new FixtureBundleLocator(
                    GitClient.Object,
                    GitObjectClient.Object,
                    GitHubClient.Object)
                .LocateAsync(
                    Repository,
                    Commit,
                    Tag,
                    CorpusPath,
                    s_now,
                    TestContext.Current.CancellationToken);
    }
}
