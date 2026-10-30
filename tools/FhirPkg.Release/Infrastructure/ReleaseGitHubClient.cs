// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Infrastructure;

internal interface IReleaseGitHubClient
{
    Task<GitHubRelease?> GetPublishedReleaseAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken);

    Task<GitHubWorkflowRun> GetWorkflowRunAsync(
        string repository,
        long runId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubWorkflowJob>> ListWorkflowRunJobsAsync(
        string repository,
        long runId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubArtifact>> ListWorkflowRunArtifactsAsync(
        string repository,
        long runId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubArtifact>> ListArtifactsByNameAsync(
        string repository,
        string name,
        CancellationToken cancellationToken);

    Task<string> DownloadArtifactZipAsync(
        string repository,
        long artifactId,
        string destinationFile,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseGitHubClient : IReleaseGitHubClient
{
    internal const string DefaultApiBaseUri = "https://api.github.com/";

    private const string AcceptHeaderValue = "application/vnd.github+json";
    private const string ApiVersionHeaderName = "X-GitHub-Api-Version";
    private const string ApiVersionHeaderValue = "2022-11-28";
    private const int DownloadBufferSize = 81920;
    private const string UserAgentHeaderValue = "FhirPkg.Release";

    private static readonly Regex LinkRegex = new(
        "<(?<uri>[^>]*)>\\s*;\\s*rel=\"(?<rel>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepositoryRegex = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Uri _apiBaseUri;
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _tokenProvider;

    internal ReleaseGitHubClient(
        HttpMessageHandler handler,
        Uri apiBaseUri,
        Func<string?> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (!apiBaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The GitHub API base URI must be absolute.",
                nameof(apiBaseUri));
        }

        _tokenProvider =
            tokenProvider ??
            throw new ArgumentNullException(nameof(tokenProvider));
        _apiBaseUri = apiBaseUri.AbsoluteUri.EndsWith('/')
            ? apiBaseUri
            : new Uri($"{apiBaseUri.AbsoluteUri}/");
        _httpClient = new HttpClient(handler, disposeHandler: false);
    }

    internal static ReleaseGitHubClient CreateFromEnvironment() =>
        new(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
            },
            ResolveApiBaseUri(
                Environment.GetEnvironmentVariable("GITHUB_API_URL")),
            static () => Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

    internal static Uri ResolveApiBaseUri(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return new Uri(DefaultApiBaseUri);
        }

        if (!Uri.TryCreate(
                configuredValue,
                UriKind.Absolute,
                out Uri? apiBaseUri) ||
            !IsHttpUri(apiBaseUri))
        {
            throw new ReleaseValidationException(
                $"GITHUB_API_URL '{configuredValue}' must be an absolute http or https URI.");
        }

        return apiBaseUri;
    }

    public async Task<GitHubRelease?> GetPublishedReleaseAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        Uri requestUri = CreateApiUri(
            $"repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        using HttpResponseMessage response = await SendApiRequestAsync(
                "GetPublishedRelease",
                requestUri,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccessStatusCode(response, requestUri);
        using JsonDocument document = await ReadJsonAsync(
                response,
                requestUri,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseRelease(
            document.RootElement,
            DescribeRequest(requestUri));
    }

    public async Task<GitHubWorkflowRun> GetWorkflowRunAsync(
        string repository,
        long runId,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(runId, 1);

        Uri requestUri = CreateApiUri(
            $"repos/{repository}/actions/runs/{FormatId(runId)}");
        using HttpResponseMessage response = await SendApiRequestAsync(
                "GetWorkflowRun",
                requestUri,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessStatusCode(response, requestUri);
        using JsonDocument document = await ReadJsonAsync(
                response,
                requestUri,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseWorkflowRun(
            document.RootElement,
            DescribeRequest(requestUri));
    }

    public async Task<IReadOnlyList<GitHubWorkflowJob>>
        ListWorkflowRunJobsAsync(
            string repository,
            long runId,
            CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(runId, 1);

        return await ListAsync(
                "ListWorkflowRunJobs",
                $"repos/{repository}/actions/runs/{FormatId(runId)}/jobs?filter=all&per_page=100",
                "jobs",
                ParseWorkflowJob,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubArtifact>>
        ListWorkflowRunArtifactsAsync(
            string repository,
            long runId,
            CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(runId, 1);

        return await ListAsync(
                "ListWorkflowRunArtifacts",
                $"repos/{repository}/actions/runs/{FormatId(runId)}/artifacts?per_page=100",
                "artifacts",
                ParseArtifact,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubArtifact>> ListArtifactsByNameAsync(
        string repository,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return await ListAsync(
                "ListArtifactsByName",
                $"repos/{repository}/actions/artifacts?name={Uri.EscapeDataString(name)}&per_page=100",
                "artifacts",
                ParseArtifact,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> DownloadArtifactZipAsync(
        string repository,
        long artifactId,
        string destinationFile,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(artifactId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);

        Uri archiveUri = await GetArtifactArchiveUriAsync(
                repository,
                artifactId,
                cancellationToken)
            .ConfigureAwait(false);

        // The archive host receives no Authorization header: the redirect
        // target is pre-signed, and the token must never leave the API host.
        string description =
            $"Artifact {FormatId(artifactId)} download from {archiveUri.GetLeftPart(UriPartial.Path)}";
        using HttpRequestMessage request = new(HttpMethod.Get, archiveUri);
        request.Headers.UserAgent.ParseAdd(UserAgentHeaderValue);
        using HttpResponseMessage response = await SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                description,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseValidationException(
                $"{description} returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return await WriteContentAsync(
                    response.Content,
                    destinationFile,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is IOException or HttpRequestException)
        {
            TryDeleteFile(destinationFile);
            throw new ReleaseValidationException(
                $"{description} failed: {ex.Message}",
                ex);
        }
        catch
        {
            TryDeleteFile(destinationFile);
            throw;
        }
    }

    private async Task<Uri> GetArtifactArchiveUriAsync(
        string repository,
        long artifactId,
        CancellationToken cancellationToken)
    {
        Uri requestUri = CreateApiUri(
            $"repos/{repository}/actions/artifacts/{FormatId(artifactId)}/zip");
        using HttpResponseMessage response = await SendApiRequestAsync(
                "DownloadArtifactZip",
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Found)
        {
            throw new ReleaseValidationException(
                $"{DescribeRequest(requestUri)} returned HTTP {(int)response.StatusCode} instead of a 302 redirect to the artifact archive.");
        }

        Uri? location = response.Headers.Location;
        if (location is null ||
            !Uri.TryCreate(requestUri, location, out Uri? archiveUri) ||
            !IsHttpUri(archiveUri))
        {
            throw new ReleaseValidationException(
                $"{DescribeRequest(requestUri)} returned a 302 redirect without a usable Location header.");
        }

        return archiveUri;
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string operation,
        string relativeUri,
        string itemsPropertyName,
        Func<JsonElement, string, T> parseItem,
        CancellationToken cancellationToken)
    {
        List<T> items = [];
        HashSet<string> requestedUris = new(StringComparer.Ordinal);
        Uri? requestUri = CreateApiUri(relativeUri);
        while (requestUri is not null)
        {
            string description = DescribeRequest(requestUri);
            if (!requestedUris.Add(requestUri.AbsoluteUri))
            {
                throw new ReleaseValidationException(
                    $"{description} was requested twice while following pagination links.");
            }

            using HttpResponseMessage response = await SendApiRequestAsync(
                    operation,
                    requestUri,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSuccessStatusCode(response, requestUri);
            using JsonDocument document = await ReadJsonAsync(
                    response,
                    requestUri,
                    cancellationToken)
                .ConfigureAwait(false);
            JsonElement itemsElement = GetProperty(
                document.RootElement,
                itemsPropertyName,
                description);
            if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw CreateMalformedValueException(
                    description,
                    itemsPropertyName);
            }

            foreach (JsonElement itemElement in itemsElement.EnumerateArray())
            {
                items.Add(parseItem(itemElement, description));
            }

            requestUri = GetNextPageUri(response, requestUri);
        }

        return items;
    }

    private Uri? GetNextPageUri(
        HttpResponseMessage response,
        Uri requestUri)
    {
        if (!response.Headers.TryGetValues(
                "Link",
                out IEnumerable<string>? linkValues))
        {
            return null;
        }

        foreach (string linkValue in linkValues)
        {
            foreach (Match match in LinkRegex.Matches(linkValue))
            {
                string[] relations = match.Groups["rel"].Value.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (!relations.Contains(
                        "next",
                        StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The token is sent with every page, so a next-page link
                // must stay on the API host.
                if (!Uri.TryCreate(
                        requestUri,
                        match.Groups["uri"].Value,
                        out Uri? nextUri) ||
                    Uri.Compare(
                        nextUri,
                        _apiBaseUri,
                        UriComponents.SchemeAndServer,
                        UriFormat.UriEscaped,
                        StringComparison.OrdinalIgnoreCase) != 0)
                {
                    throw new ReleaseValidationException(
                        $"{DescribeRequest(requestUri)} returned a next-page link outside {_apiBaseUri.GetLeftPart(UriPartial.Authority)}.");
                }

                return nextUri;
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendApiRequestAsync(
        string operation,
        Uri requestUri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        string? token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ReleaseValidationException(
                $"GITHUB_TOKEN is required for {operation}.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd(AcceptHeaderValue);
        request.Headers.Add(ApiVersionHeaderName, ApiVersionHeaderValue);
        request.Headers.UserAgent.ParseAdd(UserAgentHeaderValue);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(
                request,
                completionOption,
                DescribeRequest(requestUri),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                    request,
                    completionOption,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseValidationException(
                $"{description} failed: {ex.Message}",
                ex);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseValidationException(
                $"{description} timed out.",
                ex);
        }
    }

    private Uri CreateApiUri(string relativeUri) =>
        new(_apiBaseUri, relativeUri);

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream contentStream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(
                    contentStream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ReleaseValidationException(
                $"{DescribeRequest(requestUri)} returned malformed JSON.",
                ex);
        }
    }

    private static async Task<string> WriteContentAsync(
        HttpContent content,
        string destinationFile,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream contentStream =
            await content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
        await using FileStream fileStream = new(
            destinationFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);
        byte[] buffer = new byte[DownloadBufferSize];
        while (true)
        {
            int bytesRead = await contentStream.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, bytesRead);
            await fileStream.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return "sha256:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureSuccessStatusCode(
        HttpResponseMessage response,
        Uri requestUri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new ReleaseValidationException(
            $"{DescribeRequest(requestUri)} returned HTTP {(int)response.StatusCode}.");
    }

    private static void ValidateRepository(string repository)
    {
        if (repository is null ||
            !RepositoryRegex.IsMatch(repository) ||
            repository.Split('/').Any(
                static segment => segment is "." or ".."))
        {
            throw new ReleaseValidationException(
                $"Repository '{repository}' must be in 'owner/name' form.");
        }
    }

    private static bool IsHttpUri(Uri uri) =>
        string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.Ordinal) ||
        string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);

    private static string DescribeRequest(Uri requestUri) =>
        $"GitHub API GET {requestUri.PathAndQuery}";

    private static string FormatId(long id) =>
        id.ToString(CultureInfo.InvariantCulture);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static GitHubRelease ParseRelease(
        JsonElement element,
        string source) =>
        new(
            GetInt64(element, "id", source),
            GetString(element, "tag_name", source),
            GetBoolean(element, "draft", source),
            GetString(element, "html_url", source));

    private static GitHubWorkflowRun ParseWorkflowRun(
        JsonElement element,
        string source)
    {
        JsonElement repositoryElement = GetProperty(
            element,
            "repository",
            source);
        JsonElement? headRepositoryElement = GetOptionalObject(
            element,
            "head_repository",
            source);
        return new GitHubWorkflowRun(
            GetInt64(element, "id", source),
            GetString(element, "path", source),
            GetString(element, "event", source),
            GetString(element, "head_sha", source),
            GetOptionalString(element, "status", source),
            GetOptionalString(element, "conclusion", source),
            GetInt32(element, "run_attempt", source),
            GetString(repositoryElement, "full_name", source),
            headRepositoryElement is JsonElement headRepository
                ? GetString(headRepository, "full_name", source)
                : null,
            GetString(element, "html_url", source),
            GetDateTimeOffset(element, "created_at", source));
    }

    private static GitHubWorkflowJob ParseWorkflowJob(
        JsonElement element,
        string source) =>
        new(
            GetInt64(element, "id", source),
            GetString(element, "name", source),
            GetInt32(element, "run_attempt", source),
            GetString(element, "status", source),
            GetOptionalString(element, "conclusion", source),
            GetOptionalString(element, "html_url", source));

    private static GitHubArtifact ParseArtifact(
        JsonElement element,
        string source)
    {
        JsonElement? workflowRunElement = GetOptionalObject(
            element,
            "workflow_run",
            source);
        return new GitHubArtifact(
            GetInt64(element, "id", source),
            GetString(element, "name", source),
            GetBoolean(element, "expired", source),
            GetOptionalString(element, "digest", source),
            GetInt64(element, "size_in_bytes", source),
            GetOptionalDateTimeOffset(element, "expires_at", source),
            workflowRunElement is JsonElement workflowRun
                ? GetOptionalInt64(workflowRun, "id", source)
                : null);
    }

    private static JsonElement GetProperty(
        JsonElement element,
        string propertyName,
        string source)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return value;
    }

    private static bool TryGetNonNullProperty(
        JsonElement element,
        string propertyName,
        string source,
        out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        if (!element.TryGetProperty(propertyName, out value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return true;
    }

    private static JsonElement? GetOptionalObject(
        JsonElement element,
        string propertyName,
        string source)
    {
        if (!TryGetNonNullProperty(
                element,
                propertyName,
                source,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return value;
    }

    private static string GetString(
        JsonElement element,
        string propertyName,
        string source) =>
        GetOptionalString(element, propertyName, source) ??
        throw CreateMalformedValueException(source, propertyName);

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName,
        string source)
    {
        if (!TryGetNonNullProperty(
                element,
                propertyName,
                source,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return value.GetString();
    }

    private static bool GetBoolean(
        JsonElement element,
        string propertyName,
        string source)
    {
        JsonElement value = GetProperty(element, propertyName, source);
        if (value.ValueKind != JsonValueKind.True &&
            value.ValueKind != JsonValueKind.False)
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return value.GetBoolean();
    }

    private static int GetInt32(
        JsonElement element,
        string propertyName,
        string source)
    {
        JsonElement value = GetProperty(element, propertyName, source);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return result;
    }

    private static long GetInt64(
        JsonElement element,
        string propertyName,
        string source) =>
        GetOptionalInt64(element, propertyName, source) ??
        throw CreateMalformedValueException(source, propertyName);

    private static long? GetOptionalInt64(
        JsonElement element,
        string propertyName,
        string source)
    {
        if (!TryGetNonNullProperty(
                element,
                propertyName,
                source,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return result;
    }

    private static DateTimeOffset GetDateTimeOffset(
        JsonElement element,
        string propertyName,
        string source) =>
        GetOptionalDateTimeOffset(element, propertyName, source) ??
        throw CreateMalformedValueException(source, propertyName);

    private static DateTimeOffset? GetOptionalDateTimeOffset(
        JsonElement element,
        string propertyName,
        string source)
    {
        if (!TryGetNonNullProperty(
                element,
                propertyName,
                source,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTimeOffset(out DateTimeOffset result))
        {
            throw CreateMalformedValueException(source, propertyName);
        }

        return result;
    }

    private static ReleaseValidationException CreateMalformedValueException(
        string source,
        string propertyName) =>
        new($"{source} returned a missing or malformed '{propertyName}' value.");
}

internal sealed record GitHubRelease(
    long Id,
    string TagName,
    bool Draft,
    string HtmlUrl);

internal sealed record GitHubWorkflowRun(
    long Id,
    string Path,
    string Event,
    string HeadSha,
    string? Status,
    string? Conclusion,
    int RunAttempt,
    string RepositoryFullName,
    string? HeadRepositoryFullName,
    string HtmlUrl,
    DateTimeOffset CreatedAt);

internal sealed record GitHubWorkflowJob(
    long Id,
    string Name,
    int RunAttempt,
    string Status,
    string? Conclusion,
    string? HtmlUrl);

internal sealed record GitHubArtifact(
    long Id,
    string Name,
    bool Expired,
    string? Digest,
    long SizeInBytes,
    DateTimeOffset? ExpiresAt,
    long? WorkflowRunId);
