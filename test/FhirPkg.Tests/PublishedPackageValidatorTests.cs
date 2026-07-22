// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class PublishedPackageValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsMatchingPublishedPackageAndIgnoresSigningEntries()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            byte[] publishedBytes = CreatePackageVariant(
                candidatePackagePath,
                additions:
                [
                    (
                        ".SIGNATURE.P7S",
                        [0x01, 0x02]),
                    (
                        "_RELS/.RELS",
                        [0x03]),
                    (
                        "[content_types].xml",
                        [0x04]),
                ]);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    publishedBytes)));
            HttpClient httpClient = new(handler);
            RecordingProcessRunner processRunner = new(0);
            PublishedPackageValidator validator = new(
                httpClient,
                processRunner,
                new ReleasePackageValidator(),
                new NoOpReleaseDelay());

            PublishedPackageValidationResult result =
                await validator.ValidateAsync(
                        ReleasePackageValidationCommon.SdkPackageId,
                        candidatePackagePath,
                        "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: false,
                        cancellationToken: TestContext.Current.CancellationToken)
                    ;

            result.PublishedSha256.ShouldBe(GetSha256(publishedBytes));
            processRunner.Invocations.Count.ShouldBe(1);
            processRunner.Invocations[0].FileName.ShouldBe("dotnet");
            IsSignatureVerificationArguments(
                processRunner.Invocations[0].Arguments)
                .ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsPublishedPackageWithUnexpectedEntryCount()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            byte[] publishedBytes = CreatePackageVariant(
                candidatePackagePath,
                additions:
                [
                    (
                        "extra.txt",
                        [0x45]),
                ]);
            PublishedPackageValidator validator = CreateValidator(
                publishedBytes,
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            ReleasePackageValidationCommon.SdkPackageId,
                            candidatePackagePath,
                            "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            attempts: 1,
                            delaySeconds: 0,
                            skipSignatureVerification: true,
                            cancellationToken: TestContext.Current.CancellationToken))
                    ;

            exception.Message.ShouldBe(
                "Published package entries do not match the release candidate.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsPublishedPackageWithDifferingEntry()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            string publishedPackagePath = Path.Combine(
                workingDirectory,
                $"published-fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            ReleaseValidationFixture.CreateSdkPackage(
                publishedPackagePath,
                mismatchSdkAssembly: true);
            byte[] publishedBytes = File.ReadAllBytes(publishedPackagePath);
            PublishedPackageValidator validator = CreateValidator(
                publishedBytes,
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            ReleasePackageValidationCommon.SdkPackageId,
                            candidatePackagePath,
                            "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            attempts: 1,
                            delaySeconds: 0,
                            skipSignatureVerification: true,
                            cancellationToken: TestContext.Current.CancellationToken))
                    ;

            exception.Message.ShouldBe(
                "Published package entry 'lib/net9.0/FhirPkg.dll' differs from the release candidate.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_ThrowsExactExhaustionMessageAfterRetryableFailures()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)));
            HttpClient httpClient = new(handler);
            PublishedPackageValidator validator = new(
                httpClient,
                new ThrowingProcessRunner(),
                new ReleasePackageValidator(),
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            ReleasePackageValidationCommon.SdkPackageId,
                            candidatePackagePath,
                            "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            attempts: 2,
                            delaySeconds: 0,
                            skipSignatureVerification: true,
                            cancellationToken: TestContext.Current.CancellationToken))
                    ;

            exception.Message.ShouldBe(
                "Published package was not available after 2 attempts.");
            handler.CallCount.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_EnforcesPerAttemptRequestTimeout()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            NeverCompletingHttpMessageHandler handler = new();
            using HttpClient httpClient = new(handler);
            PublishedPackageValidator validator = new(
                httpClient,
                new ThrowingProcessRunner(),
                new ReleasePackageValidator(),
                new NoOpReleaseDelay(),
                requestTimeout: TimeSpan.FromMilliseconds(10));

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                    () => validator.ValidateAsync(
                        ReleasePackageValidationCommon.SdkPackageId,
                        candidatePackagePath,
                        "https://packages.example/fhir-pkg-lib/package.nupkg",
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: true,
                        cancellationToken:
                            TestContext.Current.CancellationToken));

            exception.Message.ShouldBe(
                "Published package was not available after 1 attempts.");
            handler.CallCount.ShouldBe(1);
            handler.CancellationObserved.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RetriesTransientNotFoundOnlyAfterDelay()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            byte[] publishedBytes = File.ReadAllBytes(candidatePackagePath);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    publishedBytes)));
            HttpClient httpClient = new(handler);
            GatedReleaseDelay delay = new();
            PublishedPackageValidator validator = new(
                httpClient,
                new ThrowingProcessRunner(),
                new ReleasePackageValidator(),
                delay);

            Task<PublishedPackageValidationResult> validationTask =
                validator.ValidateAsync(
                    ReleasePackageValidationCommon.SdkPackageId,
                    candidatePackagePath,
                    "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit,
                    attempts: 2,
                    delaySeconds: 1,
                    skipSignatureVerification: true,
                    cancellationToken: TestContext.Current.CancellationToken);
            await delay.WaitForEntryAsync();

            handler.CallCount.ShouldBe(1);
            validationTask.IsCompleted.ShouldBeFalse();
            delay.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);

            delay.Release();
            PublishedPackageValidationResult result =
                await validationTask;

            result.PublishedSha256.ShouldBe(GetSha256(publishedBytes));
            handler.CallCount.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_CancellationDuringDelayStopsRetries()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string candidatePackagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateSdkPackage(candidatePackagePath);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)));
            HttpClient httpClient = new(handler);
            GatedReleaseDelay delay = new();
            PublishedPackageValidator validator = new(
                httpClient,
                new ThrowingProcessRunner(),
                new ReleasePackageValidator(),
                delay);
            using CancellationTokenSource cancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);

            Task<PublishedPackageValidationResult> validationTask =
                validator.ValidateAsync(
                    ReleasePackageValidationCommon.SdkPackageId,
                    candidatePackagePath,
                    "https://packages.example/fhir-pkg-lib/2099.101.1/fhir-pkg-lib.2099.101.1.nupkg",
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit,
                    attempts: 2,
                    delaySeconds: 1,
                    skipSignatureVerification: true,
                    cancellationToken: cancellationTokenSource.Token);
            await delay.WaitForEntryAsync();

            cancellationTokenSource.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(
                    () => validationTask)
                ;
            handler.CallCount.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static PublishedPackageValidator CreateValidator(
        byte[] publishedBytes,
        IReleaseDelay delay)
    {
        SequenceHttpMessageHandler handler = new(
            _ => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                publishedBytes)));
        HttpClient httpClient = new(handler);
        return new PublishedPackageValidator(
            httpClient,
            new ThrowingProcessRunner(),
            new ReleasePackageValidator(),
            delay);
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

    private static bool IsSignatureVerificationArguments(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 6 ||
            !string.Equals(arguments[0], "nuget", StringComparison.Ordinal) ||
            !string.Equals(arguments[1], "verify", StringComparison.Ordinal) ||
            !string.Equals(arguments[2], "--all", StringComparison.Ordinal) ||
            !string.Equals(arguments[3], "--verbosity", StringComparison.Ordinal) ||
            !string.Equals(arguments[4], "minimal", StringComparison.Ordinal))
        {
            return false;
        }

        string packagePath = arguments[5];
        string? directoryName = Path.GetFileName(
            Path.GetDirectoryName(packagePath));
        return string.Equals(
                   Path.GetFileName(packagePath),
                   $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg",
                   StringComparison.Ordinal) &&
               !string.IsNullOrEmpty(directoryName) &&
               directoryName.StartsWith(
                   "fhirpkg-published-",
                   StringComparison.Ordinal);
    }

    private static string GetSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    private sealed class NoOpReleaseDelay : IReleaseDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class GatedReleaseDelay : IReleaseDelay
    {
        private TaskCompletionSource<bool>? _gate;
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            TaskCompletionSource<bool> gate = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = gate;
            _entered.TrySetResult(true);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(
                    static state =>
                        ((TaskCompletionSource<bool>)state!)
                            .TrySetCanceled(),
                    gate);
            }

            return gate.Task;
        }

        internal Task WaitForEntryAsync() => _entered.Task;

        internal void Release()
        {
            _gate.ShouldNotBeNull();
            _gate.TrySetResult(true);
        }
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses;

        internal SequenceHttpMessageHandler(
            params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>(
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

    private sealed class NeverCompletingHttpMessageHandler
        : HttpMessageHandler
    {
        internal bool CancellationObserved { get; private set; }

        internal int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException(
                    "The request timeout was not enforced.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
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

    private sealed class RecordingProcessRunner(int exitCode)
        : IReleaseProcessRunner
    {
        internal List<ProcessInvocation> Invocations { get; } = [];

        public Task<ReleaseProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new ProcessInvocation(fileName, [.. arguments]));
            return Task.FromResult(new ReleaseProcessResult(
                exitCode,
                string.Empty,
                string.Empty));
        }
    }

    private sealed record ProcessInvocation(
        string FileName,
        IReadOnlyList<string> Arguments);
}
