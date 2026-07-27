// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Commands;

internal static class ReleaseCommandSupport
{
    internal static void AddMinimumValidator(
        Option<int> option,
        int minimum,
        string message)
    {
        option.Validators.Add(result =>
        {
            string? value = result.Tokens.LastOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedValue) &&
                parsedValue < minimum)
            {
                result.AddError(message);
            }
        });
    }

    internal static void AddAbsoluteUriValidator(
        Option<string> option,
        string optionName)
    {
        option.Validators.Add(result =>
        {
            string? value = result.Tokens.LastOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out Uri? parsedUri) ||
                !parsedUri.IsAbsoluteUri)
            {
                result.AddError(
                    $"{optionName} must be an absolute URI.");
            }
        });
    }

    internal static Option<FileInfo?> CreateGitHubOutputOption() =>
        new("--github-output")
        {
            Description =
                "Path to the GitHub Actions output file. Defaults to GITHUB_OUTPUT."
        };

    internal static async Task<int> ExecuteAsync(
        TextWriter standardError,
        Func<CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (ReleaseValidationException ex)
        {
            await standardError.WriteLineAsync(ex.Message)
                .ConfigureAwait(false);
            return 1;
        }
    }

    internal static string FormatPublicationState(
        ReleasePackagePublicationState state) =>
        state.ToString().ToLowerInvariant();
}
