// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class ValidatePublishedPackageCommand
{
    public static Command Build(ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Option<string> packageIdOption = new("--package-id")
        {
            Description = "Package identifier to validate.",
            Required = true,
        };
        packageIdOption.Validators.Add(result =>
        {
            string? value = result.Tokens.LastOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!string.Equals(
                    value,
                    ReleasePackageValidationCommon.SdkPackageId,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    value,
                    ReleasePackageValidationCommon.CliPackageId,
                    StringComparison.Ordinal))
            {
                result.AddError(
                    "--package-id must be 'fhir-pkg-lib' or 'fhir-pkg-cli'.");
            }
        });

        Option<FileInfo> candidatePackagePathOption =
            new("--candidate-package-path")
            {
                Description = "Candidate package path.",
                Required = true,
            };

        Option<string> publishedPackageUriOption =
            new("--published-package-uri")
            {
                Description = "Published package URI.",
                Required = true,
            };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            publishedPackageUriOption,
            "--published-package-uri");

        Option<string> versionOption = new("--version")
        {
            Description = "Package version to validate.",
            Required = true,
        };

        Option<string> repositoryCommitOption =
            new("--repository-commit")
            {
                Description = "Repository commit recorded in the package.",
                Required = true,
            };

        Option<int> attemptsOption = new("--attempts")
        {
            Description = "Maximum download attempts.",
            DefaultValueFactory = _ => 45,
        };
        ReleaseCommandSupport.AddMinimumValidator(
            attemptsOption,
            1,
            "Attempts must be at least one.");

        Option<int> delaySecondsOption = new("--delay-seconds")
        {
            Description = "Delay between download attempts in seconds.",
            DefaultValueFactory = _ => 20,
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
            "validate-published-package",
            "Validate a published primary package against the release candidate.")
        {
            packageIdOption,
            candidatePackagePathOption,
            publishedPackageUriOption,
            versionOption,
            repositoryCommitOption,
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
                        string packageId =
                            parseResult.GetValue(packageIdOption)!;
                        FileInfo candidatePackagePath =
                            parseResult.GetValue(
                                candidatePackagePathOption)!;
                        string publishedPackageUri =
                            parseResult.GetValue(
                                publishedPackageUriOption)!;
                        string version = parseResult.GetValue(versionOption)!;
                        string repositoryCommit =
                            parseResult.GetValue(repositoryCommitOption)!;
                        int attempts = parseResult.GetValue(attemptsOption);
                        int delaySeconds =
                            parseResult.GetValue(delaySecondsOption);
                        bool skipSignatureVerification =
                            parseResult.GetValue(
                                skipSignatureVerificationOption);
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);

                        PublishedPackageValidationResult result =
                            await services.PublishedPackageValidator
                                .ValidateAsync(
                                    packageId,
                                    candidatePackagePath.FullName,
                                    publishedPackageUri,
                                    version,
                                    repositoryCommit,
                                    attempts,
                                    delaySeconds,
                                    skipSignatureVerification,
                                    ct)
                                .ConfigureAwait(false);

                        await services.GitHubOutputWriter.WriteAsync(
                                githubOutput?.FullName,
                                [
                                    KeyValuePair.Create(
                                        "published_sha256",
                                        result.PublishedSha256),
                                ],
                                ct)
                            .ConfigureAwait(false);
                        await services.StandardOutput.WriteLineAsync(
                                $"Verified published {packageId} {version} ({result.PublishedSha256}).")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }
}
