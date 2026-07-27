// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Validation;

internal interface IPublishedPackageValidator
{
    Task<PublishedPackageValidationResult> ValidateAsync(
        string packageId,
        string candidatePackagePath,
        string publishedPackageUri,
        string version,
        string repositoryCommit,
        int attempts = 45,
        int delaySeconds = 20,
        bool skipSignatureVerification = false,
        CancellationToken cancellationToken = default);
}

internal sealed class PublishedPackageValidator
    : IPublishedPackageValidator
{
    private static readonly TimeSpan s_defaultRequestTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> s_ignoredEntryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".signature.p7s",
            "_rels/.rels",
            "[Content_Types].xml",
        };

    private readonly HttpClient _httpClient;
    private readonly IReleasePackageValidator _packageValidator;
    private readonly IReleaseDelay _delay;
    private readonly IReleaseProcessRunner _processRunner;
    private readonly TimeSpan _requestTimeout;

    internal PublishedPackageValidator(
        HttpClient httpClient,
        IReleaseProcessRunner processRunner,
        IReleasePackageValidator packageValidator,
        IReleaseDelay delay,
        TimeSpan? requestTimeout = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _processRunner =
            processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
        _packageValidator =
            packageValidator ??
            throw new ArgumentNullException(nameof(packageValidator));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _requestTimeout = requestTimeout ?? s_defaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The request timeout must be greater than zero.");
        }
    }

    public async Task<PublishedPackageValidationResult> ValidateAsync(
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
        ReleasePackageValidationCommon.EnsureSupportedPackageId(packageId);

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

        string fullCandidatePath = Path.GetFullPath(candidatePackagePath);
        if (!File.Exists(fullCandidatePath))
        {
            throw new ReleaseValidationException(
                $"Candidate package '{fullCandidatePath}' does not exist.");
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-published-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string publishedPath = Path.Combine(
            temporaryDirectory,
            $"{packageId}.{version}.nupkg");

        try
        {
            bool downloaded = false;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    using CancellationTokenSource requestCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                    requestCancellation.CancelAfter(_requestTimeout);
                    byte[] content = await _httpClient.GetByteArrayAsync(
                            publishedPackageUri,
                            requestCancellation.Token)
                        .ConfigureAwait(false);
                    if (content.Length == 0)
                    {
                        throw new InvalidDataException(
                            "Published package download was empty.");
                    }

                    await File.WriteAllBytesAsync(
                            publishedPath,
                            content,
                            cancellationToken)
                        .ConfigureAwait(false);
                    downloaded = true;
                    break;
                }
                catch (HttpRequestException ex)
                    when (!cancellationToken.IsCancellationRequested &&
                          IsRetryableStatusCode(ex.StatusCode))
                {
                    DeleteIfExists(publishedPath);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    DeleteIfExists(publishedPath);
                }
                catch (IOException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    DeleteIfExists(publishedPath);
                }

                if (attempt < attempts)
                {
                    await _delay.DelayAsync(
                            TimeSpan.FromSeconds(delaySeconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!downloaded)
            {
                throw new ReleaseValidationException(
                    $"Published package was not available after {attempts} attempts.");
            }

            if (!skipSignatureVerification)
            {
                string[] arguments =
                [
                    "nuget",
                    "verify",
                    "--all",
                    "--verbosity",
                    "minimal",
                    publishedPath,
                ];
                ReleaseProcessResult processResult =
                    await _processRunner.RunAsync(
                            "dotnet",
                            arguments,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (processResult.ExitCode != 0)
                {
                    throw new ReleaseValidationException(
                        "Published package signature verification failed.");
                }
            }

            _packageValidator.Validate(
                packageId,
                publishedPath,
                version,
                repositoryCommit);

            Dictionary<string, string> candidateHashes =
                GetContentHashes(fullCandidatePath);
            Dictionary<string, string> publishedHashes =
                GetContentHashes(publishedPath);
            if (candidateHashes.Count != publishedHashes.Count)
            {
                throw new ReleaseValidationException(
                    "Published package entries do not match the release candidate.");
            }

            foreach (KeyValuePair<string, string> entry in candidateHashes)
            {
                if (!publishedHashes.TryGetValue(
                        entry.Key,
                        out string? publishedHash) ||
                    !string.Equals(
                        publishedHash,
                        entry.Value,
                        StringComparison.Ordinal))
                {
                    throw new ReleaseValidationException(
                        $"Published package entry '{entry.Key}' differs from the release candidate.");
                }
            }

            return new PublishedPackageValidationResult(
                ReleasePackageValidationCommon.ComputeSha256(
                    publishedPath));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static Dictionary<string, string> GetContentHashes(
        string packagePath)
    {
        Dictionary<string, string> hashes =
            new(StringComparer.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (s_ignoredEntryNames.Contains(entry.FullName))
            {
                continue;
            }

            using Stream stream = entry.Open();
            byte[] hash = SHA256.HashData(stream);
            hashes.Add(
                entry.FullName,
                Convert.ToHexString(hash).ToLowerInvariant());
        }

        return hashes;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null ||
        statusCode == HttpStatusCode.NotFound ||
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 425 ||
        (int)statusCode == 429 ||
        statusCode == HttpStatusCode.InternalServerError ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout;
}
