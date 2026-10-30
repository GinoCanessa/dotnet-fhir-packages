// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FhirPkg.Release;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseCommandTests
{
    private static readonly SemaphoreSlim s_consoleGate =
        new(initialCount: 1, maxCount: 1);

    private const string BundleDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string CandidateDigest =
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string CliIndexUri =
        "https://packages.example/cli/index.json";
    private const string CorpusBlob =
        "89abcdef0123456789abcdef0123456789abcdef";
    private const string ExpectedDigestError =
        "--expected-digest must be 'sha256:' followed by 64 lowercase hexadecimal characters.";
    private const string GitHubRepository =
        "octo-org/dotnet-fhir-packages";
    private const string RecoveryApprovalAuthor =
        "Release Approver <approver@example.test>";
    private const string RecoveryApprovalCommit =
        "abcdef0123456789abcdef0123456789abcdef01";
    private const string RecoveryReleaseUrl =
        $"https://github.test/{GitHubRepository}/releases/tag/{ReleaseValidationFixture.Tag}";
    private const string RecoveryRunUrl =
        $"https://github.test/{GitHubRepository}/actions/runs/4242";
    private const string SdkIndexUri =
        "https://packages.example/sdk/index.json";

    [Fact]
    public async Task ValidateInputsCommand_BindsOptionsAndWritesExpectedOutputs()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            StringWriter stdout = new();
            StringWriter stderr = new();
            CapturingReleaseInputValidator inputValidator = new(
                new ReleaseInputValidationResult(
                    "head-commit",
                    "main-commit"));

            ReleaseCommandServices services = CreateServices(
                inputValidator: inputValidator,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-inputs",
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--github-ref",
                    $"refs/tags/{ReleaseValidationFixture.Tag}",
                    "--sdk-index-uri",
                    SdkIndexUri,
                    "--cli-index-uri",
                    CliIndexUri,
                    "--allow-published-version",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            stdout.ToString().ShouldBe(
                "Validated release 2099.101.1 at head-commit on origin/main main-commit." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            inputValidator.Invocations.ShouldHaveSingleItem();
            ReleaseInputInvocation invocation =
                inputValidator.Invocations[0];
            invocation.Version.ShouldBe(ReleaseValidationFixture.Version);
            invocation.Tag.ShouldBe(ReleaseValidationFixture.Tag);
            invocation.GitHubRef.ShouldBe(
                $"refs/tags/{ReleaseValidationFixture.Tag}");
            invocation.SdkIndexUri.ShouldBe(SdkIndexUri);
            invocation.CliIndexUri.ShouldBe(CliIndexUri);
            invocation.AllowPublishedVersion.ShouldBeTrue();
            ReadOutputKeys(outputPath).ShouldBe(
                ["commit", "main_commit"]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateInputsCommand_WritesExactValidationMessageToStandardError()
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        ReleaseCommandServices services = CreateServices(
            inputValidator: new ThrowingReleaseInputValidator(
                new ReleaseValidationException("exact message")),
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "validate-inputs",
                "--version",
                ReleaseValidationFixture.Version,
                "--tag",
                ReleaseValidationFixture.Tag,
            ]);

        result.ExitCode.ShouldBe(1);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldBe(
            "exact message" + Environment.NewLine);
    }

    [Fact]
    public async Task ValidateVersionCommand_BindsUriOptions()
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        CapturingReleaseVersionAvailabilityValidator validator = new();
        ReleaseCommandServices services = CreateServices(
            versionAvailabilityValidator: validator,
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "validate-version",
                "--version",
                ReleaseValidationFixture.Version,
                "--sdk-index-uri",
                SdkIndexUri,
                "--cli-index-uri",
                CliIndexUri,
            ]);

        result.ExitCode.ShouldBe(0, result.DiagnosticError);
        stdout.ToString().ShouldBe(
            "Validated fresh synchronized release version 2099.101.1." +
            Environment.NewLine);
        stderr.ToString().ShouldBeEmpty();
        validator.Invocations.ShouldHaveSingleItem();
        ReleaseVersionInvocation invocation = validator.Invocations[0];
        invocation.Version.ShouldBe(ReleaseValidationFixture.Version);
        invocation.SdkIndexUri.ShouldBe(SdkIndexUri);
        invocation.CliIndexUri.ShouldBe(CliIndexUri);
    }

    [Fact]
    public async Task ValidateCandidateCommand_BindsExpectedShaOptionsAndWritesExactKeys()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            StringWriter stdout = new();
            StringWriter stderr = new();
            CapturingReleaseCandidateValidator validator = new(
                new ReleaseCandidateValidationResult(
                    "sdk-package",
                    "sdk-symbols",
                    "sdk-manifest",
                    "sdk-sha",
                    "cli-package",
                    "cli-symbols",
                    "cli-manifest",
                    "cli-sha"));
            ReleaseCommandServices services = CreateServices(
                candidateValidator: validator,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-candidate",
                    "--candidate-directory",
                    workingDirectory,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--expected-sdk-package-sha256",
                    "expected-sdk",
                    "--expected-cli-package-sha256",
                    "expected-cli",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                "Verified synchronized release candidate 2099.101.1 (SDK sdk-sha, CLI cli-sha)." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            validator.Invocations.ShouldHaveSingleItem();
            ReleaseCandidateInvocation invocation =
                validator.Invocations[0];
            invocation.CandidateDirectory.ShouldBe(workingDirectory);
            invocation.ExpectedSdkPackageSha256.ShouldBe("expected-sdk");
            invocation.ExpectedCliPackageSha256.ShouldBe("expected-cli");
            ReadOutputKeys(outputPath).ShouldBe(
                [
                    "sdk_package_path",
                    "sdk_symbols_path",
                    "sdk_manifest_path",
                    "sdk_sha256",
                    "cli_package_path",
                    "cli_symbols_path",
                    "cli_manifest_path",
                    "cli_sha256",
                ]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectPublicationCommand_BindsRetryOptionsSwitchAndWritesLowerCaseStates()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            StringWriter stdout = new();
            StringWriter stderr = new();
            CapturingReleasePublicationStateValidator validator = new(
                new ReleasePublicationStateResult(
                    ReleasePackagePublicationState.Verified,
                    ReleasePackagePublicationState.Missing));
            ReleaseCommandServices services = CreateServices(
                publicationStateValidator: validator,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "inspect-publication",
                    "--candidate-directory",
                    workingDirectory,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--sdk-flat-container-uri",
                    "https://packages.example/sdk",
                    "--cli-flat-container-uri",
                    "https://packages.example/cli",
                    "--attempts",
                    "7",
                    "--delay-seconds",
                    "9",
                    "--skip-signature-verification",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                "Publication state: CLI verified; SDK missing." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            validator.Invocations.ShouldHaveSingleItem();
            ReleasePublicationStateInvocation invocation =
                validator.Invocations[0];
            invocation.Attempts.ShouldBe(7);
            invocation.DelaySeconds.ShouldBe(9);
            invocation.SkipSignatureVerification.ShouldBeTrue();
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "cli_state=verified",
                    "sdk_state=missing",
                ]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidatePublishedPackageCommand_BindsOptionsAndWritesExpectedOutputKey()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            StringWriter stdout = new();
            StringWriter stderr = new();
            CapturingPublishedPackageValidator validator = new(
                new PublishedPackageValidationResult("published-sha"));
            ReleaseCommandServices services = CreateServices(
                publishedPackageValidator: validator,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-published-package",
                    "--package-id",
                    ReleasePackageValidationCommon.SdkPackageId,
                    "--candidate-package-path",
                    candidatePackagePath,
                    "--published-package-uri",
                    "https://packages.example/fhir-pkg-lib/package.nupkg",
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--attempts",
                    "4",
                    "--delay-seconds",
                    "3",
                    "--skip-signature-verification",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                "Verified published fhir-pkg-lib 2099.101.1 (published-sha)." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            validator.Invocations.ShouldHaveSingleItem();
            PublishedPackageInvocation invocation =
                validator.Invocations[0];
            invocation.PackageId.ShouldBe(
                ReleasePackageValidationCommon.SdkPackageId);
            invocation.CandidatePackagePath.ShouldBe(candidatePackagePath);
            invocation.PublishedPackageUri.ShouldBe(
                "https://packages.example/fhir-pkg-lib/package.nupkg");
            invocation.Attempts.ShouldBe(4);
            invocation.DelaySeconds.ShouldBe(3);
            invocation.SkipSignatureVerification.ShouldBeTrue();
            ReadOutputKeys(outputPath).ShouldBe(["published_sha256"]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GitHubOutputWriter_RejectsLineBreaksInNamesAndValues()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            GitHubOutputWriter writer = new();

            ArgumentException nameException =
                await Should.ThrowAsync<ArgumentException>(
                    () => writer.WriteAsync(
                        outputPath,
                        [
                            KeyValuePair.Create(
                                "bad\nname",
                                "value"),
                        ],
                        TestContext.Current.CancellationToken));
            nameException.Message.ShouldContain(
                "GitHub output names and values must not contain CR or LF characters.");

            ArgumentException valueException =
                await Should.ThrowAsync<ArgumentException>(
                    () => writer.WriteAsync(
                        outputPath,
                        [
                            KeyValuePair.Create(
                                "name",
                                "bad\rvalue"),
                        ],
                        TestContext.Current.CancellationToken));
            valueException.Message.ShouldContain(
                "GitHub output names and values must not contain CR or LF characters.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateInputsCommand_IntegrationInvocationSucceedsWithoutShell()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                inputValidator: new ReleaseInputValidator(
                    new FakeReleaseGitClient(),
                    new CapturingReleaseVersionAvailabilityValidator()),
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-inputs",
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--github-ref",
                    $"refs/tags/{ReleaseValidationFixture.Tag}",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                $"Validated release {ReleaseValidationFixture.Version} at {FakeReleaseGitClient.HeadCommit} on origin/main {FakeReleaseGitClient.MainCommit}." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            ReadOutputKeys(outputPath).ShouldBe(
                ["commit", "main_commit"]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateVersionCommand_IntegrationInvocationSucceedsWithFakeHttp()
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        SequenceHttpMessageHandler handler = new(
            _ => Task.FromResult(CreateJsonResponse("""
                {"versions":["2099.101.0","2099.100.9","not-canonical"]}
                """)),
            _ => Task.FromResult(CreateJsonResponse("""
                {"versions":["2099.101.0","2099.100.8"]}
                """)));
        HttpClient httpClient = new(handler);
        ReleaseCommandServices services = CreateServices(
            versionAvailabilityValidator:
                new ReleaseVersionAvailabilityValidator(httpClient),
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "validate-version",
                "--version",
                ReleaseValidationFixture.Version,
                "--sdk-index-uri",
                SdkIndexUri,
                "--cli-index-uri",
                CliIndexUri,
            ]);

        result.ExitCode.ShouldBe(0);
        stdout.ToString().ShouldBe(
            "Validated fresh synchronized release version 2099.101.1." +
            Environment.NewLine);
        stderr.ToString().ShouldBeEmpty();
        handler.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task ValidateCandidateCommand_IntegrationInvocationSucceedsWithLocalFixture()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        string outputPath = Path.Combine(
            candidateDirectory,
            "github-output.txt");
        try
        {
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                candidateValidator: new ReleaseCandidateValidator(),
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-candidate",
                    "--candidate-directory",
                    candidateDirectory,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldContain(
                "Verified synchronized release candidate 2099.101.1");
            stderr.ToString().ShouldBeEmpty();
            ReadOutputKeys(outputPath).ShouldBe(
                [
                    "sdk_package_path",
                    "sdk_symbols_path",
                    "sdk_manifest_path",
                    "sdk_sha256",
                    "cli_package_path",
                    "cli_symbols_path",
                    "cli_manifest_path",
                    "cli_sha256",
                ]);
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectPublicationCommand_IntegrationInvocationSucceedsWithFakeHttp()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        string outputPath = Path.Combine(
            candidateDirectory,
            "github-output.txt");
        try
        {
            StringWriter stdout = new();
            StringWriter stderr = new();
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)));
            HttpClient httpClient = new(handler);
            ReleaseCommandServices services = CreateServices(
                publicationStateValidator:
                    new ReleasePublicationStateValidator(
                        httpClient,
                        new CapturingPublishedPackageValidator(
                            new PublishedPackageValidationResult(
                                "published-sha")),
                        new NoOpReleaseDelay()),
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "inspect-publication",
                    "--candidate-directory",
                    candidateDirectory,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--sdk-flat-container-uri",
                    "https://packages.example/sdk",
                    "--cli-flat-container-uri",
                    "https://packages.example/cli",
                    "--attempts",
                    "1",
                    "--delay-seconds",
                    "0",
                    "--skip-signature-verification",
                    "--github-output",
                    outputPath,
                ]);

            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                "Publication state: CLI missing; SDK verified." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "cli_state=missing",
                    "sdk_state=verified",
                ]);
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidatePublishedPackageCommand_IntegrationInvocationSucceedsWithFakeHttp()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            byte[] publishedBytes = CreatePackageVariant(
                candidatePackagePath,
                additions:
                [
                    (
                        ".SIGNATURE.P7S",
                        [0x01]),
                ]);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    publishedBytes)));
            HttpClient httpClient = new(handler);
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                publishedPackageValidator:
                    new PublishedPackageValidator(
                        httpClient,
                        new ThrowingProcessRunner(),
                        new ReleasePackageValidator(),
                        new NoOpReleaseDelay()),
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "validate-published-package",
                    "--package-id",
                    ReleasePackageValidationCommon.SdkPackageId,
                    "--candidate-package-path",
                    candidatePackagePath,
                    "--published-package-uri",
                    "https://packages.example/fhir-pkg-lib/package.nupkg",
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--repository-commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--attempts",
                    "1",
                    "--delay-seconds",
                    "0",
                    "--skip-signature-verification",
                    "--github-output",
                    outputPath,
                ]);

            string expectedSha256 =
                Convert.ToHexString(SHA256.HashData(publishedBytes))
                    .ToLowerInvariant();
            result.ExitCode.ShouldBe(0);
            stdout.ToString().ShouldBe(
                $"Verified published fhir-pkg-lib {ReleaseValidationFixture.Version} ({expectedSha256})." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            ReadOutputLines(outputPath).ShouldBe(
                [$"published_sha256={expectedSha256}"]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LocateFixtureBundleCommand_BindsOptionsAndWritesExpectedOutputs()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            StringWriter stdout = new();
            StringWriter stderr = new();
            CapturingFixtureBundleLocator locator = new(
                new LocatedFixtureBundle(
                    101,
                    $"fixture-bundle-v1-{CorpusBlob}",
                    BundleDigest,
                    501,
                    $"https://github.test/{GitHubRepository}/actions/runs/501",
                    CorpusBlob,
                    new DateTimeOffset(2027, 1, 14, 6, 23, 45, TimeSpan.Zero)));
            ReleaseCommandServices services = CreateServices(
                fixtureBundleLocator: locator,
                standardOutput: stdout,
                standardError: stderr);
            DateTimeOffset before = DateTimeOffset.UtcNow;

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "locate-fixture-bundle",
                    "--repository",
                    GitHubRepository,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--corpus-path",
                    "custom/qualification-corpus.json",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            DateTimeOffset after = DateTimeOffset.UtcNow;
            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            stdout.ToString().ShouldBe(
                $"Located frozen fixture bundle fixture-bundle-v1-{CorpusBlob} (artifact 101, {BundleDigest}) from run 501." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            FixtureBundleLocatorInvocation invocation =
                locator.Invocations.ShouldHaveSingleItem();
            invocation.Repository.ShouldBe(GitHubRepository);
            invocation.Commit.ShouldBe(
                ReleaseValidationFixture.RepositoryCommit);
            invocation.Tag.ShouldBe(ReleaseValidationFixture.Tag);
            invocation.CorpusPath.ShouldBe(
                "custom/qualification-corpus.json");
            invocation.Now.ShouldBeInRange(before, after);
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "bundle_artifact_id=101",
                    $"bundle_artifact_name=fixture-bundle-v1-{CorpusBlob}",
                    $"bundle_artifact_digest={BundleDigest}",
                    "bundle_run_id=501",
                    $"corpus_blob={CorpusBlob}",
                ]);
            File.ReadAllText(summaryPath).ShouldBe(
                $"Frozen fixture bundle `fixture-bundle-v1-{CorpusBlob}` \u00b7 digest `{BundleDigest}` \u00b7 produced by [fixture-drift run 501](https://github.test/{GitHubRepository}/actions/runs/501) \u00b7 expires 2027-01-14T06:23:45Z\n");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LocateFixtureBundleCommand_DefaultsToTheQualificationCorpus()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            CapturingFixtureBundleLocator locator = new(
                new LocatedFixtureBundle(
                    101,
                    $"fixture-bundle-v1-{CorpusBlob}",
                    BundleDigest,
                    501,
                    $"https://github.test/{GitHubRepository}/actions/runs/501",
                    CorpusBlob,
                    new DateTimeOffset(2027, 1, 14, 6, 23, 45, TimeSpan.Zero)));
            ReleaseCommandServices services = CreateServices(
                fixtureBundleLocator: locator);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "locate-fixture-bundle",
                    "--repository",
                    GitHubRepository,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--github-output",
                    Path.Combine(workingDirectory, "github-output.txt"),
                    "--step-summary",
                    Path.Combine(workingDirectory, "step-summary.md"),
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            locator.Invocations.ShouldHaveSingleItem().CorpusPath.ShouldBe(
                "test/FhirPkg.Qualification/qualification-corpus.json");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LocateFixtureBundleCommand_FailureWritesAnnotationSummaryAndExitsOne()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            string message =
                $"No frozen fixture bundle for test/FhirPkg.Qualification/qualification-corpus.json at {ReleaseValidationFixture.RepositoryCommit} (corpus blob {CorpusBlob}). Run 'gh workflow run fixture-drift.yaml --ref {ReleaseValidationFixture.Tag}', wait for it to pass, then re-run this workflow's failed jobs.";
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                fixtureBundleLocator: new ThrowingFixtureBundleLocator(
                    new ReleaseValidationException(message)),
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "locate-fixture-bundle",
                    "--repository",
                    GitHubRepository,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stdout.ToString().ShouldBe(
                $"::error title=Frozen fixture bundle unavailable::{message}" +
                Environment.NewLine);
            stderr.ToString().ShouldBe(message + Environment.NewLine);
            File.ReadAllText(summaryPath).ShouldBe(
                $"### Frozen fixture bundle unavailable\n\n{message}\n");
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchArtifactCommand_VerifiesDigestExtractsAndWritesSha256(
        bool destinationExists)
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string destination = Path.Combine(
                workingDirectory,
                "fixture-bundle");
            if (destinationExists)
            {
                Directory.CreateDirectory(destination);
            }

            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            byte[] archive = CreateZipArchive(
                ("bundle-manifest.json", "{\"schemaVersion\":1}"),
                ("blobs/0123.tgz", "blob bytes"));
            string digest = ComputeDigest(archive);
            string expectedSha256 = digest["sha256:".Length..];
            List<string> downloadPaths = [];
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                gitHubClient: CreateDownloadingGitHubClient(
                    archive,
                    digest,
                    downloadPaths).Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "fetch-artifact",
                    "--repository",
                    GitHubRepository,
                    "--artifact-id",
                    "77",
                    "--expected-digest",
                    digest,
                    "--destination",
                    destination,
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            stdout.ToString().ShouldBe(
                $"Fetched artifact 77 ({digest}) into {destination}." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            File.ReadAllText(
                    Path.Combine(destination, "bundle-manifest.json"))
                .ShouldBe("{\"schemaVersion\":1}");
            File.ReadAllText(
                    Path.Combine(destination, "blobs", "0123.tgz"))
                .ShouldBe("blob bytes");
            ReadOutputLines(outputPath).ShouldBe(
                [$"artifact_sha256={expectedSha256}"]);
            File.Exists(summaryPath).ShouldBeFalse();
            File.Exists(downloadPaths.ShouldHaveSingleItem())
                .ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FetchArtifactCommand_DigestMismatchWritesAnnotationSummaryAndExitsOne()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string destination = Path.Combine(
                workingDirectory,
                "fixture-bundle");
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            byte[] archive = CreateZipArchive(
                ("bundle-manifest.json", "{}"));
            string downloadedDigest = ComputeDigest(archive);
            string expectedDigest = ComputeDigest([0x01]);
            string message =
                $"Artifact 77 digest mismatch: expected {expectedDigest}, downloaded {downloadedDigest}.";
            List<string> downloadPaths = [];
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                gitHubClient: CreateDownloadingGitHubClient(
                    archive,
                    downloadedDigest,
                    downloadPaths).Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "fetch-artifact",
                    "--repository",
                    GitHubRepository,
                    "--artifact-id",
                    "77",
                    "--expected-digest",
                    expectedDigest,
                    "--destination",
                    destination,
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stdout.ToString().ShouldBe(
                $"::error title=Artifact fetch failed::{message}" +
                Environment.NewLine);
            stderr.ToString().ShouldBe(message + Environment.NewLine);
            File.ReadAllText(summaryPath).ShouldBe(
                $"### Artifact fetch failed\n\n{message}\n");
            Directory.Exists(destination).ShouldBeFalse();
            File.Exists(downloadPaths.ShouldHaveSingleItem())
                .ShouldBeFalse();
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FetchArtifactCommand_RefusesANonEmptyDestination()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string destination = Path.Combine(
                workingDirectory,
                "fixture-bundle");
            Directory.CreateDirectory(destination);
            File.WriteAllText(
                Path.Combine(destination, "stale.txt"),
                "stale");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            string message =
                $"Destination '{destination}' must not exist or must be an empty directory.";
            Mock<IReleaseGitHubClient> gitHubClient =
                new(MockBehavior.Strict);
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                gitHubClient: gitHubClient.Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "fetch-artifact",
                    "--repository",
                    GitHubRepository,
                    "--artifact-id",
                    "77",
                    "--expected-digest",
                    BundleDigest,
                    "--destination",
                    destination,
                    "--github-output",
                    Path.Combine(workingDirectory, "github-output.txt"),
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stderr.ToString().ShouldBe(message + Environment.NewLine);
            stdout.ToString().ShouldBe(
                $"::error title=Artifact fetch failed::{message}" +
                Environment.NewLine);
            string summary = File.ReadAllText(summaryPath);
            summary.ShouldStartWith("### Artifact fetch failed\n\n");
            summary.ShouldContain(
                "must not exist or must be an empty directory.");
            File.ReadAllText(Path.Combine(destination, "stale.txt"))
                .ShouldBe("stale");
            gitHubClient.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FetchArtifactCommand_RejectsArchiveEntriesOutsideTheDestination()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string destination = Path.Combine(
                workingDirectory,
                "fixture-bundle");
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            byte[] archive = CreateZipArchive(
                ("bundle-manifest.json", "{}"),
                ("../escaped.txt", "escaped"));
            string digest = ComputeDigest(archive);
            List<string> downloadPaths = [];
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                gitHubClient: CreateDownloadingGitHubClient(
                    archive,
                    digest,
                    downloadPaths).Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "fetch-artifact",
                    "--repository",
                    GitHubRepository,
                    "--artifact-id",
                    "77",
                    "--expected-digest",
                    digest,
                    "--destination",
                    destination,
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stderr.ToString().ShouldStartWith(
                $"Artifact 77 could not be extracted to '{destination}': ");
            stdout.ToString().ShouldStartWith(
                "::error title=Artifact fetch failed::Artifact 77 could not be extracted to ");
            File.ReadAllText(summaryPath).ShouldStartWith(
                "### Artifact fetch failed\n\n");
            File.Exists(Path.Combine(workingDirectory, "escaped.txt"))
                .ShouldBeFalse();
            Directory.Exists(destination).ShouldBeFalse();
            File.Exists(downloadPaths.ShouldHaveSingleItem())
                .ShouldBeFalse();
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("0", BundleDigest, "--artifact-id must be at least one.")]
    [InlineData(
        "77",
        "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef",
        ExpectedDigestError)]
    [InlineData(
        "77",
        "sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ExpectedDigestError)]
    [InlineData("77", "sha256:0123", ExpectedDigestError)]
    [InlineData(
        "77",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ExpectedDigestError)]
    public void FetchArtifactCommand_RejectsAnInvalidArtifactIdOrDigest(
        string artifactId,
        string expectedDigest,
        string expectedError)
    {
        ParseResult parseResult =
            Program.BuildRootCommand(CreateServices()).Parse(
                [
                    "fetch-artifact",
                    "--repository",
                    GitHubRepository,
                    "--artifact-id",
                    artifactId,
                    "--expected-digest",
                    expectedDigest,
                    "--destination",
                    "unused-destination",
                ]);

        parseResult.Errors
            .Select(static error => error.Message)
            .ToArray()
            .ShouldBe([expectedError]);
    }

    [Fact]
    public void GitHubAnnotations_EscapesTitleAndMessage()
    {
        GitHubAnnotations.Error(
                "Fetch: one, two",
                "50% done\r\nnext: line, more")
            .ShouldBe(
                "::error title=Fetch%3A one%2C two::50%25 done%0D%0Anext: line, more");
    }

    [Fact]
    public async Task GitHubStepSummaryWriter_AppendsWithoutTruncating()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            File.WriteAllText(summaryPath, "existing\n");
            GitHubStepSummaryWriter writer = new();

            await writer.AppendAsync(
                summaryPath,
                "first line",
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                summaryPath,
                "### Heading\n\nbody\n",
                TestContext.Current.CancellationToken);

            File.ReadAllText(summaryPath).ShouldBe(
                "existing\nfirst line\n### Heading\n\nbody\n");
            File.ReadAllBytes(summaryPath)[0].ShouldBe((byte)'e');
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyRecoveryCommand_BindsOptionsAndWritesOutputsAndSummary()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            List<RecoveryRequest> requests = [];
            Mock<IReleaseRecoveryVerifier> verifier = CreateRecoveryVerifier(
                CreateAuthorizedRecoveryResult(RecoveryApprovalAuthor),
                requests);
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                recoveryVerifier: verifier.Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "4242",
                    "--approval-commit",
                    RecoveryApprovalCommit,
                    "--dry-run",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            requests.ShouldHaveSingleItem().ShouldBe(
                new RecoveryRequest(
                    GitHubRepository,
                    ReleaseValidationFixture.Tag,
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit,
                    4242,
                    RecoveryApprovalCommit,
                    DryRun: true));
            stdout.ToString().ShouldBe(
                "Authorized recovery of v2099.101.1 from run 4242 (dry run); GitHub Release published: true." +
                Environment.NewLine);
            stderr.ToString().ShouldBeEmpty();
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "release_published=true",
                    "candidate_artifact_id=21",
                    $"candidate_artifact_digest={CandidateDigest}",
                    "original_bundle_artifact_id=22",
                    $"original_bundle_artifact_digest={BundleDigest}",
                    $"approval_commit={RecoveryApprovalCommit}",
                    $"approval_author={RecoveryApprovalAuthor}",
                ]);
            File.ReadAllText(summaryPath).ShouldBe(
                "### Recovery authorization\n" +
                "\n" +
                "| Rule | Result | Detail |\n" +
                "|-|-|-|\n" +
                $"| R1 `release-published` | reported | GitHub Release v2099.101.1 is published: {RecoveryReleaseUrl} Dry run: reported, not enforced. |\n" +
                $"| R2 `original-run` | passed | Workflow run 4242 is a completed 'workflow_dispatch' run (accepted in a dry run) of .github/workflows/nuget-generator.yaml at {ReleaseValidationFixture.RepositoryCommit}: {RecoveryRunUrl} |\n" +
                "| R3 `source-ci` | passed | All 2 'exact-source-ci / *' jobs and 'pack-release-candidate' concluded 'success' in their latest attempts. |\n" +
                $"| R4 `retained` | passed | Candidate 'fhir-pkg-2099.101.1-candidate' (artifact 21, {CandidateDigest}) and qualification inputs 'fhir-pkg-2099.101.1-fixture-bundle' (artifact 22, {BundleDigest}) are retained. |\n" +
                $"| R5 `approval` | passed | docs/releases/approvals/v2099.101.1.json at approval commit {RecoveryApprovalCommit} (blob 1111111111111111111111111111111111111111, authored by Release Approver &lt;approver@example.test&gt;) is unchanged on origin/main. |\n" +
                "\n" +
                "Original source-CI jobs (linked, not re-run):\n" +
                "\n" +
                $"- <{RecoveryRunUrl}/job/12>\n" +
                $"- <{RecoveryRunUrl}/job/13>\n");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyRecoveryCommand_TreatsAnEmptyApprovalCommitAsAbsent()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            List<RecoveryRequest> requests = [];
            Mock<IReleaseRecoveryVerifier> verifier = CreateRecoveryVerifier(
                CreateAuthorizedRecoveryResult(approvalAuthor: null),
                requests);
            StringWriter stdout = new();
            ReleaseCommandServices services = CreateServices(
                recoveryVerifier: verifier.Object,
                standardOutput: stdout);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "4242",
                    "--approval-commit",
                    "",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    Path.Combine(workingDirectory, "step-summary.md"),
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            RecoveryRequest request = requests.ShouldHaveSingleItem();
            request.ApprovalCommit.ShouldBe(string.Empty);
            request.DryRun.ShouldBeFalse();
            stdout.ToString().ShouldBe(
                "Authorized recovery of v2099.101.1 from run 4242; GitHub Release published: true." +
                Environment.NewLine);
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "release_published=true",
                    "candidate_artifact_id=21",
                    $"candidate_artifact_digest={CandidateDigest}",
                    "original_bundle_artifact_id=22",
                    $"original_bundle_artifact_digest={BundleDigest}",
                    "approval_commit=",
                    "approval_author=",
                ]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyRecoveryCommand_FailedRulesWriteAnnotationsSummaryAndExitOne()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            const string releaseDetail =
                "GitHub Release v2099.101.1 is not published (the Release is a draft).";
            const string runDetail =
                "Workflow run 4242 is not an eligible original release run: workflow is '.github/workflows/a|b.yaml', not '.github/workflows/nuget-generator.yaml'.";
            const string notEvaluatedDetail =
                "Not evaluated because R2 original-run did not pass.";
            RecoveryVerificationResult refused = new(
                [
                    new RecoveryRuleResult(
                        "R1",
                        "release-published",
                        RecoveryRuleOutcome.Failed,
                        releaseDetail),
                    new RecoveryRuleResult(
                        "R2",
                        "original-run",
                        RecoveryRuleOutcome.Failed,
                        runDetail),
                    new RecoveryRuleResult(
                        "R3",
                        "source-ci",
                        RecoveryRuleOutcome.NotEvaluated,
                        notEvaluatedDetail),
                    new RecoveryRuleResult(
                        "R4",
                        "retained",
                        RecoveryRuleOutcome.NotEvaluated,
                        notEvaluatedDetail),
                    new RecoveryRuleResult(
                        "R5",
                        "approval",
                        RecoveryRuleOutcome.Reported,
                        "No approval commit was named; the original qualification inputs are used unchanged."),
                ],
                ReleasePublished: false,
                CandidateArtifact: null,
                BundleArtifact: null,
                SourceCiJobs: [],
                ApprovalAuthor: null);
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                recoveryVerifier: CreateRecoveryVerifier(refused, []).Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "4242",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stdout.ToString().ShouldBe(
                $"::error title=Recovery refused (R1 release-published)::{releaseDetail}" +
                Environment.NewLine +
                $"::error title=Recovery refused (R2 original-run)::{runDetail}" +
                Environment.NewLine);
            stderr.ToString().ShouldBe(
                $"Recovery refused (R1 release-published): {releaseDetail}" +
                Environment.NewLine +
                $"Recovery refused (R2 original-run): {runDetail}" +
                Environment.NewLine);
            File.ReadAllText(summaryPath).ShouldBe(
                "### Recovery authorization\n" +
                "\n" +
                "| Rule | Result | Detail |\n" +
                "|-|-|-|\n" +
                $"| R1 `release-published` | failed | {releaseDetail} |\n" +
                "| R2 `original-run` | failed | Workflow run 4242 is not an eligible original release run: workflow is '.github/workflows/a\\|b.yaml', not '.github/workflows/nuget-generator.yaml'. |\n" +
                $"| R3 `source-ci` | not-evaluated | {notEvaluatedDetail} |\n" +
                $"| R4 `retained` | not-evaluated | {notEvaluatedDetail} |\n" +
                "| R5 `approval` | reported | No approval commit was named; the original qualification inputs are used unchanged. |\n" +
                "\n" +
                "Original source-CI jobs (linked, not re-run): none.\n");
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyRecoveryCommand_RefusedRequestWritesAnnotationSummaryAndExitsOne()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            const string message =
                "Tag 'v2099.101.2' must exactly match 'v2099.101.1'.";
            Mock<IReleaseRecoveryVerifier> verifier = new(MockBehavior.Strict);
            verifier
                .Setup(recoveryVerifier => recoveryVerifier.VerifyAsync(
                    It.IsAny<RecoveryRequest>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ReleaseValidationException(message));
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                recoveryVerifier: verifier.Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    "v2099.101.2",
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "4242",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(1);
            stdout.ToString().ShouldBe(
                $"::error title=Recovery refused::{message}" +
                Environment.NewLine);
            stderr.ToString().ShouldBe(message + Environment.NewLine);
            File.ReadAllText(summaryPath).ShouldBe(
                $"### Recovery authorization\n\n{message}\n");
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void VerifyRecoveryCommand_RejectsARecoverRunIdBelowOne()
    {
        ParseResult parseResult =
            Program.BuildRootCommand(CreateServices()).Parse(
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "0",
                ]);

        parseResult.Errors
            .Select(static error => error.Message)
            .ToArray()
            .ShouldBe(["--recover-run-id must be at least one."]);
    }

    [Fact]
    public async Task VerifyRecoveryCommand_IntegrationInvocationAuthorizesFromGitHubRecords()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string outputPath = Path.Combine(
                workingDirectory,
                "github-output.txt");
            string summaryPath = Path.Combine(
                workingDirectory,
                "step-summary.md");
            Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
            gitHubClient
                .Setup(client => client.GetPublishedReleaseAsync(
                    GitHubRepository,
                    ReleaseValidationFixture.Tag,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubRelease(
                    9001,
                    ReleaseValidationFixture.Tag,
                    Draft: false,
                    RecoveryReleaseUrl));
            gitHubClient
                .Setup(client => client.GetWorkflowRunAsync(
                    GitHubRepository,
                    4242,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubWorkflowRun(
                    4242,
                    ".github/workflows/nuget-generator.yaml",
                    "release",
                    ReleaseValidationFixture.RepositoryCommit,
                    "completed",
                    "failure",
                    RunAttempt: 1,
                    GitHubRepository,
                    GitHubRepository,
                    RecoveryRunUrl,
                    new DateTimeOffset(2026, 10, 16, 14, 17, 0, TimeSpan.Zero)));
            GitHubWorkflowJob[] jobs =
            [
                new(
                    12,
                    "exact-source-ci / build-and-test-ubuntu-latest",
                    1,
                    "completed",
                    "success",
                    $"{RecoveryRunUrl}/job/12"),
                new(
                    14,
                    "pack-release-candidate",
                    1,
                    "completed",
                    "success",
                    $"{RecoveryRunUrl}/job/14"),
                new(
                    15,
                    "qualify-ubuntu-latest-net8.0",
                    1,
                    "completed",
                    "failure",
                    $"{RecoveryRunUrl}/job/15"),
            ];
            gitHubClient
                .Setup(client => client.ListWorkflowRunJobsAsync(
                    GitHubRepository,
                    4242,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(jobs);
            GitHubArtifact[] artifacts =
            [
                CreateRecoveryArtifact(
                    21,
                    "fhir-pkg-2099.101.1-candidate",
                    CandidateDigest),
                CreateRecoveryArtifact(
                    22,
                    "fhir-pkg-2099.101.1-fixture-bundle",
                    BundleDigest),
            ];
            gitHubClient
                .Setup(client => client.ListWorkflowRunArtifactsAsync(
                    GitHubRepository,
                    4242,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(artifacts);
            Mock<IReleaseGitClient> gitClient = new(MockBehavior.Strict);
            Mock<IReleaseGitObjectClient> gitObjectClient =
                new(MockBehavior.Strict);
            StringWriter stdout = new();
            StringWriter stderr = new();
            ReleaseCommandServices services = CreateServices(
                recoveryVerifier: new ReleaseRecoveryVerifier(
                    gitClient.Object,
                    gitObjectClient.Object,
                    gitHubClient.Object),
                gitHubClient: gitHubClient.Object,
                standardOutput: stdout,
                standardError: stderr);

            CommandInvocationResult result = await InvokeAsync(
                services,
                [
                    "verify-recovery",
                    "--repository",
                    GitHubRepository,
                    "--version",
                    ReleaseValidationFixture.Version,
                    "--tag",
                    ReleaseValidationFixture.Tag,
                    "--commit",
                    ReleaseValidationFixture.RepositoryCommit,
                    "--recover-run-id",
                    "4242",
                    "--approval-commit",
                    "",
                    "--github-output",
                    outputPath,
                    "--step-summary",
                    summaryPath,
                ]);

            result.ExitCode.ShouldBe(0, result.DiagnosticError);
            stderr.ToString().ShouldBeEmpty();
            ReadOutputLines(outputPath).ShouldBe(
                [
                    "release_published=true",
                    "candidate_artifact_id=21",
                    $"candidate_artifact_digest={CandidateDigest}",
                    "original_bundle_artifact_id=22",
                    $"original_bundle_artifact_digest={BundleDigest}",
                    "approval_commit=",
                    "approval_author=",
                ]);
            string summary = File.ReadAllText(summaryPath);
            summary.ShouldContain(
                $"| R1 `release-published` | passed | GitHub Release v2099.101.1 is published: {RecoveryReleaseUrl} |\n");
            summary.ShouldContain("| R5 `approval` | reported | ");
            summary.ShouldEndWith(
                $"Original source-CI jobs (linked, not re-run):\n\n- <{RecoveryRunUrl}/job/12>\n");
            gitClient.VerifyNoOtherCalls();
            gitObjectClient.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyReleasePublishedCommand_BindsOptionsAndReportsAPublishedRelease()
    {
        Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
        gitHubClient
            .Setup(client => client.GetPublishedReleaseAsync(
                GitHubRepository,
                ReleaseValidationFixture.Tag,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRelease(
                9001,
                ReleaseValidationFixture.Tag,
                Draft: false,
                RecoveryReleaseUrl));
        StringWriter stdout = new();
        StringWriter stderr = new();
        ReleaseCommandServices services = CreateServices(
            gitHubClient: gitHubClient.Object,
            gitHubOutputWriter:
                new Mock<IGitHubOutputWriter>(MockBehavior.Strict).Object,
            stepSummaryWriter:
                new Mock<IGitHubStepSummaryWriter>(MockBehavior.Strict).Object,
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "verify-release-published",
                "--repository",
                GitHubRepository,
                "--tag",
                ReleaseValidationFixture.Tag,
            ]);

        result.ExitCode.ShouldBe(0, result.DiagnosticError);
        stdout.ToString().ShouldBe(
            $"GitHub Release v2099.101.1 is published: {RecoveryReleaseUrl}" +
            Environment.NewLine);
        stderr.ToString().ShouldBeEmpty();
        gitHubClient.Verify(
            client => client.GetPublishedReleaseAsync(
                GitHubRepository,
                ReleaseValidationFixture.Tag,
                It.IsAny<CancellationToken>()),
            Times.Once);
        gitHubClient.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyReleasePublishedCommand_RefusesAMissingOrDraftRelease(
        bool draftExists)
    {
        const string message = "GitHub Release v2099.101.1 is not published.";
        Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
        gitHubClient
            .Setup(client => client.GetPublishedReleaseAsync(
                GitHubRepository,
                ReleaseValidationFixture.Tag,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                draftExists
                    ? new GitHubRelease(
                        9001,
                        ReleaseValidationFixture.Tag,
                        Draft: true,
                        RecoveryReleaseUrl)
                    : null);
        StringWriter stdout = new();
        StringWriter stderr = new();
        ReleaseCommandServices services = CreateServices(
            gitHubClient: gitHubClient.Object,
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "verify-release-published",
                "--repository",
                GitHubRepository,
                "--tag",
                ReleaseValidationFixture.Tag,
            ]);

        result.ExitCode.ShouldBe(1);
        stdout.ToString().ShouldBe(
            $"::error title=GitHub Release not published::{message}" +
            Environment.NewLine);
        stderr.ToString().ShouldBe(message + Environment.NewLine);
    }

    [Fact]
    public async Task VerifyReleasePublishedCommand_RefusesAReleaseThatCannotBeRead()
    {
        const string message =
            "GITHUB_TOKEN is required for GetPublishedRelease.";
        Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
        gitHubClient
            .Setup(client => client.GetPublishedReleaseAsync(
                GitHubRepository,
                ReleaseValidationFixture.Tag,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseValidationException(message));
        StringWriter stdout = new();
        StringWriter stderr = new();
        ReleaseCommandServices services = CreateServices(
            gitHubClient: gitHubClient.Object,
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "verify-release-published",
                "--repository",
                GitHubRepository,
                "--tag",
                ReleaseValidationFixture.Tag,
            ]);

        result.ExitCode.ShouldBe(1);
        stdout.ToString().ShouldBe(
            $"::error title=GitHub Release not published::{message}" +
            Environment.NewLine);
        stderr.ToString().ShouldBe(message + Environment.NewLine);
    }

    [Fact]
    public async Task VerifyReleasePublishedCommand_RequiresARepository()
    {
        const string message =
            "--repository is required when GITHUB_REPOSITORY is not set.";
        Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
        StringWriter stdout = new();
        StringWriter stderr = new();
        ReleaseCommandServices services = CreateServices(
            gitHubClient: gitHubClient.Object,
            standardOutput: stdout,
            standardError: stderr);

        CommandInvocationResult result = await InvokeAsync(
            services,
            [
                "verify-release-published",
                "--repository",
                "",
                "--tag",
                ReleaseValidationFixture.Tag,
            ]);

        result.ExitCode.ShouldBe(1);
        stdout.ToString().ShouldBe(
            $"::error title=GitHub Release not published::{message}" +
            Environment.NewLine);
        stderr.ToString().ShouldBe(message + Environment.NewLine);
        gitHubClient.VerifyNoOtherCalls();
    }

    private static ReleaseCommandServices CreateServices(
        IReleaseInputValidator? inputValidator = null,
        IReleaseVersionAvailabilityValidator? versionAvailabilityValidator = null,
        IReleaseCandidateValidator? candidateValidator = null,
        IReleasePublicationStateValidator? publicationStateValidator = null,
        IPublishedPackageValidator? publishedPackageValidator = null,
        IFixtureBundleLocator? fixtureBundleLocator = null,
        IReleaseRecoveryVerifier? recoveryVerifier = null,
        IReleaseGitHubClient? gitHubClient = null,
        IGitHubOutputWriter? gitHubOutputWriter = null,
        IGitHubStepSummaryWriter? stepSummaryWriter = null,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        return new ReleaseCommandServices(
            inputValidator ?? new ThrowingReleaseInputValidator(),
            versionAvailabilityValidator ??
            new ThrowingReleaseVersionAvailabilityValidator(),
            candidateValidator ?? new ThrowingReleaseCandidateValidator(),
            publicationStateValidator ??
            new ThrowingReleasePublicationStateValidator(),
            publishedPackageValidator ??
            new ThrowingPublishedPackageValidator(),
            fixtureBundleLocator ?? new ThrowingFixtureBundleLocator(),
            recoveryVerifier ??
            new Mock<IReleaseRecoveryVerifier>(MockBehavior.Strict).Object,
            gitHubClient ??
            new Mock<IReleaseGitHubClient>(MockBehavior.Strict).Object,
            gitHubOutputWriter ?? new GitHubOutputWriter(),
            stepSummaryWriter ?? new GitHubStepSummaryWriter(),
            standardOutput ?? TextWriter.Null,
            standardError ?? TextWriter.Null);
    }

    private static async Task<CommandInvocationResult> InvokeAsync(
        ReleaseCommandServices services,
        string[] args)
    {
        RootCommand rootCommand = Program.BuildRootCommand(services);
        ParseResult parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            throw new ShouldAssertException(
                "Unexpected parse errors: " +
                string.Join(
                    " | ",
                    parseResult.Errors.Select(
                        static error => error.Message)));
        }

        await s_consoleGate.WaitAsync(TestContext.Current.CancellationToken);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        StringWriter diagnosticOutput = new();
        StringWriter diagnosticError = new();
        try
        {
            Console.SetOut(diagnosticOutput);
            Console.SetError(diagnosticError);
            int exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);
            return new CommandInvocationResult(
                exitCode,
                diagnosticOutput.ToString(),
                diagnosticError.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            s_consoleGate.Release();
        }
    }

    private static HttpResponseMessage CreateJsonResponse(string content)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK);
        response.Content = new StringContent(
            content,
            Encoding.UTF8,
            "application/json");
        return response;
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        byte[]? content = null)
    {
        HttpResponseMessage response = new(statusCode);
        response.Content = new ByteArrayContent(content ?? []);
        return response;
    }

    private static byte[] CreatePackageVariant(
        string sourcePath,
        IReadOnlyList<(string Name, byte[] Content)> additions)
    {
        using MemoryStream memoryStream = new();
        using (ZipArchive outputArchive = new(
            memoryStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        using (ZipArchive sourceArchive = ZipFile.OpenRead(sourcePath))
        {
            foreach (ZipArchiveEntry sourceEntry in sourceArchive.Entries)
            {
                ZipArchiveEntry outputEntry =
                    outputArchive.CreateEntry(sourceEntry.FullName);
                using Stream sourceStream = sourceEntry.Open();
                using Stream outputStream = outputEntry.Open();
                sourceStream.CopyTo(outputStream);
            }

            foreach ((string Name, byte[] Content) addition in additions)
            {
                ZipArchiveEntry outputEntry =
                    outputArchive.CreateEntry(addition.Name);
                using Stream outputStream = outputEntry.Open();
                outputStream.Write(addition.Content);
            }
        }

        return memoryStream.ToArray();
    }

    private static byte[] CreateZipArchive(
        params (string Name, string Content)[] entries)
    {
        using MemoryStream memoryStream = new();
        using (ZipArchive archive = new(
            memoryStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach ((string Name, string Content) entry in entries)
            {
                ZipArchiveEntry archiveEntry = archive.CreateEntry(entry.Name);
                using Stream entryStream = archiveEntry.Open();
                entryStream.Write(Encoding.UTF8.GetBytes(entry.Content));
            }
        }

        return memoryStream.ToArray();
    }

    private static string ComputeDigest(byte[] content) =>
        "sha256:" +
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static Mock<IReleaseGitHubClient> CreateDownloadingGitHubClient(
        byte[] archive,
        string returnedDigest,
        List<string> downloadPaths)
    {
        Mock<IReleaseGitHubClient> gitHubClient = new(MockBehavior.Strict);
        gitHubClient
            .Setup(client => client.DownloadArtifactZipAsync(
                GitHubRepository,
                77,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(
                (string _,
                 long _,
                 string destinationFile,
                 CancellationToken _) =>
                {
                    downloadPaths.Add(destinationFile);
                    File.WriteAllBytes(destinationFile, archive);
                    return Task.FromResult(returnedDigest);
                });
        return gitHubClient;
    }

    private static Mock<IReleaseRecoveryVerifier> CreateRecoveryVerifier(
        RecoveryVerificationResult result,
        List<RecoveryRequest> requests)
    {
        Mock<IReleaseRecoveryVerifier> verifier = new(MockBehavior.Strict);
        verifier
            .Setup(recoveryVerifier => recoveryVerifier.VerifyAsync(
                It.IsAny<RecoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback(
                (RecoveryRequest request, CancellationToken _) =>
                    requests.Add(request))
            .ReturnsAsync(result);
        return verifier;
    }

    private static RecoveryVerificationResult CreateAuthorizedRecoveryResult(
        string? approvalAuthor) =>
        new(
            [
                new RecoveryRuleResult(
                    "R1",
                    "release-published",
                    RecoveryRuleOutcome.Reported,
                    $"GitHub Release {ReleaseValidationFixture.Tag} is published: {RecoveryReleaseUrl} Dry run: reported, not enforced."),
                new RecoveryRuleResult(
                    "R2",
                    "original-run",
                    RecoveryRuleOutcome.Passed,
                    $"Workflow run 4242 is a completed 'workflow_dispatch' run (accepted in a dry run) of .github/workflows/nuget-generator.yaml at {ReleaseValidationFixture.RepositoryCommit}: {RecoveryRunUrl}"),
                new RecoveryRuleResult(
                    "R3",
                    "source-ci",
                    RecoveryRuleOutcome.Passed,
                    "All 2 'exact-source-ci / *' jobs and 'pack-release-candidate' concluded 'success' in their latest attempts."),
                new RecoveryRuleResult(
                    "R4",
                    "retained",
                    RecoveryRuleOutcome.Passed,
                    $"Candidate 'fhir-pkg-2099.101.1-candidate' (artifact 21, {CandidateDigest}) and qualification inputs 'fhir-pkg-2099.101.1-fixture-bundle' (artifact 22, {BundleDigest}) are retained."),
                new RecoveryRuleResult(
                    "R5",
                    "approval",
                    RecoveryRuleOutcome.Passed,
                    $"docs/releases/approvals/v2099.101.1.json at approval commit {RecoveryApprovalCommit} (blob 1111111111111111111111111111111111111111, authored by {RecoveryApprovalAuthor}) is unchanged on origin/main."),
            ],
            ReleasePublished: true,
            CreateRecoveryArtifact(
                21,
                "fhir-pkg-2099.101.1-candidate",
                CandidateDigest),
            CreateRecoveryArtifact(
                22,
                "fhir-pkg-2099.101.1-fixture-bundle",
                BundleDigest),
            [$"{RecoveryRunUrl}/job/12", $"{RecoveryRunUrl}/job/13"],
            approvalAuthor);

    private static GitHubArtifact CreateRecoveryArtifact(
        long id,
        string name,
        string digest) =>
        new(
            id,
            name,
            Expired: false,
            digest,
            SizeInBytes: 4096,
            new DateTimeOffset(2027, 1, 14, 6, 23, 45, TimeSpan.Zero),
            4242);

    private static string[] ReadOutputKeys(string outputPath) =>
        [.. ReadOutputLines(outputPath)
            .Select(
                static line => line[.. line.IndexOf('=')])];

    private static string[] ReadOutputLines(string outputPath) =>
        File.ReadAllLines(outputPath);

    private sealed record CommandInvocationResult(
        int ExitCode,
        string DiagnosticOutput,
        string DiagnosticError);

    private sealed record ReleaseInputInvocation(
        string Version,
        string Tag,
        string? GitHubRef,
        string SdkIndexUri,
        string CliIndexUri,
        bool AllowPublishedVersion);

    private sealed class CapturingReleaseInputValidator
        : IReleaseInputValidator
    {
        private readonly ReleaseInputValidationResult _result;

        internal CapturingReleaseInputValidator(
            ReleaseInputValidationResult result)
        {
            _result = result;
        }

        internal List<ReleaseInputInvocation> Invocations { get; } = [];

        public Task<ReleaseInputValidationResult> ValidateAsync(
            string version,
            string tag,
            string? githubRef,
            string sdkIndexUri,
            string cliIndexUri,
            bool allowPublishedVersion,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new ReleaseInputInvocation(
                version,
                tag,
                githubRef,
                sdkIndexUri,
                cliIndexUri,
                allowPublishedVersion));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingReleaseInputValidator
        : IReleaseInputValidator
    {
        private readonly Exception _exception;

        internal ThrowingReleaseInputValidator(
            Exception? exception = null)
        {
            _exception = exception ?? new ShouldAssertException(
                "The release input validator should not be called.");
        }

        public Task<ReleaseInputValidationResult> ValidateAsync(
            string version,
            string tag,
            string? githubRef,
            string sdkIndexUri,
            string cliIndexUri,
            bool allowPublishedVersion,
            CancellationToken cancellationToken) =>
            Task.FromException<ReleaseInputValidationResult>(_exception);
    }

    private sealed record ReleaseVersionInvocation(
        string Version,
        string SdkIndexUri,
        string CliIndexUri);

    private sealed class CapturingReleaseVersionAvailabilityValidator
        : IReleaseVersionAvailabilityValidator
    {
        internal List<ReleaseVersionInvocation> Invocations { get; } = [];

        public Task ValidateAsync(
            string version,
            string sdkIndexUri,
            string cliIndexUri,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new ReleaseVersionInvocation(
                version,
                sdkIndexUri,
                cliIndexUri));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReleaseVersionAvailabilityValidator
        : IReleaseVersionAvailabilityValidator
    {
        public Task ValidateAsync(
            string version,
            string sdkIndexUri,
            string cliIndexUri,
            CancellationToken cancellationToken) =>
            throw new ShouldAssertException(
                "The release version validator should not be called.");
    }

    private sealed record ReleaseCandidateInvocation(
        string CandidateDirectory,
        string Version,
        string Tag,
        string RepositoryCommit,
        string? ExpectedSdkPackageSha256,
        string? ExpectedCliPackageSha256);

    private sealed class CapturingReleaseCandidateValidator
        : IReleaseCandidateValidator
    {
        private readonly ReleaseCandidateValidationResult _result;

        internal CapturingReleaseCandidateValidator(
            ReleaseCandidateValidationResult result)
        {
            _result = result;
        }

        internal List<ReleaseCandidateInvocation> Invocations { get; } = [];

        public ReleaseCandidateValidationResult Validate(
            string candidateDirectory,
            string version,
            string tag,
            string repositoryCommit,
            string? expectedSdkPackageSha256 = null,
            string? expectedCliPackageSha256 = null)
        {
            Invocations.Add(new ReleaseCandidateInvocation(
                candidateDirectory,
                version,
                tag,
                repositoryCommit,
                expectedSdkPackageSha256,
                expectedCliPackageSha256));
            return _result;
        }
    }

    private sealed class ThrowingReleaseCandidateValidator
        : IReleaseCandidateValidator
    {
        public ReleaseCandidateValidationResult Validate(
            string candidateDirectory,
            string version,
            string tag,
            string repositoryCommit,
            string? expectedSdkPackageSha256 = null,
            string? expectedCliPackageSha256 = null) =>
            throw new ShouldAssertException(
                "The release candidate validator should not be called.");
    }

    private sealed record ReleasePublicationStateInvocation(
        string CandidateDirectory,
        string Version,
        string RepositoryCommit,
        string SdkFlatContainerUri,
        string CliFlatContainerUri,
        int Attempts,
        int DelaySeconds,
        bool SkipSignatureVerification);

    private sealed class CapturingReleasePublicationStateValidator
        : IReleasePublicationStateValidator
    {
        private readonly ReleasePublicationStateResult _result;

        internal CapturingReleasePublicationStateValidator(
            ReleasePublicationStateResult result)
        {
            _result = result;
        }

        internal List<ReleasePublicationStateInvocation> Invocations
        {
            get;
        } = [];

        public Task<ReleasePublicationStateResult> ValidateAsync(
            string candidateDirectory,
            string version,
            string repositoryCommit,
            string sdkFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib",
            string cliFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli",
            int attempts = 5,
            int delaySeconds = 5,
            bool skipSignatureVerification = false,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(new ReleasePublicationStateInvocation(
                candidateDirectory,
                version,
                repositoryCommit,
                sdkFlatContainerUri,
                cliFlatContainerUri,
                attempts,
                delaySeconds,
                skipSignatureVerification));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingReleasePublicationStateValidator
        : IReleasePublicationStateValidator
    {
        public Task<ReleasePublicationStateResult> ValidateAsync(
            string candidateDirectory,
            string version,
            string repositoryCommit,
            string sdkFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib",
            string cliFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli",
            int attempts = 5,
            int delaySeconds = 5,
            bool skipSignatureVerification = false,
            CancellationToken cancellationToken = default) =>
            throw new ShouldAssertException(
                "The publication state validator should not be called.");
    }

    private sealed record PublishedPackageInvocation(
        string PackageId,
        string CandidatePackagePath,
        string PublishedPackageUri,
        string Version,
        string RepositoryCommit,
        int Attempts,
        int DelaySeconds,
        bool SkipSignatureVerification);

    private sealed class CapturingPublishedPackageValidator
        : IPublishedPackageValidator
    {
        private readonly PublishedPackageValidationResult _result;

        internal CapturingPublishedPackageValidator(
            PublishedPackageValidationResult result)
        {
            _result = result;
        }

        internal List<PublishedPackageInvocation> Invocations { get; } = [];

        public Task<PublishedPackageValidationResult> ValidateAsync(
            string packageId,
            string candidatePackagePath,
            string publishedPackageUri,
            string version,
            string repositoryCommit,
            int attempts = 45,
            int delaySeconds = 20,
            bool skipSignatureVerification = false,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(new PublishedPackageInvocation(
                packageId,
                candidatePackagePath,
                publishedPackageUri,
                version,
                repositoryCommit,
                attempts,
                delaySeconds,
                skipSignatureVerification));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingPublishedPackageValidator
        : IPublishedPackageValidator
    {
        public Task<PublishedPackageValidationResult> ValidateAsync(
            string packageId,
            string candidatePackagePath,
            string publishedPackageUri,
            string version,
            string repositoryCommit,
            int attempts = 45,
            int delaySeconds = 20,
            bool skipSignatureVerification = false,
            CancellationToken cancellationToken = default) =>
            throw new ShouldAssertException(
                "The published package validator should not be called.");
    }

    private sealed record FixtureBundleLocatorInvocation(
        string Repository,
        string Commit,
        string Tag,
        string CorpusPath,
        DateTimeOffset Now);

    private sealed class CapturingFixtureBundleLocator
        : IFixtureBundleLocator
    {
        private readonly LocatedFixtureBundle _result;

        internal CapturingFixtureBundleLocator(
            LocatedFixtureBundle result)
        {
            _result = result;
        }

        internal List<FixtureBundleLocatorInvocation> Invocations
        {
            get;
        } = [];

        public Task<LocatedFixtureBundle> LocateAsync(
            string repository,
            string commit,
            string tag,
            string corpusPath,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new FixtureBundleLocatorInvocation(
                repository,
                commit,
                tag,
                corpusPath,
                now));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingFixtureBundleLocator
        : IFixtureBundleLocator
    {
        private readonly Exception _exception;

        internal ThrowingFixtureBundleLocator(
            Exception? exception = null)
        {
            _exception = exception ?? new ShouldAssertException(
                "The fixture bundle locator should not be called.");
        }

        public Task<LocatedFixtureBundle> LocateAsync(
            string repository,
            string commit,
            string tag,
            string corpusPath,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromException<LocatedFixtureBundle>(_exception);
    }

    private sealed class NoOpReleaseDelay : IReleaseDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>
            _responses;

        internal SequenceHttpMessageHandler(
            params Func<HttpRequestMessage, Task<HttpResponseMessage>>[]
                responses)
        {
            _responses =
                new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>(
                    responses);
        }

        internal int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _responses.Count.ShouldBeGreaterThan(0);
            Func<HttpRequestMessage, Task<HttpResponseMessage>> response =
                _responses.Dequeue();
            return response(request);
        }
    }

    private sealed class ThrowingProcessRunner : IReleaseProcessRunner
    {
        public Task<ReleaseProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            throw new ShouldAssertException(
                "The process runner should not be called.");
    }

    private sealed class FakeReleaseGitClient : IReleaseGitClient
    {
        internal const string HeadCommit =
            "0123456789abcdef0123456789abcdef01234567";
        internal const string MainCommit =
            "89abcdef0123456789abcdef0123456789abcdef";

        public Task<ReleaseGitCommandResult> CheckAncestorAsync(
            string ancestorCommit,
            string descendantRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty));

        public Task<ReleaseGitCommandResult> FetchOriginMainAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty));

        public Task<ReleaseGitCommandResult> FetchReleaseTagAsync(
            string tag,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                string.Empty,
                string.Empty));

        public Task<ReleaseGitCommandResult> ResolveHeadCommitAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                $"{HeadCommit}{Environment.NewLine}",
                string.Empty));

        public Task<ReleaseGitCommandResult> ResolveOriginMainCommitAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                $"{MainCommit}{Environment.NewLine}",
                string.Empty));

        public Task<ReleaseGitCommandResult> ResolveTagCommitAsync(
            string tag,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseGitCommandResult(
                0,
                $"{HeadCommit}{Environment.NewLine}",
                string.Empty));
    }
}
