// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class InspectPublicationCommand
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
            Description = "Release version to inspect.",
            Required = true,
        };

        Option<string> repositoryCommitOption =
            new("--repository-commit")
            {
                Description = "Repository commit recorded in the candidate.",
                Required = true,
            };

        Option<string> sdkFlatContainerUriOption =
            new("--sdk-flat-container-uri")
            {
                Description = "SDK package flat-container base URI.",
                DefaultValueFactory =
                    _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib",
            };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            sdkFlatContainerUriOption,
            "--sdk-flat-container-uri");

        Option<string> cliFlatContainerUriOption =
            new("--cli-flat-container-uri")
            {
                Description = "CLI package flat-container base URI.",
                DefaultValueFactory =
                    _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli",
            };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            cliFlatContainerUriOption,
            "--cli-flat-container-uri");

        Option<int> attemptsOption = new("--attempts")
        {
            Description = "Maximum number of visibility attempts.",
            DefaultValueFactory = _ => 5,
        };
        ReleaseCommandSupport.AddMinimumValidator(
            attemptsOption,
            1,
            "Attempts must be at least one.");

        Option<int> delaySecondsOption = new("--delay-seconds")
        {
            Description = "Delay between attempts in seconds.",
            DefaultValueFactory = _ => 5,
        };
        ReleaseCommandSupport.AddMinimumValidator(
            delaySecondsOption,
            0,
            "DelaySeconds cannot be negative.");

        Option<bool> skipSignatureVerificationOption =
            new("--skip-signature-verification")
            {
                Description = "Skip dotnet nuget signature verification.",
            };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Command command = new(
            "inspect-publication",
            "Inspect current publication state for synchronized release packages.")
        {
            candidateDirectoryOption,
            versionOption,
            repositoryCommitOption,
            sdkFlatContainerUriOption,
            cliFlatContainerUriOption,
            attemptsOption,
            delaySecondsOption,
            skipSignatureVerificationOption,
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
                        string repositoryCommit =
                            parseResult.GetValue(repositoryCommitOption)!;
                        string sdkFlatContainerUri =
                            parseResult.GetValue(
                                sdkFlatContainerUriOption)!;
                        string cliFlatContainerUri =
                            parseResult.GetValue(
                                cliFlatContainerUriOption)!;
                        int attempts = parseResult.GetValue(attemptsOption);
                        int delaySeconds =
                            parseResult.GetValue(delaySecondsOption);
                        bool skipSignatureVerification =
                            parseResult.GetValue(
                                skipSignatureVerificationOption);
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);

                        ReleasePublicationStateResult result =
                            await services.PublicationStateValidator
                                .ValidateAsync(
                                    candidateDirectory.FullName,
                                    version,
                                    repositoryCommit,
                                    sdkFlatContainerUri,
                                    cliFlatContainerUri,
                                    attempts,
                                    delaySeconds,
                                    skipSignatureVerification,
                                    ct)
                                .ConfigureAwait(false);

                        string cliState =
                            ReleaseCommandSupport.FormatPublicationState(
                                result.CliState);
                        string sdkState =
                            ReleaseCommandSupport.FormatPublicationState(
                                result.SdkState);

                        await services.GitHubOutputWriter.WriteAsync(
                                githubOutput?.FullName,
                                [
                                    KeyValuePair.Create(
                                        "cli_state",
                                        cliState),
                                    KeyValuePair.Create(
                                        "sdk_state",
                                        sdkState),
                                ],
                                ct)
                            .ConfigureAwait(false);
                        await services.StandardOutput.WriteLineAsync(
                                $"Publication state: CLI {cliState}; SDK {sdkState}.")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }
}
