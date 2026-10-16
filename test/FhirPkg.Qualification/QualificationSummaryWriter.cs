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
