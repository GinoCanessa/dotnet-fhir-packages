// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;

namespace FhirPkg.Release.Infrastructure;

internal interface IGitHubStepSummaryWriter
{
    Task AppendAsync(
        string? explicitPath,
        string markdown,
        CancellationToken cancellationToken);
}

internal sealed class GitHubStepSummaryWriter : IGitHubStepSummaryWriter
{
    private static readonly UTF8Encoding s_utf8Encoding =
        new(encoderShouldEmitUTF8Identifier: false);

    public async Task AppendAsync(
        string? explicitPath,
        string markdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string? summaryPath = ResolvePath(explicitPath);
        if (string.IsNullOrWhiteSpace(summaryPath))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(
            summaryPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        using StreamWriter writer = new(stream, s_utf8Encoding);
        await writer.WriteAsync(markdown).ConfigureAwait(false);
        if (!markdown.EndsWith('\n'))
        {
            await writer.WriteAsync('\n').ConfigureAwait(false);
        }
    }

    private static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    }
}

internal static class GitHubAnnotations
{
    internal static string Error(
        string title,
        string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        return $"::error title={EscapeProperty(title)}::{EscapeData(message)}";
    }

    private static string EscapeData(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string EscapeProperty(string value) =>
        EscapeData(value)
            .Replace(":", "%3A", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);
}

internal static class GitHubMarkdown
{
    internal static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

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
}
