// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// Retrieves <see cref="GitHubRepositoryFacts"/> from the GitHub REST API for tier 2
/// of canonical CI build repository selection.
/// </summary>
/// <remarks>
/// <para>
/// Results — including negative results — are cached per repository for the lifetime
/// of the instance, so an unavailable GitHub is queried at most once per repository.
/// </para>
/// <para>
/// Every failure mode (non-success status, transport failure, malformed JSON, timeout)
/// yields <see langword="null"/> rather than an exception, so selection degrades to the
/// next tier instead of failing the resolution. Only cancellation raised from the
/// caller's own token propagates.
/// </para>
/// <para>
/// This type deliberately does not derive from <see cref="RegistryClientBase"/>: it is
/// not a registry, and GitHub is not a package source.
/// </para>
/// </remarks>
internal sealed class GitHubRepositoryFactsProvider : IGitHubRepositoryFactsProvider
{
    private const string UserAgent = "FhirPkg/1.0";
    private const string DefaultApiBaseUrl = "https://api.github.com/";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<CiBuildRepositoryIdentity, GitHubRepositoryFacts?> _cache = new();
    private readonly RegistryHttpTransport _transport;
    private readonly ILogger _logger;
    private readonly string? _token;
    private readonly Uri _apiBaseUri;

    /// <summary>
    /// Initialises a new <see cref="GitHubRepositoryFactsProvider"/>.
    /// </summary>
    /// <param name="transport">The HTTP transport used for GitHub requests.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="token">
    /// An optional GitHub token. When <see langword="null"/> (the default) no
    /// <c>Authorization</c> header is ever sent.
    /// </param>
    /// <param name="apiBaseUri">
    /// An optional API base URI; defaults to <c>https://api.github.com/</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a token is supplied on a transport that is not redirect-controlled,
    /// because the credential could otherwise follow a redirect to another origin.
    /// </exception>
    public GitHubRepositoryFactsProvider(
        RegistryHttpTransport transport,
        ILogger logger,
        string? token = null,
        Uri? apiBaseUri = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);

        if (token is not null && !transport.RedirectsControlled)
        {
            throw new InvalidOperationException(
                "Authenticated GitHub repository lookups require a redirect-controlled transport.");
        }

        _transport = transport;
        _logger = logger;
        _token = token;
        _apiBaseUri = apiBaseUri ?? new Uri(DefaultApiBaseUrl);
    }

    /// <inheritdoc />
    public async Task<GitHubRepositoryFacts?> TryGetFactsAsync(
        CiBuildRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(repository, out GitHubRepositoryFacts? cached))
            return cached;

        GitHubRepositoryFacts? facts = await FetchFactsAsync(repository, cancellationToken)
            .ConfigureAwait(false);

        _cache[repository] = facts;
        return facts;
    }

    private async Task<GitHubRepositoryFacts?> FetchFactsAsync(
        CiBuildRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        Uri requestUri = new(_apiBaseUri, $"repos/{repository.ToUrlPath()}");

        using CancellationTokenSource timeoutSource = CreateTimeoutSource();
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            // The credential is scoped to the configured API origin, mirroring the
            // trusted-origin scoping in RegistryClientBase.CreateRequestMessage.
            if (_token is not null && SameOrigin(_apiBaseUri, requestUri))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
            }

            using HttpResponseMessage response = await _transport.HttpClient
                .SendAsync(request, linkedSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "GitHub returned HTTP {StatusCode} for {Repository}; treating its facts as unavailable",
                    (int)response.StatusCode, repository);
                return null;
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(linkedSource.Token)
                .ConfigureAwait(false);

            GitHubRepositoryPayload? payload = await JsonSerializer
                .DeserializeAsync<GitHubRepositoryPayload>(stream, s_jsonOptions, linkedSource.Token)
                .ConfigureAwait(false);

            if (payload is null)
            {
                _logger.LogDebug("GitHub returned an empty payload for {Repository}", repository);
                return null;
            }

            return new GitHubRepositoryFacts
            {
                IsFork = payload.Fork ?? false,
                CreatedAt = payload.CreatedAt,
                ParentFullName = payload.Parent?.FullName,
                DefaultBranch = payload.DefaultBranch,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("GitHub lookup for {Repository} timed out", repository);
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(
                "GitHub lookup for {Repository} failed: {Message}", repository, exception.Message);
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(
                "GitHub returned unreadable JSON for {Repository}: {Message}", repository, exception.Message);
            return null;
        }
    }

    private CancellationTokenSource CreateTimeoutSource()
    {
        TimeSpan timeout = _transport.Timeout;

        return timeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout);
    }

    /// <summary>
    /// Compares scheme, host, and port, mirroring the trusted-origin scoping applied to
    /// registry credentials in <see cref="RegistryClientBase"/>.
    /// </summary>
    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private sealed record GitHubRepositoryPayload
    {
        [JsonPropertyName("fork")]
        public bool? Fork { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("default_branch")]
        public string? DefaultBranch { get; init; }

        [JsonPropertyName("parent")]
        public GitHubParentPayload? Parent { get; init; }
    }

    private sealed record GitHubParentPayload
    {
        [JsonPropertyName("full_name")]
        public string? FullName { get; init; }
    }
}
