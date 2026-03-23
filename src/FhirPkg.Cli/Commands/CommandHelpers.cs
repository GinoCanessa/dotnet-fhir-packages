// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cli.Formatting;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Shared helper methods for CLI command implementations.
/// </summary>
internal static class CommandHelpers
{
    /// <summary>
    /// Writes an error message to the appropriate output channel based on global options.
    /// Uses JSON format when <see cref="GlobalOptions.Json"/> is enabled, otherwise
    /// writes a formatted console error.
    /// </summary>
    /// <param name="opts">The global CLI options controlling output format.</param>
    /// <param name="message">The error message to display.</param>
    public static void WriteErrorOutput(GlobalOptions opts, string message)
    {
        if (opts.Json)
            JsonOutput.WriteError(message);
        else
            ConsoleOutput.WriteError(message);
    }
}
