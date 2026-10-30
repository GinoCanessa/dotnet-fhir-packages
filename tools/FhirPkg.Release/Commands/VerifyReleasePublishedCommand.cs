// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class VerifyReleasePublishedCommand
{
    internal const string FailureTitle = "GitHub Release not published";

    public static Command Build(ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Option<string?> repositoryOption = new("--repository")
        {
            Description =
                "GitHub repository (owner/name). Defaults to GITHUB_REPOSITORY.",
            DefaultValueFactory =
                _ => Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"),
        };

        Option<string> tagOption = new("--tag")
        {
            Description = "Release tag whose GitHub Release must be published.",
            Required = true,
        };

        Command command = new(
            "verify-release-published",
            "Verify that the GitHub Release for a tag is published and not a draft.")
        {
            repositoryOption,
            tagOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await ReleaseCommandSupport.ExecuteAsync(
                    services.StandardError,
                    async ct =>
                    {
                        string? repository =
                            parseResult.GetValue(repositoryOption);
                        string tag = parseResult.GetValue(tagOption)!;

                        try
                        {
                            GitHubRelease? release =
                                await services.GitHubClient
                                    .GetPublishedReleaseAsync(
                                        RequireRepository(repository),
                                        tag,
                                        ct)
                                    .ConfigureAwait(false);
                            if (!ReleaseRecoveryVerifier.IsPublished(release))
                            {
                                throw new ReleaseValidationException(
                                    $"GitHub Release {tag} is not published.");
                            }

                            await services.StandardOutput.WriteLineAsync(
                                    $"GitHub Release {tag} is published: {release.HtmlUrl}")
                                .ConfigureAwait(false);
                            return 0;
                        }
                        catch (ReleaseValidationException ex)
                        {
                            await services.StandardOutput.WriteLineAsync(
                                    GitHubAnnotations.Error(
                                        FailureTitle,
                                        ex.Message))
                                .ConfigureAwait(false);
                            throw;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }

    private static string RequireRepository(string? repository) =>
        string.IsNullOrWhiteSpace(repository)
            ? throw new ReleaseValidationException(
                "--repository is required when GITHUB_REPOSITORY is not set.")
            : repository;
}
