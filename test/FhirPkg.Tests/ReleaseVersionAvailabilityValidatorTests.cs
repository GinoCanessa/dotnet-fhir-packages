// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseVersionAvailabilityValidatorTests
{
    private const string CliIndexUri =
        "https://example.test/cli/index.json";
    private const string SdkIndexUri =
        "https://example.test/sdk/index.json";

    [Fact]
    public async Task ValidateAsync_RejectsVersionBehindHighestCanonicalAcrossBothIndexes()
    {
        Dictionary<string, string[]> responses =
            new(StringComparer.Ordinal)
            {
                [SdkIndexUri] = ["2099.100.0", "preview-value"],
                [CliIndexUri] = ["2099.102.0"],
            };
        using RecordingVersionIndexHandler handler =
            new(responses);
        using HttpClient httpClient = new(handler);
        IReleaseVersionAvailabilityValidator validator =
            new ReleaseVersionAvailabilityValidator(httpClient);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => validator.ValidateAsync(
                    "2099.101.1",
                    SdkIndexUri,
                    CliIndexUri,
                    CancellationToken.None));

        exception.Message.ShouldBe(
            "Version '2099.101.1' must be greater than the highest published canonical version '2099.102.0'.");
        handler.RequestedUris.ToArray().ShouldBe(
            [SdkIndexUri, CliIndexUri]);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAlreadyPublishedVersion()
    {
        Dictionary<string, string[]> responses =
            new(StringComparer.Ordinal)
            {
                [SdkIndexUri] = ["2099.100.0"],
                [CliIndexUri] = ["2099.102.0", "2099.103.0"],
            };
        using RecordingVersionIndexHandler handler =
            new(responses);
        using HttpClient httpClient = new(handler);
        IReleaseVersionAvailabilityValidator validator =
            new ReleaseVersionAvailabilityValidator(httpClient);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => validator.ValidateAsync(
                    "2099.102.0",
                    SdkIndexUri,
                    CliIndexUri,
                    CancellationToken.None));

        exception.Message.ShouldBe(
            "fhir-pkg-cli '2099.102.0' is already published.");
        handler.RequestedUris.ToArray().ShouldBe(
            [SdkIndexUri, CliIndexUri]);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsFreshVersion()
    {
        Dictionary<string, string[]> responses =
            new(StringComparer.Ordinal)
            {
                [SdkIndexUri] = ["2099.100.0"],
                [CliIndexUri] = ["2099.102.0"],
            };
        using RecordingVersionIndexHandler handler =
            new(responses);
        using HttpClient httpClient = new(handler);
        IReleaseVersionAvailabilityValidator validator =
            new ReleaseVersionAvailabilityValidator(httpClient);

        await validator.ValidateAsync(
            "2099.103.0",
            SdkIndexUri,
            CliIndexUri,
            CancellationToken.None);

        handler.RequestedUris.ToArray().ShouldBe(
            [SdkIndexUri, CliIndexUri]);
    }

    [Fact]
    public async Task ValidateAsync_IgnoresNonCanonicalPublishedVersions()
    {
        Dictionary<string, string[]> responses =
            new(StringComparer.Ordinal)
            {
                [SdkIndexUri] =
                [
                    "preview-value",
                    "02099.500.0",
                    "2099.500.00",
                ],
                [CliIndexUri] =
                [
                    "2099.500",
                    "2099.101.000",
                    "2099.101.1-preview",
                ],
            };
        using RecordingVersionIndexHandler handler =
            new(responses);
        using HttpClient httpClient = new(handler);
        IReleaseVersionAvailabilityValidator validator =
            new ReleaseVersionAvailabilityValidator(httpClient);

        await validator.ValidateAsync(
            "2099.101.1",
            SdkIndexUri,
            CliIndexUri,
            CancellationToken.None);

        handler.RequestedUris.ToArray().ShouldBe(
            [SdkIndexUri, CliIndexUri]);
    }

    private sealed class RecordingVersionIndexHandler
        : HttpMessageHandler
    {
        private readonly Dictionary<string, string[]> _responses;

        public RecordingVersionIndexHandler(
            Dictionary<string, string[]> responses)
        {
            _responses =
                responses ??
                throw new ArgumentNullException(nameof(responses));
        }

        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string requestUri =
                request.RequestUri?.AbsoluteUri ??
                throw new InvalidOperationException(
                    "Request URI is required.");
            RequestedUris.Add(requestUri);

            if (!_responses.TryGetValue(
                    requestUri,
                    out string[]? versions))
            {
                throw new InvalidOperationException(
                    $"No response configured for '{requestUri}'.");
            }

            string json = JsonSerializer.Serialize(
                new Dictionary<string, string[]>
                {
                    ["versions"] = versions,
                });
            HttpResponseMessage response =
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),
                };
            return Task.FromResult(response);
        }
    }
}
