// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class LocateFixtureBundleCommand
{
    internal const string DefaultCorpusPath =
        "test/FhirPkg.Qualification/qualification-corpus.json";
    internal const string FailureTitle =
        "Frozen fixture bundle unavailable";

    private const string MiddleDot = "\u00b7";

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

        Option<string> commitOption = new("--commit")
        {
            Description =
                "Release commit whose qualification corpus selects the bundle.",
            Required = true,
        };

        Option<string> tagOption = new("--tag")
        {
            Description = "Release tag named in the no-bundle remediation.",
            Required = true,
        };

        Option<string> corpusPathOption = new("--corpus-path")
        {
            Description = "Repository path of the qualification corpus.",
            DefaultValueFactory = _ => DefaultCorpusPath,
        };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Option<FileInfo?> stepSummaryOption = new("--step-summary")
        {
            Description =
                "Path to the GitHub Actions step summary file. Defaults to GITHUB_STEP_SUMMARY.",
        };

        Command command = new(
            "locate-fixture-bundle",
            "Locate the trusted frozen fixture bundle for a release commit.")
        {
            repositoryOption,
            commitOption,
            tagOption,
            corpusPathOption,
            githubOutputOption,
            stepSummaryOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await ReleaseCommandSupport.ExecuteAsync(
                    services.StandardError,
                    async ct =>
                    {
                        string? repository =
                            parseResult.GetValue(repositoryOption);
                        string commit = parseResult.GetValue(commitOption)!;
                        string tag = parseResult.GetValue(tagOption)!;
                        string corpusPath =
                            parseResult.GetValue(corpusPathOption)!;
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);
                        FileInfo? stepSummary =
                            parseResult.GetValue(stepSummaryOption);

                        try
                        {
                            LocatedFixtureBundle bundle =
                                await services.FixtureBundleLocator
                                    .LocateAsync(
                                        RequireRepository(repository),
                                        commit,
                                        tag,
                                        corpusPath,
                                        DateTimeOffset.UtcNow,
                                        ct)
                                    .ConfigureAwait(false);

                            await services.GitHubOutputWriter.WriteAsync(
                                    githubOutput?.FullName,
                                    [
                                        KeyValuePair.Create(
                                            "bundle_artifact_id",
                                            bundle.ArtifactId.ToString(
                                                CultureInfo.InvariantCulture)),
                                        KeyValuePair.Create(
                                            "bundle_artifact_name",
                                            bundle.ArtifactName),
                                        KeyValuePair.Create(
                                            "bundle_artifact_digest",
                                            bundle.Digest),
                                        KeyValuePair.Create(
                                            "bundle_run_id",
                                            bundle.RunId.ToString(
                                                CultureInfo.InvariantCulture)),
                                        KeyValuePair.Create(
                                            "corpus_blob",
                                            bundle.CorpusBlob),
                                    ],
                                    ct)
                                .ConfigureAwait(false);
                            await services.StepSummaryWriter.AppendAsync(
                                    stepSummary?.FullName,
                                    FormatSummary(bundle),
                                    ct)
                                .ConfigureAwait(false);
                            await services.StandardOutput.WriteLineAsync(
                                    $"Located frozen fixture bundle {bundle.ArtifactName} (artifact {bundle.ArtifactId}, {bundle.Digest}) from run {bundle.RunId}.")
                                .ConfigureAwait(false);
                            return 0;
                        }
                        catch (ReleaseValidationException ex)
                        {
                            await services.StepSummaryWriter.AppendAsync(
                                    stepSummary?.FullName,
                                    $"### {FailureTitle}\n\n{GitHubMarkdown.Escape(ex.Message)}\n",
                                    ct)
                                .ConfigureAwait(false);
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

    private static string FormatSummary(LocatedFixtureBundle bundle)
    {
        string expiresAt = bundle.ExpiresAt.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture);
        return $"Frozen fixture bundle `{bundle.ArtifactName}` {MiddleDot} digest `{bundle.Digest}` {MiddleDot} produced by [fixture-drift run {bundle.RunId}]({bundle.RunUrl}) {MiddleDot} expires {expiresAt}\n";
    }

    private static string RequireRepository(string? repository) =>
        string.IsNullOrWhiteSpace(repository)
            ? throw new ReleaseValidationException(
                "--repository is required when GITHUB_REPOSITORY is not set.")
            : repository;
}
