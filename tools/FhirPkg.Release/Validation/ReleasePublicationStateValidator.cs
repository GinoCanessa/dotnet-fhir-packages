// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Validation;

internal interface IReleasePublicationStateValidator
{
    Task<ReleasePublicationStateResult> ValidateAsync(
        string candidateDirectory,
        string version,
        string repositoryCommit,
        string sdkFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib",
        string cliFlatContainerUri = "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli",
        int attempts = 5,
        int delaySeconds = 5,
        bool skipSignatureVerification = false,
        CancellationToken cancellationToken = default);
}

internal sealed class ReleasePublicationStateValidator
    : IReleasePublicationStateValidator
{
    private static readonly TimeSpan s_defaultRequestTimeout =
        TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly IPublishedPackageValidator _publishedPackageValidator;
    private readonly IReleaseDelay _delay;
    private readonly TimeSpan _requestTimeout;

    internal ReleasePublicationStateValidator(
        HttpClient httpClient,
        IPublishedPackageValidator publishedPackageValidator,
        IReleaseDelay delay,
        TimeSpan? requestTimeout = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _publishedPackageValidator =
            publishedPackageValidator ??
            throw new ArgumentNullException(
                nameof(publishedPackageValidator));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _requestTimeout = requestTimeout ?? s_defaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The request timeout must be greater than zero.");
        }
    }

    public async Task<ReleasePublicationStateResult> ValidateAsync(
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
        if (attempts < 1)
        {
            throw new ReleaseValidationException(
                "Attempts must be at least one.");
        }

        if (delaySeconds < 0)
        {
            throw new ReleaseValidationException(
                "DelaySeconds cannot be negative.");
        }

        string fullCandidateDirectory = Path.GetFullPath(candidateDirectory);
        if (!Directory.Exists(fullCandidateDirectory))
        {
            throw new ReleaseValidationException(
                $"Candidate directory '{fullCandidateDirectory}' does not exist.");
        }

        ReleasePackagePublicationState cliState =
            await ValidatePackageAsync(
                    packageId: ReleasePackageValidationCommon.CliPackageId,
                    flatContainerUri: cliFlatContainerUri,
                    fullCandidateDirectory,
                    version,
                    repositoryCommit,
                    attempts,
                    delaySeconds,
                    skipSignatureVerification,
                    cancellationToken)
                .ConfigureAwait(false);
        ReleasePackagePublicationState sdkState =
            await ValidatePackageAsync(
                    packageId: ReleasePackageValidationCommon.SdkPackageId,
                    flatContainerUri: sdkFlatContainerUri,
                    fullCandidateDirectory,
                    version,
                    repositoryCommit,
                    attempts,
                    delaySeconds,
                    skipSignatureVerification,
                    cancellationToken)
                .ConfigureAwait(false);

        return new ReleasePublicationStateResult(cliState, sdkState);
    }

    private async Task<ReleasePackagePublicationState> ValidatePackageAsync(
        string packageId,
        string flatContainerUri,
        string fullCandidateDirectory,
        string version,
        string repositoryCommit,
        int attempts,
        int delaySeconds,
        bool skipSignatureVerification,
        CancellationToken cancellationToken)
    {
        string lowerVersion = version.ToLowerInvariant();
        string lowerPackageId = packageId.ToLowerInvariant();
        string packageUri =
            $"{flatContainerUri.TrimEnd('/')}/{lowerVersion}/{lowerPackageId}.{lowerVersion}.nupkg";
        bool visible = false;
        bool resolved = false;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                packageUri);
            using CancellationTokenSource requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            requestCancellation.CancelAfter(_requestTimeout);
            try
            {
                using HttpResponseMessage response =
                    await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            requestCancellation.Token)
                        .ConfigureAwait(false);
                int statusCode = (int)response.StatusCode;
                if (statusCode == 404)
                {
                    resolved = true;
                    break;
                }

                if (response.IsSuccessStatusCode)
                {
                    visible = true;
                    resolved = true;
                    break;
                }

                if (!IsRetryableVisibilityStatusCode(response.StatusCode))
                {
                    throw new ReleaseValidationException(
                        $"Package visibility check for '{packageId}' returned HTTP {statusCode}.");
                }
            }
            catch (HttpRequestException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (!resolved && attempt < attempts)
            {
                await _delay.DelayAsync(
                        TimeSpan.FromSeconds(delaySeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!resolved)
        {
            throw new ReleaseValidationException(
                $"Unable to determine publication state for '{packageId}' after {attempts} attempts.");
        }

        if (!visible)
        {
            return ReleasePackagePublicationState.Missing;
        }

        string candidatePackagePath = Path.Combine(
            fullCandidateDirectory,
            $"{packageId}.{version}.nupkg");
        await _publishedPackageValidator.ValidateAsync(
                packageId,
                candidatePackagePath,
                packageUri,
                version,
                repositoryCommit,
                attempts,
                delaySeconds,
                skipSignatureVerification,
                cancellationToken)
            .ConfigureAwait(false);
        return ReleasePackagePublicationState.Verified;
    }

    private static bool IsRetryableVisibilityStatusCode(
        HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 425 ||
        (int)statusCode == 429 ||
        statusCode == HttpStatusCode.InternalServerError ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout;
}
