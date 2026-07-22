// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;

namespace FhirPkg.Release.Infrastructure;

internal interface IGitHubOutputWriter
{
    Task WriteAsync(
        string? explicitPath,
        IReadOnlyList<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken);
}

internal sealed class GitHubOutputWriter : IGitHubOutputWriter
{
    private static readonly UTF8Encoding s_utf8Encoding =
        new(encoderShouldEmitUTF8Identifier: false);

    public async Task WriteAsync(
        string? explicitPath,
        IReadOnlyList<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values);

        string? outputPath = ResolvePath(explicitPath);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        using FileStream stream = new(
            outputPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        using StreamWriter writer = new(stream, s_utf8Encoding);
        foreach (KeyValuePair<string, string> value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                    $"{value.Key}={value.Value}")
                .ConfigureAwait(false);
        }
    }

    private static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    }

    private static void ValidateValues(
        IReadOnlyList<KeyValuePair<string, string>> values)
    {
        foreach (KeyValuePair<string, string> value in values)
        {
            ValidatePart(value.Key, nameof(values));
            ValidatePart(value.Value, nameof(values));
        }
    }

    private static void ValidatePart(
        string value,
        string parameterName)
    {
        if (value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "GitHub output names and values must not contain CR or LF characters.",
                parameterName);
        }
    }
}
