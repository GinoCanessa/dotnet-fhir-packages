// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg resolve</c> command for resolving a package directive
/// to a concrete version and download URL without actually downloading it.
/// </summary>
internal static class ResolveCommand
{
    /// <summary>
    /// Builds the <c>resolve</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the resolve subcommand.</returns>
    public static Command Build()
    {
        var directiveArg = new Argument<string>("directive")
        {
            Description = "Package directive to resolve (e.g. hl7.fhir.r4.core#4.0.1 or hl7.fhir.r4.core#latest)."
        };

        var command = new Command("resolve", "Resolve a package directive to a concrete version and download URL.")
        {
            directiveArg
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var directive = parseResult.GetValue(directiveArg);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                var mgrOptions = globalOpts.BuildManagerOptions();
                var manager = ManagerFactory.Create(mgrOptions);

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Resolving: {directive}");
                }

                ResolvedDirective? resolved;

                if (!globalOpts.Quiet && !globalOpts.Json)
                {
                    resolved = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Resolving...", async _ =>
                            await manager.ResolveAsync(directive, ct));
                }
                else
                {
                    resolved = await manager.ResolveAsync(directive, ct);
                }

                if (resolved is null)
                {
                    CommandHelpers.WriteErrorOutput(globalOpts, $"Could not resolve '{directive}'.");
                    return ExitCodes.NotFound;
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteResolveResult(resolved);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WriteResolveResult(resolved);
                }

                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, $"Network error: {ex.Message}");
                return ExitCodes.NetworkError;
            }
            catch (OperationCanceledException)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, "Operation was cancelled.");
                return ExitCodes.GeneralError;
            }
            catch (Exception ex)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, ex.Message);
                return ExitCodes.GeneralError;
            }
        });

        return command;
    }
}
