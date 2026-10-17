// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Text;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

internal sealed record QualificationSummaryContent(
    string Markdown,
    IReadOnlyList<string> Annotations);

internal static class QualificationSummaryWriter
{
    private const string EmDash = "\u2014";
    private const string MiddleDot = "\u00b7";
    private const string MissingValue = "none";

    private static readonly UTF8Encoding s_utf8Encoding =
        new(encoderShouldEmitUTF8Identifier: false);

    internal static QualificationSummaryContent Render(
        QualificationReport report,
        string title,
        string reportFileName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(reportFileName);

        StringBuilder markdown = new();
        List<string> annotations = [];
        string status = report.Success ? "PASSED" : "FAILED";
        string caseCounts = string.Create(
            CultureInfo.InvariantCulture,
            $"{report.Cases.Count(result => result.Success)} of {report.Cases.Count} cases passed");
        AddLine(
            markdown,
            $"### {EscapeMarkdown(title)} {EmDash} {status}");
        AddLine(markdown);
        if (report.ValidationOnly)
        {
            AddLine(markdown, caseCounts);
            return new QualificationSummaryContent(
                markdown.ToString(),
                annotations);
        }

        QualificationCaseResult? freeze = report.Cases.FirstOrDefault(
            result => string.Equals(
                result.Id,
                FixtureBundle.FreezeCaseId,
                StringComparison.Ordinal));
        if (freeze is not null)
        {
            AddDriftTable(markdown, annotations, report, freeze);
            return new QualificationSummaryContent(
                markdown.ToString(),
                annotations);
        }

        QualificationCaseResult? derive = report.Cases.FirstOrDefault(
            result => string.Equals(
                result.Id,
                FixtureBundle.DeriveCaseId,
                StringComparison.Ordinal));
        if (derive is not null)
        {
            AddDeriveTable(markdown, annotations, report, derive);
            return new QualificationSummaryContent(
                markdown.ToString(),
                annotations);
        }

        AddQualificationInputs(markdown, report);
        AddRejectedArtifacts(markdown, annotations, report);
        AddFailedCases(markdown, annotations, report);
        AddLine(
            markdown,
            $"{caseCounts} {MiddleDot} full detail: {EscapeMarkdown(reportFileName)}");
        return new QualificationSummaryContent(
            markdown.ToString(),
            annotations);
    }

    internal static async Task WriteAsync(
        QualificationReport report,
        string title,
        string reportFileName,
        string summaryPath,
        TextWriter annotationOutput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryPath);
        ArgumentNullException.ThrowIfNull(annotationOutput);
        QualificationSummaryContent content = Render(
            report,
            title,
            reportFileName);
        foreach (string annotation in content.Annotations)
        {
            await annotationOutput.WriteLineAsync(
                    annotation.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await annotationOutput.FlushAsync(cancellationToken)
            .ConfigureAwait(false);

        string fullPath = Path.GetFullPath(summaryPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await using FileStream stream = new(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        await using StreamWriter writer = new(
            stream,
            s_utf8Encoding);
        if (stream.Length > 0)
        {
            await writer.WriteAsync(
                    "\n".AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await writer.WriteAsync(
                content.Markdown.AsMemory(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddQualificationInputs(
        StringBuilder markdown,
        QualificationReport report)
    {
        // A bootstrap failure report cannot tell which inputs were requested.
        if (report.Cases.Any(result => string.Equals(
                result.Id,
                Program.BootstrapCaseId,
                StringComparison.Ordinal)))
        {
            return;
        }

        QualificationCaseResult? verify = report.Cases.FirstOrDefault(
            result => string.Equals(
                result.Id,
                FixtureBundle.VerifyCaseId,
                StringComparison.Ordinal));
        string inputs;
        if (verify is null)
        {
            inputs =
                $"live upstream (no bundle) {MiddleDot} corpus {EscapeMarkdown(report.CorpusSha256)}";
        }
        else
        {
            string bundleSha256 = FindDetail(
                    verify,
                    FixtureBundle.BundleSha256DetailName)
                ?? MissingValue;
            string corpusSha256 = FindDetail(
                    verify,
                    FixtureBundle.CorpusSha256DetailName)
                ?? report.CorpusSha256;
            string approvals = DescribeApprovals(verify);
            inputs =
                $"bundle sha256:{EscapeMarkdown(bundleSha256)} {MiddleDot} corpus {EscapeMarkdown(corpusSha256)} {MiddleDot} approvals: {EscapeMarkdown(approvals)}";
        }

        AddLine(markdown, $"Qualification inputs: {inputs}");
        AddLine(markdown);
    }

    // Lists "<artifact> <pinned> → <approved>" for each approval detail.
    private static string DescribeApprovals(QualificationCaseResult verify)
    {
        string[] approvals = GetApprovalDetails(verify)
            .Select(detail =>
                $"{GetApprovedArtifact(detail)} {DescribeApprovedChange(detail.Value)}")
            .ToArray();
        return approvals.Length > 0
            ? string.Join(", ", approvals)
            : FindDetail(verify, FixtureBundle.ApprovalsDetailName)
                ?? FixtureBundle.NoApprovals;
    }

    private static QualificationDetail[] GetApprovalDetails(
        QualificationCaseResult result) =>
        result.Details
            .Where(detail => detail.Name.StartsWith(
                FixtureBundle.ApprovalDetailPrefix,
                StringComparison.Ordinal))
            .ToArray();

    private static string GetApprovedArtifact(QualificationDetail detail) =>
        detail.Name[FixtureBundle.ApprovalDetailPrefix.Length..];

    // An approval detail reads "<pinned> → <approved> by <approvedBy> at
    // <approvalCommit>"; this keeps "<pinned> → <approved>".
    private static string DescribeApprovedChange(string value)
    {
        int approver = value.IndexOf(" by ", StringComparison.Ordinal);
        return approver < 0 ? value : value[..approver];
    }

    private static (string Pinned, string Approved) ParseApprovedChange(
        string value)
    {
        string change = DescribeApprovedChange(value);
        string separator = $" {FixtureBundle.ApprovalArrow} ";
        int arrow = change.IndexOf(separator, StringComparison.Ordinal);
        return arrow < 0
            ? (change, MissingValue)
            : (change[..arrow], change[(arrow + separator.Length)..]);
    }

    private static string? FindDetail(
        QualificationCaseResult result,
        string name) =>
        result.Details
            .FirstOrDefault(detail => string.Equals(
                detail.Name,
                name,
                StringComparison.Ordinal))
            ?.Value;

    private static void AddRejectedArtifacts(
        StringBuilder markdown,
        List<string> annotations,
        QualificationReport report)
    {
        QualificationArtifactResult[] rejected = report.Artifacts
            .Where(result => !result.Success)
            .ToArray();
        if (rejected.Length == 0)
            return;

        AddLine(markdown, "#### Rejected artifacts");
        AddLine(markdown);
        AddLine(
            markdown,
            "| Artifact | Code | Expected SHA-256 | Actual SHA-256 |");
        AddLine(markdown, "|-|-|-|-|");
        foreach (QualificationArtifactResult artifact in rejected)
        {
            string code = DescribeCode(artifact.Failure);
            string expected = artifact.ExpectedSha256 ?? MissingValue;
            string actual = artifact.ActualSha256 ?? MissingValue;
            AddLine(
                markdown,
                $"| {EscapeMarkdown(artifact.Id)} | {EscapeMarkdown(code)} | {EscapeMarkdown(expected)} | {EscapeMarkdown(actual)} |");
            annotations.Add(FormatError(
                $"Rejected fixture {artifact.Id}",
                $"{code} expected {expected} actual {actual}"));
        }

        AddLine(markdown);
    }

    private static void AddFailedCases(
        StringBuilder markdown,
        List<string> annotations,
        QualificationReport report)
    {
        QualificationCaseResult[] failed = report.Cases
            .Where(result => !result.Success)
            .ToArray();
        if (failed.Length == 0)
            return;

        AddLine(markdown, "#### Failed cases");
        AddLine(markdown);
        AddLine(markdown, "| Case | Classification | Message |");
        AddLine(markdown, "|-|-|-|");
        foreach (QualificationCaseResult result in failed)
        {
            bool dependent = IsDependent(result);
            string classification = dependent
                ? DescribeDependency(result)
                : "independent";
            string message = DescribeFailures(result);
            AddLine(
                markdown,
                $"| {EscapeMarkdown(result.Id)} | {EscapeMarkdown(classification)} | {EscapeMarkdown(message)} |");
            if (!dependent)
            {
                annotations.Add(FormatError(
                    $"Qualification case {result.Id} failed",
                    message));
            }
        }

        AddLine(markdown);
    }

    private static void AddDriftTable(
        StringBuilder markdown,
        List<string> annotations,
        QualificationReport report,
        QualificationCaseResult freeze)
    {
        AddLine(
            markdown,
            "| Artifact | Kind | Pinned SHA-256 | Observed SHA-256 | Upstream Last-Modified | Size | Entries | Result |");
        AddLine(markdown, "|-|-|-|-|-|-|-|-|");
        int pinned = 0;
        int changed = 0;
        foreach (QualificationArtifactResult artifact in report.Artifacts)
        {
            string expected = artifact.ExpectedSha256 ?? MissingValue;
            string actual = artifact.ActualSha256 ?? MissingValue;
            string lastModified = artifact.PublicationUtc ?? MissingValue;
            AddLine(
                markdown,
                $"| {EscapeMarkdown(artifact.Id)} | {EscapeMarkdown(artifact.Kind)} | {EscapeMarkdown(expected)} | {EscapeMarkdown(actual)} | {EscapeMarkdown(lastModified)} | {FormatCount(artifact.CompressedBytes)} | {FormatCount(artifact.EntryCount)} | {DescribeDriftResult(artifact)} |");
            if (string.Equals(
                    artifact.Kind,
                    FixtureBundle.ImmutableKind,
                    StringComparison.Ordinal))
            {
                pinned++;
            }

            if (artifact.Success)
                continue;

            string message = artifact.Failure?.Message
                ?? "No failure was recorded.";
            switch (artifact.Failure?.Code)
            {
                case QualificationFailureCodes.ImmutableHashMismatch:
                    changed++;
                    annotations.Add(FormatError(
                        $"Fixture changed {artifact.Id}",
                        $"pinned {expected} observed {actual}"));
                    break;
                case QualificationFailureCodes.SourceUnreachable:
                    annotations.Add(FormatError(
                        $"Fixture source unreachable {artifact.Id}",
                        message));
                    break;
                default:
                    annotations.Add(FormatError(
                        $"Fixture failed {artifact.Id}",
                        message));
                    break;
            }
        }

        AddLine(markdown);
        if (!freeze.Success
            && report.Artifacts.All(artifact => artifact.Success))
        {
            string message = DescribeFailures(freeze);
            AddLine(
                markdown,
                $"{EscapeMarkdown(freeze.Id)} failed: {EscapeMarkdown(message)}");
            AddLine(markdown);
            annotations.Add(FormatError(
                $"Qualification case {freeze.Id} failed",
                message));
        }

        string? bundleSha256 = freeze.Success
            ? freeze.Details
                .FirstOrDefault(detail => string.Equals(
                    detail.Name,
                    FixtureBundle.BundleSha256DetailName,
                    StringComparison.Ordinal))
                ?.Value
            : null;
        string outcome = bundleSha256 is null
            ? "no bundle frozen"
            : $"bundle sha256:{EscapeMarkdown(bundleSha256)} frozen";
        AddLine(
            markdown,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{changed} of {pinned} pinned artifacts changed {MiddleDot} {outcome}"));
    }

    private static string DescribeDriftResult(
        QualificationArtifactResult artifact)
    {
        if (artifact.Success)
        {
            return string.Equals(
                artifact.Kind,
                FixtureBundle.MutableKind,
                StringComparison.Ordinal)
                ? "captured"
                : "verified";
        }

        return artifact.Failure?.Code switch
        {
            QualificationFailureCodes.ImmutableHashMismatch =>
                "**changed**",
            QualificationFailureCodes.SourceUnreachable =>
                "**unreachable**",
            _ => "**failed**"
        };
    }

    private static void AddDeriveTable(
        StringBuilder markdown,
        List<string> annotations,
        QualificationReport report,
        QualificationCaseResult derive)
    {
        QualificationDetail[] approvals = GetApprovalDetails(derive);
        bool artifactFailed = false;
        if (approvals.Length > 0)
        {
            AddLine(
                markdown,
                "| Artifact | Pinned SHA-256 | Approved SHA-256 | Observed SHA-256 | Result |");
            AddLine(markdown, "|-|-|-|-|-|");
            foreach (QualificationDetail approval in approvals)
            {
                string id = GetApprovedArtifact(approval);
                (string pinned, string approved) =
                    ParseApprovedChange(approval.Value);
                QualificationArtifactResult? artifact =
                    report.Artifacts.FirstOrDefault(result => string.Equals(
                        result.Id,
                        id,
                        StringComparison.Ordinal));
                string observed = artifact?.ActualSha256 ?? MissingValue;
                AddLine(
                    markdown,
                    $"| {EscapeMarkdown(id)} | {EscapeMarkdown(pinned)} | {EscapeMarkdown(approved)} | {EscapeMarkdown(observed)} | {DescribeDeriveResult(artifact)} |");
                if (artifact is null || artifact.Success)
                    continue;

                artifactFailed = true;
                annotations.Add(FormatError(
                    $"Approved bytes unavailable {id}",
                    artifact.Failure?.Message ?? "No failure was recorded."));
            }

            AddLine(markdown);
        }

        if (!derive.Success && !artifactFailed)
        {
            string message = DescribeFailures(derive);
            AddLine(
                markdown,
                $"{EscapeMarkdown(derive.Id)} failed: {EscapeMarkdown(message)}");
            AddLine(markdown);
            annotations.Add(FormatError(
                $"Qualification case {derive.Id} failed",
                message));
        }

        string? derivedSha256 = derive.Success
            ? FindDetail(
                derive,
                FixtureBundle.DerivedBundleSha256DetailName)
            : null;
        string baseSha256 = FindDetail(
                derive,
                FixtureBundle.BaseBundleSha256DetailName)
            ?? MissingValue;
        AddLine(
            markdown,
            derivedSha256 is null
                ? "no bundle derived"
                : $"derived bundle sha256:{EscapeMarkdown(derivedSha256)} from base sha256:{EscapeMarkdown(baseSha256)}");
    }

    private static string DescribeDeriveResult(
        QualificationArtifactResult? artifact)
    {
        if (artifact is null)
            return "not fetched";

        if (artifact.Success)
            return "verified";

        return artifact.Failure?.Code switch
        {
            QualificationFailureCodes.ApprovedBytesUnavailable =>
                "**unavailable**",
            QualificationFailureCodes.SourceUnreachable =>
                "**unreachable**",
            _ => "**failed**"
        };
    }

    private static string FormatCount(long? value) =>
        value is long count
            ? QualificationFormatting.Invariant(count)
            : MissingValue;

    private static bool IsDependent(QualificationCaseResult result) =>
        result.Failures.Count > 0
        && result.Failures.All(failure => string.Equals(
            failure.Code,
            QualificationFailureCodes.RequiredArtifactUnavailable,
            StringComparison.Ordinal));

    private static string DescribeDependency(
        QualificationCaseResult result)
    {
        string[] artifacts = result.Details
            .Where(detail => string.Equals(
                detail.Name,
                RequiredArtifactUnavailableException.DetailName,
                StringComparison.Ordinal))
            .Select(detail => detail.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return artifacts.Length == 0
            ? "dependent"
            : $"dependent on {string.Join(", ", artifacts)}";
    }

    private static string DescribeFailures(
        QualificationCaseResult result) =>
        result.Failures.Count == 0
            ? "No failure was recorded."
            : string.Join(
                " ",
                result.Failures.Select(failure => failure.Message));

    private static string DescribeCode(QualificationFailure? failure) =>
        failure switch
        {
            { Code: string code } => code,
            { } unclassified =>
                $"unclassified ({unclassified.ExceptionType})",
            null => "unclassified"
        };

    private static string FormatError(
        string title,
        string message) =>
        $"::error title={EscapeProperty(title)}::{EscapeData(message)}";

    private static string EscapeData(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string EscapeProperty(string value) =>
        EscapeData(value)
            .Replace(":", "%3A", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);

    private static string EscapeMarkdown(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value.ReplaceLineEndings(" "))
        {
            switch (character)
            {
                case '\\':
                case '|':
                    builder.Append('\\').Append(character);
                    break;
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AddLine(
        StringBuilder markdown,
        string line = "") =>
        markdown.Append(line).Append('\n');
}
