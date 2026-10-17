// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text;
using FhirPkg.Release.Infrastructure;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class VerifyRecoveryCommand
{
    internal const string FailureTitle = "Recovery refused";
    internal const string SummaryHeading = "### Recovery authorization";

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

        Option<string> versionOption = new("--version")
        {
            Description = "Release version being recovered.",
            Required = true,
        };

        Option<string> tagOption = new("--tag")
        {
            Description = "Release tag being recovered.",
            Required = true,
        };

        Option<string> commitOption = new("--commit")
        {
            Description =
                "Release tag commit, as validated by validate-inputs.",
            Required = true,
        };

        Option<long> recoverRunIdOption = new("--recover-run-id")
        {
            Description = "Original release workflow run to finish.",
            Required = true,
        };
        recoverRunIdOption.Validators.Add(result =>
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
                result.AddError("--recover-run-id must be at least one.");
            }
        });

        Option<string?> approvalCommitOption = new("--approval-commit")
        {
            Description =
                "Full SHA of the main commit holding the release's fixture approval. Empty means no approval.",
        };

        Option<bool> dryRunOption = new("--dry-run")
        {
            Description =
                "Rehearse recovery: report the GitHub Release instead of requiring it, and also accept a workflow_dispatch original run.",
        };

        Option<FileInfo?> githubOutputOption =
            ReleaseCommandSupport.CreateGitHubOutputOption();

        Option<FileInfo?> stepSummaryOption = new("--step-summary")
        {
            Description =
                "Path to the GitHub Actions step summary file. Defaults to GITHUB_STEP_SUMMARY.",
        };

        Command command = new(
            "verify-recovery",
            "Verify from GitHub's records and git that an original release run may be finished.")
        {
            repositoryOption,
            versionOption,
            tagOption,
            commitOption,
            recoverRunIdOption,
            approvalCommitOption,
            dryRunOption,
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
                        string version = parseResult.GetValue(versionOption)!;
                        string tag = parseResult.GetValue(tagOption)!;
                        string commit = parseResult.GetValue(commitOption)!;
                        long recoverRunId =
                            parseResult.GetValue(recoverRunIdOption);
                        string? approvalCommit =
                            parseResult.GetValue(approvalCommitOption);
                        bool dryRun = parseResult.GetValue(dryRunOption);
                        FileInfo? githubOutput =
                            parseResult.GetValue(githubOutputOption);
                        FileInfo? stepSummary =
                            parseResult.GetValue(stepSummaryOption);

                        RecoveryVerificationResult result;
                        try
                        {
                            result = await services.RecoveryVerifier
                                .VerifyAsync(
                                    new RecoveryRequest(
                                        RequireRepository(repository),
                                        tag,
                                        version,
                                        commit,
                                        recoverRunId,
                                        approvalCommit,
                                        dryRun),
                                    ct)
                                .ConfigureAwait(false);
                        }
                        catch (ReleaseValidationException ex)
                        {
                            await services.StepSummaryWriter.AppendAsync(
                                    stepSummary?.FullName,
                                    $"{SummaryHeading}\n\n{GitHubMarkdown.Escape(ex.Message)}\n",
                                    ct)
                                .ConfigureAwait(false);
                            await services.StandardOutput.WriteLineAsync(
                                    GitHubAnnotations.Error(
                                        FailureTitle,
                                        ex.Message))
                                .ConfigureAwait(false);
                            throw;
                        }

                        await services.StepSummaryWriter.AppendAsync(
                                stepSummary?.FullName,
                                FormatSummary(result),
                                ct)
                            .ConfigureAwait(false);

                        if (!result.Authorized ||
                            result.CandidateArtifact is not
                                { Digest: string candidateDigest } candidate ||
                            result.BundleArtifact is not
                                { Digest: string bundleDigest } bundle)
                        {
                            foreach (RecoveryRuleResult rule in result.Rules)
                            {
                                if (rule.Outcome != RecoveryRuleOutcome.Failed)
                                {
                                    continue;
                                }

                                string title =
                                    $"{FailureTitle} ({rule.Id} {rule.Name})";
                                await services.StandardOutput.WriteLineAsync(
                                        GitHubAnnotations.Error(
                                            title,
                                            rule.Detail))
                                    .ConfigureAwait(false);
                                await services.StandardError.WriteLineAsync(
                                        $"{title}: {rule.Detail}")
                                    .ConfigureAwait(false);
                            }

                            return 1;
                        }

                        await services.GitHubOutputWriter.WriteAsync(
                                githubOutput?.FullName,
                                [
                                    KeyValuePair.Create(
                                        "release_published",
                                        result.ReleasePublished
                                            ? "true"
                                            : "false"),
                                    KeyValuePair.Create(
                                        "candidate_artifact_id",
                                        candidate.Id.ToString(
                                            CultureInfo.InvariantCulture)),
                                    KeyValuePair.Create(
                                        "candidate_artifact_digest",
                                        candidateDigest),
                                    KeyValuePair.Create(
                                        "original_bundle_artifact_id",
                                        bundle.Id.ToString(
                                            CultureInfo.InvariantCulture)),
                                    KeyValuePair.Create(
                                        "original_bundle_artifact_digest",
                                        bundleDigest),
                                    KeyValuePair.Create(
                                        "approval_commit",
                                        approvalCommit ?? string.Empty),
                                    KeyValuePair.Create(
                                        "approval_author",
                                        result.ApprovalAuthor ??
                                        string.Empty),
                                ],
                                ct)
                            .ConfigureAwait(false);
                        string dryRunNote = dryRun
                            ? " (dry run)"
                            : string.Empty;
                        await services.StandardOutput.WriteLineAsync(
                                $"Authorized recovery of {tag} from run {recoverRunId.ToString(CultureInfo.InvariantCulture)}{dryRunNote}; GitHub Release published: {(result.ReleasePublished ? "true" : "false")}.")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }

    private static string FormatSummary(RecoveryVerificationResult result)
    {
        StringBuilder summary = new();
        summary.Append(SummaryHeading).Append("\n\n");
        summary.Append("| Rule | Result | Detail |\n");
        summary.Append("|-|-|-|\n");
        foreach (RecoveryRuleResult rule in result.Rules)
        {
            summary
                .Append("| ")
                .Append(rule.Id)
                .Append(" `")
                .Append(rule.Name)
                .Append("` | ")
                .Append(ReleaseRecoveryVerifier.FormatOutcome(rule.Outcome))
                .Append(" | ")
                .Append(GitHubMarkdown.Escape(rule.Detail))
                .Append(" |\n");
        }

        summary.Append('\n');
        if (result.SourceCiJobs.Count == 0)
        {
            summary.Append(
                "Original source-CI jobs (linked, not re-run): none.\n");
            return summary.ToString();
        }

        summary.Append("Original source-CI jobs (linked, not re-run):\n\n");
        foreach (string jobUrl in result.SourceCiJobs)
        {
            summary.Append("- <").Append(jobUrl).Append(">\n");
        }

        return summary.ToString();
    }

    private static string RequireRepository(string? repository) =>
        string.IsNullOrWhiteSpace(repository)
            ? throw new ReleaseValidationException(
                "--repository is required when GITHUB_REPOSITORY is not set.")
            : repository;
}
