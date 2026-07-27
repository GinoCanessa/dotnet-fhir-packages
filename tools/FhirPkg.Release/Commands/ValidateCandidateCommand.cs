// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class ValidateCandidateCommand
{
    public static Command Build(ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Option<DirectoryInfo> candidateDirectoryOption =
            new("--candidate-directory")
            {
                Description = "Release candidate directory.",
                Required = true,
            };

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

        Option<string> repositoryCommitOption =
            new("--repository-commit")
            {
                Description = "Repository commit recorded in the candidate.",
                Required = true,
            };

        Option<string?> expectedSdkPackageSha256Option =
            new("--expected-sdk-package-sha256")
            {
                Description = "Expected SDK package SHA-256.",
            };

        Option<string?> expectedCliPackageSha256Option =
            new("--expected-cli-package-sha256")
            {
                Description = "Expected CLI package SHA-256.",
            };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Command command = new(
            "validate-candidate",
            "Validate synchronized release candidate artifacts.")
        {
            candidateDirectoryOption,
            versionOption,
            tagOption,
            repositoryCommitOption,
            expectedSdkPackageSha256Option,
            expectedCliPackageSha256Option,
            githubOutputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await ReleaseCommandSupport.ExecuteAsync(
                    services.StandardError,
                    async ct =>
                    {
                        DirectoryInfo candidateDirectory =
                            parseResult.GetValue(candidateDirectoryOption)!;
                        string version = parseResult.GetValue(versionOption)!;
                        string tag = parseResult.GetValue(tagOption)!;
                        string repositoryCommit =
                            parseResult.GetValue(repositoryCommitOption)!;
                        string? expectedSdkPackageSha256 =
                            parseResult.GetValue(
                                expectedSdkPackageSha256Option);
                        string? expectedCliPackageSha256 =
                            parseResult.GetValue(
                                expectedCliPackageSha256Option);
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);

                        ReleaseCandidateValidationResult result =
                            services.CandidateValidator.Validate(
                                candidateDirectory.FullName,
                                version,
                                tag,
                                repositoryCommit,
                                expectedSdkPackageSha256,
                                expectedCliPackageSha256);

                        await services.GitHubOutputWriter.WriteAsync(
                                githubOutput?.FullName,
                                [
                                    KeyValuePair.Create(
                                        "sdk_package_path",
                                        result.SdkPackagePath),
                                    KeyValuePair.Create(
                                        "sdk_symbols_path",
                                        result.SdkSymbolsPath),
                                    KeyValuePair.Create(
                                        "sdk_manifest_path",
                                        result.SdkManifestPath),
                                    KeyValuePair.Create(
                                        "sdk_sha256",
                                        result.SdkSha256),
                                    KeyValuePair.Create(
                                        "cli_package_path",
                                        result.CliPackagePath),
                                    KeyValuePair.Create(
                                        "cli_symbols_path",
                                        result.CliSymbolsPath),
                                    KeyValuePair.Create(
                                        "cli_manifest_path",
                                        result.CliManifestPath),
                                    KeyValuePair.Create(
                                        "cli_sha256",
                                        result.CliSha256),
                                ],
                                ct)
                            .ConfigureAwait(false);
                        await services.StandardOutput.WriteLineAsync(
                                $"Verified synchronized release candidate {version} (SDK {result.SdkSha256}, CLI {result.CliSha256}).")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }
}
