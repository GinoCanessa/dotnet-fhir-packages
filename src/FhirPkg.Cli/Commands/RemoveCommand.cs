// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg remove</c> command for removing one or more FHIR packages
/// from the local package cache.
/// </summary>
internal static class RemoveCommand
{
    /// <summary>
    /// Builds the <c>remove</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the remove subcommand.</returns>
    public static Command Build()
    {
        var packagesArg = new Argument<string[]>("packages")
        {
            Description = "One or more package directives to remove (e.g. hl7.fhir.r4.core#4.0.1)",
            Arity = ArgumentArity.OneOrMore
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt."
        };

        var command = new Command("remove", "Remove one or more FHIR packages from the local cache.")
        {
            packagesArg,
            forceOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var packages = parseResult.GetValue(packagesArg);
            var force = parseResult.GetValue(forceOption);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                if (!force && !globalOpts.Quiet && !globalOpts.Json)
                {
                    var packageList = string.Join(", ", packages);
                    if (!AnsiConsole.Confirm($"Remove {packages.Length} package(s): {packageList}?"))
                    {
                        ConsoleOutput.WriteWarning("Aborted.");
                        return ExitCodes.Success;
                    }
                }

                var mgrOptions = globalOpts.BuildManagerOptions();
                var manager = ManagerFactory.Create(mgrOptions);

                var removedCount = 0;
                var failedCount = 0;

                foreach (var directive in packages)
                {
                    ct.ThrowIfCancellationRequested();

                    var removed = await manager.RemoveAsync(directive, ct);

                    if (removed)
                    {
                        removedCount++;
                        if (!globalOpts.Quiet && !globalOpts.Json)
                        {
                            ConsoleOutput.WriteSuccess($"Removed {directive}");
                        }
                    }
                    else
                    {
                        failedCount++;
                        if (!globalOpts.Quiet && !globalOpts.Json)
                        {
                            ConsoleOutput.WriteWarning($"Not found: {directive}");
                        }
                    }
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteSuccess($"{removedCount} removed, {failedCount} not found.");
                }
                else if (!globalOpts.Quiet)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(
                        $"[bold]Summary:[/] [green]{removedCount} removed[/], [yellow]{failedCount} not found[/]");
                }

                return failedCount > 0 ? ExitCodes.NotFound : ExitCodes.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, $"Cache error: {ex.Message}");
                return ExitCodes.CacheError;
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
