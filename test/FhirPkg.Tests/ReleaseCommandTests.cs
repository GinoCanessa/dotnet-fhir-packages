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
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseCommandTests
{
    private static readonly SemaphoreSlim s_consoleGate =
        new(initialCount: 1, maxCount: 1);

    private const string CliIndexUri =
        "https://packages.example/cli/index.json";
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

    private static ReleaseCommandServices CreateServices(
        IReleaseInputValidator? inputValidator = null,
        IReleaseVersionAvailabilityValidator? versionAvailabilityValidator = null,
        IReleaseCandidateValidator? candidateValidator = null,
        IReleasePublicationStateValidator? publicationStateValidator = null,
        IPublishedPackageValidator? publishedPackageValidator = null,
        IGitHubOutputWriter? gitHubOutputWriter = null,
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
            gitHubOutputWriter ?? new GitHubOutputWriter(),
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
