// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleasePublicationStateValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ReportsMatchingPartialRelease()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)));
            HttpClient httpClient = new(handler);
            CapturingPublishedPackageValidator publishedPackageValidator = new();
            ReleasePublicationStateValidator validator = new(
                httpClient,
                publishedPackageValidator,
                new NoOpReleaseDelay());

            ReleasePublicationStateResult result =
                await validator.ValidateAsync(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        sdkFlatContainerUri: "https://packages.example/sdk",
                        cliFlatContainerUri: "https://packages.example/cli",
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: true,
                        cancellationToken: TestContext.Current.CancellationToken)
                    ;

            result.CliState.ShouldBe(ReleasePackagePublicationState.Missing);
            result.SdkState.ShouldBe(ReleasePackagePublicationState.Verified);
            publishedPackageValidator.Invocations.Count.ShouldBe(1);
            PublishedInvocation invocation =
                publishedPackageValidator.Invocations[0];
            invocation.PackageId.ShouldBe("fhir-pkg-lib");
            invocation.CandidatePackagePath.ShouldBe(Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg"));
            invocation.PublishedPackageUri.ShouldBe(
                $"https://packages.example/sdk/{ReleaseValidationFixture.Version.ToLowerInvariant()}/fhir-pkg-lib.{ReleaseValidationFixture.Version.ToLowerInvariant()}.nupkg");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_ReportsBothPackagesMissing()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)));
            HttpClient httpClient = new(handler);
            CapturingPublishedPackageValidator publishedPackageValidator = new();
            ReleasePublicationStateValidator validator = new(
                httpClient,
                publishedPackageValidator,
                new NoOpReleaseDelay());

            ReleasePublicationStateResult result =
                await validator.ValidateAsync(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        sdkFlatContainerUri: "https://packages.example/sdk",
                        cliFlatContainerUri: "https://packages.example/cli",
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: true,
                        cancellationToken: TestContext.Current.CancellationToken)
                    ;

            result.CliState.ShouldBe(ReleasePackagePublicationState.Missing);
            result.SdkState.ShouldBe(ReleasePackagePublicationState.Missing);
            publishedPackageValidator.Invocations.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsMismatchedExistingPackage()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        string mismatchedPackagePath = Path.Combine(
            candidateDirectory,
            $"published-fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
        ReleaseValidationFixture.CreateSdkPackage(
            mismatchedPackagePath,
            mismatchSdkAssembly: true);
        try
        {
            byte[] publishedBytes = File.ReadAllBytes(mismatchedPackagePath);
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)),
                _ => Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    publishedBytes)));
            HttpClient httpClient = new(handler);
            PublishedPackageValidator publishedPackageValidator = new(
                httpClient,
                new ThrowingProcessRunner(),
                new ReleasePackageValidator(),
                new NoOpReleaseDelay());
            ReleasePublicationStateValidator validator = new(
                httpClient,
                publishedPackageValidator,
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            candidateDirectory,
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            sdkFlatContainerUri: "https://packages.example/sdk",
                            cliFlatContainerUri: "https://packages.example/cli",
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
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_VerifiesVisiblePackagesInCliThenSdkOrder()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.OK)));
            HttpClient httpClient = new(handler);
            CapturingPublishedPackageValidator publishedPackageValidator = new();
            ReleasePublicationStateValidator validator = new(
                httpClient,
                publishedPackageValidator,
                new NoOpReleaseDelay());

            ReleasePublicationStateResult result =
                await validator.ValidateAsync(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        sdkFlatContainerUri: "https://packages.example/sdk/",
                        cliFlatContainerUri: "https://packages.example/cli/",
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: true,
                        cancellationToken: TestContext.Current.CancellationToken)
                    ;

            result.CliState.ShouldBe(ReleasePackagePublicationState.Verified);
            result.SdkState.ShouldBe(ReleasePackagePublicationState.Verified);
            publishedPackageValidator.Invocations.Count.ShouldBe(2);
            publishedPackageValidator.Invocations[0].PackageId.ShouldBe(
                "fhir-pkg-cli");
            publishedPackageValidator.Invocations[1].PackageId.ShouldBe(
                "fhir-pkg-lib");
            publishedPackageValidator.Invocations[0].PublishedPackageUri.ShouldBe(
                $"https://packages.example/cli/{ReleaseValidationFixture.Version.ToLowerInvariant()}/fhir-pkg-cli.{ReleaseValidationFixture.Version.ToLowerInvariant()}.nupkg");
            publishedPackageValidator.Invocations[1].PublishedPackageUri.ShouldBe(
                $"https://packages.example/sdk/{ReleaseValidationFixture.Version.ToLowerInvariant()}/fhir-pkg-lib.{ReleaseValidationFixture.Version.ToLowerInvariant()}.nupkg");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_ThrowsExactMessageForNonRetryableStatus()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized)));
            HttpClient httpClient = new(handler);
            ReleasePublicationStateValidator validator = new(
                httpClient,
                new CapturingPublishedPackageValidator(),
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            candidateDirectory,
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            sdkFlatContainerUri: "https://packages.example/sdk",
                            cliFlatContainerUri: "https://packages.example/cli",
                            attempts: 1,
                            delaySeconds: 0,
                            skipSignatureVerification: true,
                            cancellationToken: TestContext.Current.CancellationToken))
                    ;

            exception.Message.ShouldBe(
                "Package visibility check for 'fhir-pkg-cli' returned HTTP 401.");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_ThrowsExactExhaustionMessageAfterRetryableStatuses()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.ServiceUnavailable)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.ServiceUnavailable)));
            HttpClient httpClient = new(handler);
            ReleasePublicationStateValidator validator = new(
                httpClient,
                new CapturingPublishedPackageValidator(),
                new NoOpReleaseDelay());

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                        () => validator.ValidateAsync(
                            candidateDirectory,
                            ReleaseValidationFixture.Version,
                            ReleaseValidationFixture.RepositoryCommit,
                            sdkFlatContainerUri: "https://packages.example/sdk",
                            cliFlatContainerUri: "https://packages.example/cli",
                            attempts: 2,
                            delaySeconds: 0,
                            skipSignatureVerification: true,
                            cancellationToken: TestContext.Current.CancellationToken))
                    ;

            exception.Message.ShouldBe(
                "Unable to determine publication state for 'fhir-pkg-cli' after 2 attempts.");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_EnforcesPerAttemptRequestTimeout()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            NeverCompletingHttpMessageHandler handler = new();
            using HttpClient httpClient = new(handler);
            ReleasePublicationStateValidator validator = new(
                httpClient,
                new CapturingPublishedPackageValidator(),
                new NoOpReleaseDelay(),
                requestTimeout: TimeSpan.FromMilliseconds(10));

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                    () => validator.ValidateAsync(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit,
                        sdkFlatContainerUri: "https://packages.example/sdk",
                        cliFlatContainerUri: "https://packages.example/cli",
                        attempts: 1,
                        delaySeconds: 0,
                        skipSignatureVerification: true,
                        cancellationToken:
                            TestContext.Current.CancellationToken));

            exception.Message.ShouldBe(
                "Unable to determine publication state for 'fhir-pkg-cli' after 1 attempts.");
            handler.CallCount.ShouldBe(1);
            handler.CancellationObserved.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RetriesRetryableStatusOnlyAfterDelay()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.TooManyRequests)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)));
            HttpClient httpClient = new(handler);
            GatedReleaseDelay delay = new();
            CapturingPublishedPackageValidator publishedPackageValidator = new();
            ReleasePublicationStateValidator validator = new(
                httpClient,
                publishedPackageValidator,
                delay);

            Task<ReleasePublicationStateResult> validationTask =
                validator.ValidateAsync(
                    candidateDirectory,
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit,
                    sdkFlatContainerUri: "https://packages.example/sdk",
                    cliFlatContainerUri: "https://packages.example/cli",
                    attempts: 2,
                    delaySeconds: 1,
                    skipSignatureVerification: true,
                    cancellationToken: TestContext.Current.CancellationToken);
            await delay.WaitForEntryAsync();

            handler.CallCount.ShouldBe(1);
            delay.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);
            validationTask.IsCompleted.ShouldBeFalse();

            delay.Release();
            ReleasePublicationStateResult result =
                await validationTask;

            result.CliState.ShouldBe(ReleasePackagePublicationState.Missing);
            result.SdkState.ShouldBe(ReleasePackagePublicationState.Missing);
            publishedPackageValidator.Invocations.ShouldBeEmpty();
            handler.CallCount.ShouldBe(3);
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_CancellationDuringDelayStopsRetries()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            SequenceHttpMessageHandler handler = new(
                _ => Task.FromResult(CreateResponse(HttpStatusCode.ServiceUnavailable)),
                _ => Task.FromResult(CreateResponse(HttpStatusCode.NotFound)));
            HttpClient httpClient = new(handler);
            GatedReleaseDelay delay = new();
            ReleasePublicationStateValidator validator = new(
                httpClient,
                new CapturingPublishedPackageValidator(),
                delay);
            using CancellationTokenSource cancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);

            Task<ReleasePublicationStateResult> validationTask =
                validator.ValidateAsync(
                    candidateDirectory,
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit,
                    sdkFlatContainerUri: "https://packages.example/sdk",
                    cliFlatContainerUri: "https://packages.example/cli",
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
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        byte[]? content = null)
    {
        HttpResponseMessage response = new(statusCode);
        response.Content = new ByteArrayContent(content ?? []);
        return response;
    }

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

    private sealed class CapturingPublishedPackageValidator
        : IPublishedPackageValidator
    {
        internal List<PublishedInvocation> Invocations { get; } = [];

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
            Invocations.Add(new PublishedInvocation(
                packageId,
                candidatePackagePath,
                publishedPackageUri,
                version,
                repositoryCommit,
                attempts,
                delaySeconds,
                skipSignatureVerification));
            return Task.FromResult(
                new PublishedPackageValidationResult(
                    "published-sha256"));
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

    private sealed record PublishedInvocation(
        string PackageId,
        string CandidatePackagePath,
        string PublishedPackageUri,
        string Version,
        string RepositoryCommit,
        int Attempts,
        int DelaySeconds,
        bool SkipSignatureVerification);
}
