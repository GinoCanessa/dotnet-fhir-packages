// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseInputValidatorTests
{
    private const string CliIndexUri =
        "https://example.test/cli/index.json";
    private const string HeadCommit =
        "0123456789abcdef0123456789abcdef01234567";
    private const string MainCommit =
        "89abcdef0123456789abcdef0123456789abcdef";
    private const string MismatchedTagCommit =
        "fedcba9876543210fedcba9876543210fedcba98";
    private const string SdkIndexUri =
        "https://example.test/sdk/index.json";

    [Theory]
    [InlineData(
        "2099.101",
        ReleaseValidationFixture.Tag,
        "refs/tags/v2099.101.1",
        "Version '2099.101' must contain exactly three numeric components.")]
    [InlineData(
        "2099.0101.1",
        "v2099.0101.1",
        "refs/tags/v2099.0101.1",
        "Version '2099.0101.1' is not in canonical numeric form.")]
    [InlineData(
        "65535.1.1",
        "v65535.1.1",
        "refs/tags/v65535.1.1",
        "Version '65535.1.1' cannot be represented as an assembly version.")]
    [InlineData(
        ReleaseValidationFixture.Version,
        "v2099.101.2",
        "refs/tags/v2099.101.2",
        "Tag 'v2099.101.2' must exactly match 'v2099.101.1'.")]
    [InlineData(
        ReleaseValidationFixture.Version,
        ReleaseValidationFixture.Tag,
        "refs/heads/main",
        "The workflow must run from 'refs/tags/v2099.101.1', not 'refs/heads/main'.")]
    public async Task ValidateAsync_RejectsInvalidVersionTagOrRef(
        string version,
        string tag,
        string githubRef,
        string expectedMessage)
    {
        FakeReleaseGitClient gitClient = new();
        FakeReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new();
        IReleaseInputValidator validator =
            new ReleaseInputValidator(
                gitClient,
                versionAvailabilityValidator);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => validator.ValidateAsync(
                    version,
                    tag,
                    githubRef,
                    SdkIndexUri,
                    CliIndexUri,
                    allowPublishedVersion: false,
                    CancellationToken.None));

        exception.Message.ShouldBe(expectedMessage);
        gitClient.Calls.Count.ShouldBe(0);
        versionAvailabilityValidator.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_RejectsTagCommitMismatch()
    {
        FakeReleaseGitClient gitClient = new()
        {
            TagCommitResult = new ReleaseGitCommandResult(
                0,
                $"{MismatchedTagCommit}{Environment.NewLine}",
                string.Empty),
        };
        FakeReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new();
        IReleaseInputValidator validator =
            new ReleaseInputValidator(
                gitClient,
                versionAvailabilityValidator);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => validator.ValidateAsync(
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.Tag,
                    $"refs/tags/{ReleaseValidationFixture.Tag}",
                    SdkIndexUri,
                    CliIndexUri,
                    allowPublishedVersion: false,
                    CancellationToken.None));

        exception.Message.ShouldBe(
            $"Tag '{ReleaseValidationFixture.Tag}' points to '{MismatchedTagCommit}', not checked-out commit '{HeadCommit}'.");
        versionAvailabilityValidator.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_RejectsNonMainAncestry()
    {
        FakeReleaseGitClient gitClient = new()
        {
            AncestorResult = new ReleaseGitCommandResult(
                1,
                string.Empty,
                string.Empty),
        };
        FakeReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new();
        IReleaseInputValidator validator =
            new ReleaseInputValidator(
                gitClient,
                versionAvailabilityValidator);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => validator.ValidateAsync(
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.Tag,
                    $"refs/tags/{ReleaseValidationFixture.Tag}",
                    SdkIndexUri,
                    CliIndexUri,
                    allowPublishedVersion: false,
                    CancellationToken.None));

        exception.Message.ShouldBe(
            $"Release commit '{HeadCommit}' is not an ancestor of origin/main '{MainCommit}'.");
        versionAvailabilityValidator.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsReleaseOnOriginMain()
    {
        FakeReleaseGitClient gitClient = new();
        FakeReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new();
        IReleaseInputValidator validator =
            new ReleaseInputValidator(
                gitClient,
                versionAvailabilityValidator);

        ReleaseInputValidationResult result =
            await validator.ValidateAsync(
                ReleaseValidationFixture.Version,
                ReleaseValidationFixture.Tag,
                $"refs/tags/{ReleaseValidationFixture.Tag}",
                SdkIndexUri,
                CliIndexUri,
                allowPublishedVersion: false,
                CancellationToken.None);

        result.Commit.ShouldBe(HeadCommit);
        result.MainCommit.ShouldBe(MainCommit);
        gitClient.Calls.ToArray().ShouldBe(
            [
                "ResolveHeadCommit",
                $"FetchReleaseTag:{ReleaseValidationFixture.Tag}",
                $"ResolveTagCommit:{ReleaseValidationFixture.Tag}",
                "FetchOriginMain",
                "ResolveOriginMainCommit",
                $"CheckAncestor:{HeadCommit}:origin/main",
            ]);
        versionAvailabilityValidator.CallCount.ShouldBe(1);
        versionAvailabilityValidator.Calls.ToArray().ShouldBe(
            [
                $"{ReleaseValidationFixture.Version}|{SdkIndexUri}|{CliIndexUri}",
            ]);
    }

    [Fact]
    public async Task ValidateAsync_SkipsAvailabilityCheckWhenAllowPublishedVersion()
    {
        FakeReleaseGitClient gitClient = new();
        FakeReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new()
            {
                ExceptionToThrow =
                    new InvalidOperationException(
                        "Version availability should be skipped."),
            };
        IReleaseInputValidator validator =
            new ReleaseInputValidator(
                gitClient,
                versionAvailabilityValidator);

        ReleaseInputValidationResult result =
            await validator.ValidateAsync(
                ReleaseValidationFixture.Version,
                ReleaseValidationFixture.Tag,
                "   ",
                SdkIndexUri,
                CliIndexUri,
                allowPublishedVersion: true,
                CancellationToken.None);

        result.Commit.ShouldBe(HeadCommit);
        result.MainCommit.ShouldBe(MainCommit);
        versionAvailabilityValidator.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseGitClient_PreservesExactGitArguments()
    {
        RecordingReleaseProcessRunner processRunner = new();
        IReleaseGitClient gitClient = new ReleaseGitClient(processRunner);

        await gitClient.ResolveHeadCommitAsync(CancellationToken.None);
        await gitClient.FetchReleaseTagAsync(
            ReleaseValidationFixture.Tag,
            CancellationToken.None);
        await gitClient.ResolveTagCommitAsync(
            ReleaseValidationFixture.Tag,
            CancellationToken.None);
        await gitClient.FetchOriginMainAsync(CancellationToken.None);
        await gitClient.ResolveOriginMainCommitAsync(
            CancellationToken.None);
        await gitClient.CheckAncestorAsync(
            HeadCommit,
            "origin/main",
            CancellationToken.None);

        processRunner.Invocations
            .Select(
                static invocation =>
                    $"{invocation.FileName}|{string.Join('|', invocation.Arguments)}")
            .ToArray()
            .ShouldBe(
            [
                "git|rev-parse|HEAD",
                $"git|fetch|--force|origin|refs/tags/{ReleaseValidationFixture.Tag}:refs/tags/{ReleaseValidationFixture.Tag}",
                $"git|rev-list|-n|1|{ReleaseValidationFixture.Tag}",
                "git|fetch|--no-tags|origin|+refs/heads/main:refs/remotes/origin/main",
                "git|rev-parse|origin/main",
                $"git|merge-base|--is-ancestor|{HeadCommit}|origin/main",
            ]);
    }

    [Fact]
    public async Task ReleaseProcessRunner_LaunchesExecutableDirectly()
    {
        IReleaseProcessRunner processRunner = new ReleaseProcessRunner();

        ReleaseProcessResult result = await processRunner.RunAsync(
            "dotnet",
            ["--version"],
            TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.Trim().ShouldMatch(
            "^\\d+\\.\\d+\\.\\d+");
    }

    private sealed class FakeReleaseVersionAvailabilityValidator
        : IReleaseVersionAvailabilityValidator
    {
        public List<string> Calls { get; } = [];

        public int CallCount => Calls.Count;

        public Exception? ExceptionToThrow { get; init; }

        public Task ValidateAsync(
            string version,
            string sdkIndexUri,
            string cliIndexUri,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{version}|{sdkIndexUri}|{cliIndexUri}");
            if (ExceptionToThrow is null)
            {
                return Task.CompletedTask;
            }

            return Task.FromException(ExceptionToThrow);
        }
    }

    private sealed class RecordingReleaseProcessRunner
        : IReleaseProcessRunner
    {
        internal List<ProcessInvocation> Invocations { get; } = [];

        public Task<ReleaseProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add(
                new ProcessInvocation(fileName, [.. arguments]));
            return Task.FromResult(
                new ReleaseProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record ProcessInvocation(
        string FileName,
        IReadOnlyList<string> Arguments);

    private sealed class FakeReleaseGitClient
        : IReleaseGitClient
    {
        public FakeReleaseGitClient()
        {
            HeadCommitResult = CreateCommitResult(HeadCommit);
            FetchTagResult = new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty);
            TagCommitResult = CreateCommitResult(HeadCommit);
            FetchOriginMainResult = new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty);
            OriginMainCommitResult = CreateCommitResult(MainCommit);
            AncestorResult = new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty);
        }

        public ReleaseGitCommandResult AncestorResult { get; init; }

        public List<string> Calls { get; } = [];

        public ReleaseGitCommandResult FetchOriginMainResult
        {
            get;
            init;
        }

        public ReleaseGitCommandResult FetchTagResult { get; init; }

        public ReleaseGitCommandResult HeadCommitResult { get; init; }

        public ReleaseGitCommandResult OriginMainCommitResult
        {
            get;
            init;
        }

        public ReleaseGitCommandResult TagCommitResult { get; init; }

        public Task<ReleaseGitCommandResult> CheckAncestorAsync(
            string ancestorCommit,
            string descendantRevision,
            CancellationToken cancellationToken)
        {
            Calls.Add(
                $"CheckAncestor:{ancestorCommit}:{descendantRevision}");
            return Task.FromResult(AncestorResult);
        }

        public Task<ReleaseGitCommandResult> FetchOriginMainAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("FetchOriginMain");
            return Task.FromResult(FetchOriginMainResult);
        }

        public Task<ReleaseGitCommandResult> FetchReleaseTagAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            Calls.Add($"FetchReleaseTag:{tag}");
            return Task.FromResult(FetchTagResult);
        }

        public Task<ReleaseGitCommandResult> ResolveHeadCommitAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("ResolveHeadCommit");
            return Task.FromResult(HeadCommitResult);
        }

        public Task<ReleaseGitCommandResult> ResolveOriginMainCommitAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("ResolveOriginMainCommit");
            return Task.FromResult(OriginMainCommitResult);
        }

        public Task<ReleaseGitCommandResult> ResolveTagCommitAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            Calls.Add($"ResolveTagCommit:{tag}");
            return Task.FromResult(TagCommitResult);
        }

        private static ReleaseGitCommandResult CreateCommitResult(
            string commit) =>
            new(
                0,
                $"{commit}{Environment.NewLine}",
                string.Empty);
    }
}
