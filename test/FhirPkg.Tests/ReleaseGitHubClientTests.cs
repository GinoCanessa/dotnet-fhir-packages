// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseGitHubClientTests
{
    private const string ApiBaseUri = "https://api.github.test/";
    private const string ArtifactName = "fixture-bundle-v1-abc";
    private const string Digest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HeadSha =
        "fedcba9876543210fedcba9876543210fedcba98";
    private const string Repository = "octo-org/dotnet-fhir-packages";
    private const string RepositoryUri =
        "https://api.github.test/repos/octo-org/dotnet-fhir-packages";
    private const string Token = "fake-token-for-tests";

    [Fact]
    public async Task ListArtifactsByNameAsync_FollowsLinkPaginationWithGitHubHeaders()
    {
        const string nextPageUri =
            "https://api.github.test/repositories/42/actions/artifacts?name=fixture-bundle-v1-abc&per_page=100&page=2";
        const string firstPageUri =
            "https://api.github.test/repositories/42/actions/artifacts?name=fixture-bundle-v1-abc&per_page=100&page=1";
        HttpResponseMessage firstPage = CreateJsonResponse(
            CreateArtifactList(CreateArtifactJson(11)));
        firstPage.Headers.TryAddWithoutValidation(
            "Link",
            $"<{nextPageUri}>; rel=\"next\", <{nextPageUri}>; rel=\"last\"");
        HttpResponseMessage secondPage = CreateJsonResponse(
            CreateArtifactList(CreateArtifactJson(12)));
        secondPage.Headers.TryAddWithoutValidation(
            "Link",
            $"<{firstPageUri}>; rel=\"prev\", <{firstPageUri}>; rel=\"first\"");
        RecordingHttpMessageHandler handler = new(firstPage, secondPage);

        IReadOnlyList<GitHubArtifact> artifacts =
            await CreateClient(handler).ListArtifactsByNameAsync(
                Repository,
                ArtifactName,
                TestContext.Current.CancellationToken);

        artifacts.Select(static artifact => artifact.Id).ToArray()
            .ShouldBe([11L, 12L]);
        handler.Requests.Select(static request => request.Uri).ToArray()
            .ShouldBe(
            [
                $"{RepositoryUri}/actions/artifacts?name={ArtifactName}&per_page=100",
                nextPageUri,
            ]);
        foreach (RecordedRequest request in handler.Requests)
        {
            request.Method.ShouldBe("GET");
            request.Accept.ShouldBe("application/vnd.github+json");
            request.ApiVersion.ShouldBe("2022-11-28");
            request.UserAgent.ShouldBe("FhirPkg.Release");
            request.Authorization.ShouldBe($"Bearer {Token}");
        }
    }

    [Fact]
    public async Task ListArtifactsByNameAsync_RefusesANextPageLinkOnAnotherHost()
    {
        HttpResponseMessage firstPage = CreateJsonResponse(
            CreateArtifactList(CreateArtifactJson(11)));
        firstPage.Headers.TryAddWithoutValidation(
            "Link",
            "<https://elsewhere.test/actions/artifacts?page=2>; rel=\"next\"");
        RecordingHttpMessageHandler handler = new(firstPage);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => CreateClient(handler).ListArtifactsByNameAsync(
                    Repository,
                    ArtifactName,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"GitHub API GET /repos/{Repository}/actions/artifacts?name={ArtifactName}&per_page=100 returned a next-page link outside https://api.github.test.");
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ListWorkflowRunJobsAsync_RequestsEveryAttemptAndMapsJobs()
    {
        RecordingHttpMessageHandler handler = new(
            CreateJsonResponse(new JsonObject
            {
                ["total_count"] = 2,
                ["jobs"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = 7001,
                        ["run_id"] = 501,
                        ["name"] = "fixture-drift",
                        ["run_attempt"] = 2,
                        ["status"] = "completed",
                        ["conclusion"] = "success",
                        ["html_url"] =
                            "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/501/job/7001",
                    },
                    new JsonObject
                    {
                        ["id"] = 7002,
                        ["run_id"] = 501,
                        ["name"] = "live-registry-interop",
                        ["run_attempt"] = 1,
                        ["status"] = "in_progress",
                        ["conclusion"] = null,
                        ["html_url"] = null,
                    }),
            }));

        IReadOnlyList<GitHubWorkflowJob> jobs =
            await CreateClient(handler).ListWorkflowRunJobsAsync(
                Repository,
                501,
                TestContext.Current.CancellationToken);

        jobs.ToArray().ShouldBe(
            [
                new GitHubWorkflowJob(
                    7001,
                    "fixture-drift",
                    2,
                    "completed",
                    "success",
                    "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/501/job/7001"),
                new GitHubWorkflowJob(
                    7002,
                    "live-registry-interop",
                    1,
                    "in_progress",
                    null,
                    null),
            ]);
        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(
            $"{RepositoryUri}/actions/runs/501/jobs?filter=all&per_page=100");
    }

    [Fact]
    public async Task ListWorkflowRunArtifactsAsync_MapsArtifactsIncludingDigest()
    {
        RecordingHttpMessageHandler handler = new(
            CreateJsonResponse(new JsonObject
            {
                ["total_count"] = 2,
                ["artifacts"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = 11,
                        ["name"] = "fhir-pkg-2099.101.1-candidate",
                        ["size_in_bytes"] = 4096,
                        ["expired"] = false,
                        ["digest"] = Digest,
                        ["expires_at"] = "2027-01-14T06:23:45Z",
                        ["workflow_run"] = new JsonObject
                        {
                            ["id"] = 501,
                            ["head_sha"] = HeadSha,
                        },
                    },
                    new JsonObject
                    {
                        ["id"] = 12,
                        ["name"] = "uploaded-before-digests",
                        ["size_in_bytes"] = 10,
                        ["expired"] = true,
                        ["digest"] = null,
                        ["expires_at"] = null,
                        ["workflow_run"] = null,
                    }),
            }));

        IReadOnlyList<GitHubArtifact> artifacts =
            await CreateClient(handler).ListWorkflowRunArtifactsAsync(
                Repository,
                501,
                TestContext.Current.CancellationToken);

        artifacts.ToArray().ShouldBe(
            [
                new GitHubArtifact(
                    11,
                    "fhir-pkg-2099.101.1-candidate",
                    false,
                    Digest,
                    4096,
                    new DateTimeOffset(2027, 1, 14, 6, 23, 45, TimeSpan.Zero),
                    501),
                new GitHubArtifact(
                    12,
                    "uploaded-before-digests",
                    true,
                    null,
                    10,
                    null,
                    null),
            ]);
        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(
            $"{RepositoryUri}/actions/runs/501/artifacts?per_page=100");
    }

    [Fact]
    public async Task GetWorkflowRunAsync_MapsRunFields()
    {
        RecordingHttpMessageHandler handler = new(
            CreateJsonResponse(new JsonObject
            {
                ["id"] = 501,
                ["name"] = "Fixture drift check",
                ["path"] = ".github/workflows/fixture-drift.yaml",
                ["event"] = "schedule",
                ["head_sha"] = HeadSha,
                ["status"] = "completed",
                ["conclusion"] = "failure",
                ["run_attempt"] = 3,
                ["html_url"] =
                    "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/501",
                ["created_at"] = "2026-10-12T06:23:45Z",
                ["repository"] = new JsonObject
                {
                    ["full_name"] = Repository,
                },
                ["head_repository"] = new JsonObject
                {
                    ["full_name"] = "fork-owner/dotnet-fhir-packages",
                },
            }));

        GitHubWorkflowRun run =
            await CreateClient(handler).GetWorkflowRunAsync(
                Repository,
                501,
                TestContext.Current.CancellationToken);

        run.ShouldBe(
            new GitHubWorkflowRun(
                501,
                ".github/workflows/fixture-drift.yaml",
                "schedule",
                HeadSha,
                "completed",
                "failure",
                3,
                Repository,
                "fork-owner/dotnet-fhir-packages",
                "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/501",
                new DateTimeOffset(2026, 10, 12, 6, 23, 45, TimeSpan.Zero)));
        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(
            $"{RepositoryUri}/actions/runs/501");
    }

    [Fact]
    public async Task GetWorkflowRunAsync_RejectsAMalformedRun()
    {
        RecordingHttpMessageHandler handler = new(
            CreateJsonResponse(new JsonObject
            {
                ["id"] = 501,
                ["path"] = ".github/workflows/fixture-drift.yaml",
                ["event"] = "schedule",
                ["status"] = "completed",
                ["conclusion"] = "success",
                ["run_attempt"] = 1,
                ["html_url"] =
                    "https://github.test/octo-org/dotnet-fhir-packages/actions/runs/501",
                ["created_at"] = "2026-10-12T06:23:45Z",
                ["repository"] = new JsonObject
                {
                    ["full_name"] = Repository,
                },
                ["head_repository"] = new JsonObject
                {
                    ["full_name"] = Repository,
                },
            }));

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => CreateClient(handler).GetWorkflowRunAsync(
                    Repository,
                    501,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"GitHub API GET /repos/{Repository}/actions/runs/501 returned a missing or malformed 'head_sha' value.");
    }

    [Fact]
    public async Task GetWorkflowRunAsync_RejectsMalformedJson()
    {
        RecordingHttpMessageHandler handler = new(
            CreateTextResponse(HttpStatusCode.OK, "not json"));

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => CreateClient(handler).GetWorkflowRunAsync(
                    Repository,
                    501,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"GitHub API GET /repos/{Repository}/actions/runs/501 returned malformed JSON.");
    }

    [Fact]
    public async Task GetPublishedReleaseAsync_MapsRelease()
    {
        RecordingHttpMessageHandler handler = new(
            CreateJsonResponse(new JsonObject
            {
                ["id"] = 9001,
                ["tag_name"] = "v2099.101.1",
                ["draft"] = false,
                ["prerelease"] = false,
                ["html_url"] =
                    "https://github.test/octo-org/dotnet-fhir-packages/releases/tag/v2099.101.1",
            }));

        GitHubRelease? release =
            await CreateClient(handler).GetPublishedReleaseAsync(
                Repository,
                "v2099.101.1",
                TestContext.Current.CancellationToken);

        release.ShouldBe(
            new GitHubRelease(
                9001,
                "v2099.101.1",
                false,
                "https://github.test/octo-org/dotnet-fhir-packages/releases/tag/v2099.101.1"));
        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(
            $"{RepositoryUri}/releases/tags/v2099.101.1");
    }

    [Fact]
    public async Task GetPublishedReleaseAsync_ReturnsNullWhenTheReleaseIsMissing()
    {
        RecordingHttpMessageHandler handler = new(
            CreateTextResponse(
                HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}"));

        GitHubRelease? release =
            await CreateClient(handler).GetPublishedReleaseAsync(
                Repository,
                "v2099.101.1",
                TestContext.Current.CancellationToken);

        release.ShouldBeNull();
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task DownloadArtifactZipAsync_FollowsOneRedirectWithoutAuthorization()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            const string archiveUri =
                "https://artifacts.storage.test/container/artifact.zip?sig=signed-query";
            byte[] archiveBytes = Encoding.UTF8.GetBytes(
                "PK artifact archive bytes");
            RecordingHttpMessageHandler handler = new(
                CreateRedirect(archiveUri),
                CreateBytesResponse(HttpStatusCode.OK, archiveBytes));
            string destinationFile = Path.Combine(
                workingDirectory,
                "artifact.zip");

            string digest =
                await CreateClient(handler).DownloadArtifactZipAsync(
                    Repository,
                    77,
                    destinationFile,
                    TestContext.Current.CancellationToken);

            digest.ShouldBe(
                "sha256:" +
                Convert.ToHexString(SHA256.HashData(archiveBytes))
                    .ToLowerInvariant());
            File.ReadAllBytes(destinationFile).ShouldBe(archiveBytes);
            handler.Requests.Count.ShouldBe(2);
            handler.Requests[0].Uri.ShouldBe(
                $"{RepositoryUri}/actions/artifacts/77/zip");
            handler.Requests[0].Authorization.ShouldBe($"Bearer {Token}");
            handler.Requests[1].Uri.ShouldBe(archiveUri);
            handler.Requests[1].Authorization.ShouldBeNull();
            handler.Requests[1].ApiVersion.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadArtifactZipAsync_RefusesASecondRedirect()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            RecordingHttpMessageHandler handler = new(
                CreateRedirect(
                    "https://artifacts.storage.test/container/artifact.zip?sig=signed-query"),
                CreateRedirect("https://elsewhere.test/artifact.zip"));
            string destinationFile = Path.Combine(
                workingDirectory,
                "artifact.zip");

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                    () => CreateClient(handler).DownloadArtifactZipAsync(
                        Repository,
                        77,
                        destinationFile,
                        TestContext.Current.CancellationToken));

            exception.Message.ShouldBe(
                "Artifact 77 download from https://artifacts.storage.test/container/artifact.zip returned HTTP 302.");
            handler.Requests.Count.ShouldBe(2);
            File.Exists(destinationFile).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadArtifactZipAsync_RequiresARedirectFromTheApi()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            RecordingHttpMessageHandler handler = new(
                CreateBytesResponse(HttpStatusCode.OK, [1, 2, 3]));
            string destinationFile = Path.Combine(
                workingDirectory,
                "artifact.zip");

            ReleaseValidationException exception =
                await Should.ThrowAsync<ReleaseValidationException>(
                    () => CreateClient(handler).DownloadArtifactZipAsync(
                        Repository,
                        77,
                        destinationFile,
                        TestContext.Current.CancellationToken));

            exception.Message.ShouldBe(
                $"GitHub API GET /repos/{Repository}/actions/artifacts/77/zip returned HTTP 200 instead of a 302 redirect to the artifact archive.");
            handler.Requests.ShouldHaveSingleItem();
            File.Exists(destinationFile).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task NonSuccessStatus_ThrowsReleaseValidationExceptionNamingTheRequest(
        int statusCode)
    {
        RecordingHttpMessageHandler handler = new(
            CreateTextResponse((HttpStatusCode)statusCode, "{}"));

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => CreateClient(handler).GetWorkflowRunAsync(
                    Repository,
                    501,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"GitHub API GET /repos/{Repository}/actions/runs/501 returned HTTP {statusCode}.");
        exception.Message.ShouldNotContain(Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingToken_ThrowsWithoutSendingARequest(string? token)
    {
        RecordingHttpMessageHandler handler = new();
        ReleaseGitHubClient client = new(
            handler,
            new Uri(ApiBaseUri),
            () => token);

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => client.GetWorkflowRunAsync(
                    Repository,
                    501,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            "GITHUB_TOKEN is required for GetWorkflowRun.");
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("octo-org")]
    [InlineData("octo-org/dotnet-fhir-packages/extra")]
    [InlineData("../dotnet-fhir-packages")]
    [InlineData("octo-org/..")]
    [InlineData("octo-org/repo?name=x")]
    public async Task InvalidRepository_IsRejectedBeforeAnyRequest(
        string repository)
    {
        RecordingHttpMessageHandler handler = new();

        ReleaseValidationException exception =
            await Should.ThrowAsync<ReleaseValidationException>(
                () => CreateClient(handler).ListArtifactsByNameAsync(
                    repository,
                    ArtifactName,
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldBe(
            $"Repository '{repository}' must be in 'owner/name' form.");
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "https://api.github.com/")]
    [InlineData("", "https://api.github.com/")]
    [InlineData(
        "https://ghe.example.test/api/v3",
        "https://ghe.example.test/api/v3")]
    public void ResolveApiBaseUri_DefaultsToPublicGitHub(
        string? configuredValue,
        string expectedUri)
    {
        ReleaseGitHubClient.ResolveApiBaseUri(configuredValue)
            .AbsoluteUri.ShouldBe(expectedUri);
    }

    [Theory]
    [InlineData("ftp://ghe.example.test/api/v3")]
    [InlineData("not a uri")]
    public void ResolveApiBaseUri_RejectsNonHttpValues(string configuredValue)
    {
        ReleaseValidationException exception =
            Should.Throw<ReleaseValidationException>(
                () => ReleaseGitHubClient.ResolveApiBaseUri(
                    configuredValue));

        exception.Message.ShouldBe(
            $"GITHUB_API_URL '{configuredValue}' must be an absolute http or https URI.");
    }

    [Fact]
    public async Task ApiBaseUri_KeepsAnEnterpriseApiPath()
    {
        RecordingHttpMessageHandler handler = new(
            CreateTextResponse(
                HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}"));
        ReleaseGitHubClient client = new(
            handler,
            new Uri("https://ghe.example.test/api/v3"),
            static () => Token);

        await client.GetPublishedReleaseAsync(
            Repository,
            "v2099.101.1",
            TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(
            $"https://ghe.example.test/api/v3/repos/{Repository}/releases/tags/v2099.101.1");
    }

    private static ReleaseGitHubClient CreateClient(
        HttpMessageHandler handler) =>
        new(
            handler,
            new Uri(ApiBaseUri),
            static () => Token);

    private static JsonObject CreateArtifactJson(long id) =>
        new()
        {
            ["id"] = id,
            ["name"] = ArtifactName,
            ["size_in_bytes"] = 2048,
            ["expired"] = false,
            ["digest"] = Digest,
            ["expires_at"] = "2027-01-14T06:23:45Z",
            ["workflow_run"] = new JsonObject
            {
                ["id"] = 500 + id,
            },
        };

    private static JsonObject CreateArtifactList(
        params JsonObject[] artifacts) =>
        new()
        {
            ["total_count"] = artifacts.Length,
            ["artifacts"] = new JsonArray(artifacts),
        };

    private static HttpResponseMessage CreateJsonResponse(JsonNode content) =>
        CreateTextResponse(HttpStatusCode.OK, content.ToJsonString());

    private static HttpResponseMessage CreateTextResponse(
        HttpStatusCode statusCode,
        string content)
    {
        HttpResponseMessage response = new(statusCode);
        response.Content = new StringContent(
            content,
            Encoding.UTF8,
            "application/json");
        return response;
    }

    private static HttpResponseMessage CreateBytesResponse(
        HttpStatusCode statusCode,
        byte[] content)
    {
        HttpResponseMessage response = new(statusCode);
        response.Content = new ByteArrayContent(content);
        return response;
    }

    private static HttpResponseMessage CreateRedirect(string location)
    {
        HttpResponseMessage response = new(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? Authorization,
        string Accept,
        string UserAgent,
        string? ApiVersion);

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        internal RecordingHttpMessageHandler(
            params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        internal List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.UserAgent.ToString(),
                request.Headers.TryGetValues(
                    "X-GitHub-Api-Version",
                    out IEnumerable<string>? apiVersions)
                    ? string.Join(",", apiVersions)
                    : null));
            _responses.Count.ShouldBeGreaterThan(
                0,
                $"Unexpected request {request.Method} {request.RequestUri}.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
