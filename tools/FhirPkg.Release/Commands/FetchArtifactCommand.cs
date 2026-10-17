// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class FetchArtifactCommand
{
    internal const string FailureTitle = "Artifact fetch failed";

    private const string DigestPrefix = "sha256:";

    private static readonly Regex DigestRegex = new(
        "^sha256:[0-9a-f]{64}\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        Option<long> artifactIdOption = new("--artifact-id")
        {
            Description = "Workflow artifact id to download.",
            Required = true,
        };
        artifactIdOption.Validators.Add(result =>
        {
            string? value = result.Tokens.LastOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long parsedValue) &&
                parsedValue < 1)
            {
                result.AddError("--artifact-id must be at least one.");
            }
        });

        Option<string> expectedDigestOption = new("--expected-digest")
        {
            Description =
                "GitHub's recorded artifact digest, sha256:<64 lowercase hex characters>.",
            Required = true,
        };
        expectedDigestOption.Validators.Add(result =>
        {
            string? value = result.Tokens.LastOrDefault()?.Value;
            if (value is null)
            {
                return;
            }

            if (!DigestRegex.IsMatch(value))
            {
                result.AddError(
                    "--expected-digest must be 'sha256:' followed by 64 lowercase hexadecimal characters.");
            }
        });

        Option<DirectoryInfo> destinationOption = new("--destination")
        {
            Description =
                "Directory to extract the verified artifact into; it must not exist or must be empty.",
            Required = true,
        };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Option<FileInfo?> stepSummaryOption = new("--step-summary")
        {
            Description =
                "Path to the GitHub Actions step summary file. Defaults to GITHUB_STEP_SUMMARY.",
        };

        Command command = new(
            "fetch-artifact",
            "Download a workflow artifact and verify it against GitHub's recorded digest.")
        {
            repositoryOption,
            artifactIdOption,
            expectedDigestOption,
            destinationOption,
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
                        long artifactId =
                            parseResult.GetValue(artifactIdOption);
                        string expectedDigest =
                            parseResult.GetValue(expectedDigestOption)!;
                        DirectoryInfo destination =
                            parseResult.GetValue(destinationOption)!;
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);
                        FileInfo? stepSummary =
                            parseResult.GetValue(stepSummaryOption);

                        try
                        {
                            string artifactSha256 = await FetchAsync(
                                    services.GitHubClient,
                                    RequireRepository(repository),
                                    artifactId,
                                    expectedDigest,
                                    destination.FullName,
                                    ct)
                                .ConfigureAwait(false);

                            await services.GitHubOutputWriter.WriteAsync(
                                    githubOutput?.FullName,
                                    [
                                        KeyValuePair.Create(
                                            "artifact_sha256",
                                            artifactSha256),
                                    ],
                                    ct)
                                .ConfigureAwait(false);
                            await services.StandardOutput.WriteLineAsync(
                                    $"Fetched artifact {artifactId} ({expectedDigest}) into {destination.FullName}.")
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

    private static async Task<string> FetchAsync(
        IReleaseGitHubClient gitHubClient,
        string repository,
        long artifactId,
        string expectedDigest,
        string destination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination) ||
            (Directory.Exists(destination) &&
             Directory.EnumerateFileSystemEntries(destination).Any()))
        {
            throw new ReleaseValidationException(
                $"Destination '{destination}' must not exist or must be an empty directory.");
        }

        string temporaryFile = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-artifact-{Guid.NewGuid():N}.zip");
        try
        {
            string downloadedDigest =
                await gitHubClient.DownloadArtifactZipAsync(
                        repository,
                        artifactId,
                        temporaryFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!string.Equals(
                    downloadedDigest,
                    expectedDigest,
                    StringComparison.Ordinal))
            {
                throw new ReleaseValidationException(
                    $"Artifact {artifactId} digest mismatch: expected {expectedDigest}, downloaded {downloadedDigest}.");
            }

            ExtractArchive(temporaryFile, destination, artifactId);
            return downloadedDigest[DigestPrefix.Length..];
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static void ExtractArchive(
        string archivePath,
        string destination,
        long artifactId)
    {
        bool createdDestination = !Directory.Exists(destination);
        Directory.CreateDirectory(destination);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, destination);
        }
        catch (Exception ex)
            when (ex is IOException or
                      InvalidDataException or
                      UnauthorizedAccessException)
        {
            // The destination was empty, so everything in it now came from
            // this archive; never leave a partial extraction behind.
            if (createdDestination)
            {
                Directory.Delete(destination, recursive: true);
            }
            else
            {
                DirectoryInfo directory = new(destination);
                foreach (FileInfo file in directory.EnumerateFiles())
                {
                    file.Delete();
                }

                foreach (DirectoryInfo child in
                         directory.EnumerateDirectories())
                {
                    child.Delete(recursive: true);
                }
            }

            throw new ReleaseValidationException(
                $"Artifact {artifactId} could not be extracted to '{destination}': {ex.Message}",
                ex);
        }
    }

    private static string RequireRepository(string? repository) =>
        string.IsNullOrWhiteSpace(repository)
            ? throw new ReleaseValidationException(
                "--repository is required when GITHUB_REPOSITORY is not set.")
            : repository;
}
