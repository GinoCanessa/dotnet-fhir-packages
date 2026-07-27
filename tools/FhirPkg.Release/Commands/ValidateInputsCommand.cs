// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class ValidateInputsCommand
{
    public static Command Build(ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Option<string> versionOption = new("--version")
        {
            Description = "Release version to validate.",
            Required = true,
        };

        Option<string> tagOption = new("--tag")
        {
            Description = "Release tag to validate.",
            Required = true,
        };

        Option<string?> githubRefOption = new("--github-ref")
        {
            Description = "GitHub ref that triggered the workflow.",
            DefaultValueFactory =
                _ => Environment.GetEnvironmentVariable("GITHUB_REF"),
        };

        Option<string> sdkIndexUriOption = new("--sdk-index-uri")
        {
            Description = "SDK package index URI.",
            DefaultValueFactory =
                _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib/index.json",
        };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            sdkIndexUriOption,
            "--sdk-index-uri");

        Option<string> cliIndexUriOption = new("--cli-index-uri")
        {
            Description = "CLI package index URI.",
            DefaultValueFactory =
                _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli/index.json",
        };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            cliIndexUriOption,
            "--cli-index-uri");

        Option<bool> allowPublishedVersionOption =
            new("--allow-published-version")
            {
                Description =
                    "Skip the fresh-version availability check.",
            };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Command command = new(
            "validate-inputs",
            "Validate release version, tag, GitHub ref, and main ancestry.")
        {
            versionOption,
            tagOption,
            githubRefOption,
            sdkIndexUriOption,
            cliIndexUriOption,
            allowPublishedVersionOption,
            githubOutputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await ReleaseCommandSupport.ExecuteAsync(
                    services.StandardError,
                    async ct =>
                    {
                        string version = parseResult.GetValue(versionOption)!;
                        string tag = parseResult.GetValue(tagOption)!;
                        string? githubRef =
                            parseResult.GetValue(githubRefOption);
                        string sdkIndexUri =
                            parseResult.GetValue(sdkIndexUriOption)!;
                        string cliIndexUri =
                            parseResult.GetValue(cliIndexUriOption)!;
                        bool allowPublishedVersion =
                            parseResult.GetValue(
                                allowPublishedVersionOption);
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);

                        ReleaseInputValidationResult result =
                            await services.InputValidator.ValidateAsync(
                                    version,
                                    tag,
                                    githubRef,
                                    sdkIndexUri,
                                    cliIndexUri,
                                    allowPublishedVersion,
                                    ct)
                                .ConfigureAwait(false);

                        await services.GitHubOutputWriter.WriteAsync(
                                githubOutput?.FullName,
                                [
                                    KeyValuePair.Create(
                                        "commit",
                                        result.Commit),
                                    KeyValuePair.Create(
                                        "main_commit",
                                        result.MainCommit),
                                ],
                                ct)
                            .ConfigureAwait(false);
                        await services.StandardOutput.WriteLineAsync(
                                $"Validated release {version} at {result.Commit} on origin/main {result.MainCommit}.")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }
}
