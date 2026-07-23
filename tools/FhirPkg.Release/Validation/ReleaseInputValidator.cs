// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.RegularExpressions;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Validation;

internal interface IReleaseInputValidator
{
    Task<ReleaseInputValidationResult> ValidateAsync(
        string version,
        string tag,
        string? githubRef,
        string sdkIndexUri,
        string cliIndexUri,
        bool allowPublishedVersion,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseInputValidator : IReleaseInputValidator
{
    private static readonly Regex CommitRegex = new(
        "^[0-9a-f]{40}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReleaseGitClient _gitClient;
    private readonly IReleaseVersionAvailabilityValidator
        _versionAvailabilityValidator;

    internal ReleaseInputValidator(
        IReleaseGitClient gitClient,
        IReleaseVersionAvailabilityValidator
            versionAvailabilityValidator)
    {
        _gitClient =
            gitClient ??
            throw new ArgumentNullException(nameof(gitClient));
        _versionAvailabilityValidator =
            versionAvailabilityValidator ??
            throw new ArgumentNullException(
                nameof(versionAvailabilityValidator));
    }

    public async Task<ReleaseInputValidationResult> ValidateAsync(
        string version,
        string tag,
        string? githubRef,
        string sdkIndexUri,
        string cliIndexUri,
        bool allowPublishedVersion,
        CancellationToken cancellationToken)
    {
        Version parsedVersion =
            ReleaseVersionValidationCommon
                .ParseCanonicalThreeComponentVersion(version);
        ValidateAssemblyVersionBounds(version, parsedVersion);

        string expectedTag = $"v{version}";
        if (!string.Equals(
                tag,
                expectedTag,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"Tag '{tag}' must exactly match '{expectedTag}'.");
        }

        string expectedGitHubRef = $"refs/tags/{tag}";
        if (!string.IsNullOrWhiteSpace(githubRef) &&
            !string.Equals(
                githubRef,
                expectedGitHubRef,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"The workflow must run from '{expectedGitHubRef}', not '{githubRef}'.");
        }

        ReleaseGitCommandResult headCommitResult =
            await _gitClient.ResolveHeadCommitAsync(
                cancellationToken).ConfigureAwait(false);
        string headCommit = headCommitResult.Output;
        if (headCommitResult.ExitCode != 0 ||
            !CommitRegex.IsMatch(headCommit))
        {
            throw new ReleaseValidationException(
                "Unable to resolve the checked-out commit.");
        }

        ReleaseGitCommandResult fetchTagResult =
            await _gitClient.FetchReleaseTagAsync(
                tag,
                cancellationToken).ConfigureAwait(false);
        if (fetchTagResult.ExitCode != 0)
        {
            throw new ReleaseValidationException(
                $"Unable to fetch release tag '{tag}' from origin.");
        }

        ReleaseGitCommandResult tagCommitResult =
            await _gitClient.ResolveTagCommitAsync(
                tag,
                cancellationToken).ConfigureAwait(false);
        string tagCommit = tagCommitResult.Output;
        if (tagCommitResult.ExitCode != 0 ||
            !CommitRegex.IsMatch(tagCommit))
        {
            throw new ReleaseValidationException(
                $"Unable to resolve tag '{tag}'.");
        }

        if (!string.Equals(
                tagCommit,
                headCommit,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"Tag '{tag}' points to '{tagCommit}', not checked-out commit '{headCommit}'.");
        }

        ReleaseGitCommandResult fetchOriginMainResult =
            await _gitClient.FetchOriginMainAsync(
                cancellationToken).ConfigureAwait(false);
        if (fetchOriginMainResult.ExitCode != 0)
        {
            throw new ReleaseValidationException(
                "Unable to fetch 'origin/main'.");
        }

        ReleaseGitCommandResult mainCommitResult =
            await _gitClient.ResolveOriginMainCommitAsync(
                cancellationToken).ConfigureAwait(false);
        string mainCommit = mainCommitResult.Output;
        if (mainCommitResult.ExitCode != 0 ||
            !CommitRegex.IsMatch(mainCommit))
        {
            throw new ReleaseValidationException(
                "Unable to resolve 'origin/main'.");
        }

        ReleaseGitCommandResult ancestryResult =
            await _gitClient.CheckAncestorAsync(
                headCommit,
                "origin/main",
                cancellationToken).ConfigureAwait(false);
        if (ancestryResult.ExitCode == 1)
        {
            throw new ReleaseValidationException(
                $"Release commit '{headCommit}' is not an ancestor of origin/main '{mainCommit}'.");
        }

        if (ancestryResult.ExitCode != 0)
        {
            throw new ReleaseValidationException(
                "Unable to verify release ancestry against 'origin/main'.");
        }

        if (!allowPublishedVersion)
        {
            await _versionAvailabilityValidator.ValidateAsync(
                version,
                sdkIndexUri,
                cliIndexUri,
                cancellationToken).ConfigureAwait(false);
        }

        return new ReleaseInputValidationResult(
            headCommit,
            mainCommit);
    }

    private static void ValidateAssemblyVersionBounds(
        string version,
        Version parsedVersion)
    {
        int[] components =
        [
            parsedVersion.Major,
            parsedVersion.Minor,
            parsedVersion.Build,
        ];

        foreach (int component in components)
        {
            if (component > 65534)
            {
                throw new ReleaseValidationException(
                    $"Version '{version}' cannot be represented as an assembly version.");
            }
        }
    }
}
