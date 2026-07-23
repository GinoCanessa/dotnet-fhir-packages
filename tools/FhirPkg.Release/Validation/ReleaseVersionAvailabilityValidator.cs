// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirPkg.Release.Validation;

internal interface IReleaseVersionAvailabilityValidator
{
    Task ValidateAsync(
        string version,
        string sdkIndexUri,
        string cliIndexUri,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseVersionAvailabilityValidator
    : IReleaseVersionAvailabilityValidator
{
    private readonly HttpClient _httpClient;

    internal ReleaseVersionAvailabilityValidator(HttpClient httpClient)
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task ValidateAsync(
        string version,
        string sdkIndexUri,
        string cliIndexUri,
        CancellationToken cancellationToken)
    {
        Version parsedVersion =
            ReleaseVersionValidationCommon
                .ParseCanonicalThreeComponentVersion(version);
        List<Version> canonicalVersions = [];
        ReleasePackageIndex[] indexes =
        [
            new ReleasePackageIndex(
                ReleasePackageValidationCommon.SdkPackageId,
                sdkIndexUri),
            new ReleasePackageIndex(
                ReleasePackageValidationCommon.CliPackageId,
                cliIndexUri),
        ];

        foreach (ReleasePackageIndex index in indexes)
        {
            IReadOnlyList<string> publishedVersions =
                await GetPublishedVersionsAsync(
                    index.Uri,
                    cancellationToken).ConfigureAwait(false);
            foreach (string publishedVersion in publishedVersions)
            {
                if (string.Equals(
                        version,
                        publishedVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReleaseValidationException(
                        $"{index.PackageId} '{version}' is already published.");
                }
            }

            foreach (string publishedVersion in publishedVersions)
            {
                bool isCanonicalVersion =
                    ReleaseVersionValidationCommon
                        .TryParseCanonicalThreeComponentVersion(
                            publishedVersion,
                            out Version? canonicalVersion);
                if (!isCanonicalVersion ||
                    canonicalVersion is null)
                {
                    continue;
                }

                canonicalVersions.Add(canonicalVersion);
            }
        }

        if (canonicalVersions.Count == 0)
        {
            return;
        }

        canonicalVersions.Sort(
            static (left, right) => right.CompareTo(left));
        Version highestVersion = canonicalVersions[0];
        if (parsedVersion <= highestVersion)
        {
            throw new ReleaseValidationException(
                $"Version '{version}' must be greater than the highest published canonical version '{highestVersion.ToString(3)}'.");
        }
    }

    private async Task<IReadOnlyList<string>> GetPublishedVersionsAsync(
        string indexUri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _httpClient.GetAsync(
                indexUri,
                cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream contentStream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
        using JsonDocument document =
            await JsonDocument.ParseAsync(
                contentStream,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        JsonElement rootElement = document.RootElement;
        if (!rootElement.TryGetProperty(
                "versions",
                out JsonElement versionsElement) ||
            versionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> versions = [];
        foreach (JsonElement versionElement in
                 versionsElement.EnumerateArray())
        {
            if (versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? publishedVersion = versionElement.GetString();
            if (publishedVersion is null)
            {
                continue;
            }

            versions.Add(publishedVersion);
        }

        return versions;
    }

    private sealed record ReleasePackageIndex(
        string PackageId,
        string Uri);
}

internal static class ReleaseVersionValidationCommon
{
    private static readonly Regex ThreeComponentVersionRegex = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static Version ParseCanonicalThreeComponentVersion(
        string version)
    {
        if (!ThreeComponentVersionRegex.IsMatch(version))
        {
            throw new ReleaseValidationException(
                $"Version '{version}' must contain exactly three numeric components.");
        }

        Version parsedVersion = Version.Parse(version);
        if (!string.Equals(
                parsedVersion.ToString(3),
                version,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"Version '{version}' is not in canonical numeric form.");
        }

        return parsedVersion;
    }

    internal static bool TryParseCanonicalThreeComponentVersion(
        string version,
        out Version? parsedVersion)
    {
        parsedVersion = null;
        if (!ThreeComponentVersionRegex.IsMatch(version) ||
            !Version.TryParse(version, out Version? candidateVersion) ||
            !string.Equals(
                candidateVersion.ToString(3),
                version,
                StringComparison.Ordinal))
        {
            return false;
        }

        parsedVersion = candidateVersion;
        return true;
    }
}
