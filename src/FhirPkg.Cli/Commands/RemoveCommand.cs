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
        Argument<string[]> packagesArg = new Argument<string[]>("packages")
        {
            Description = "One or more package directives to remove (e.g. hl7.fhir.r4.core#4.0.1)",
            Arity = ArgumentArity.OneOrMore
        };

        Option<bool> forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt."
        };

        Command command = new Command("remove", "Remove one or more FHIR packages from the local cache.")
        {
            packagesArg,
            forceOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string[] packages = parseResult.GetValue(packagesArg)!;
            bool force = parseResult.GetValue(forceOption);

            GlobalOptions globalOpts = parseResult.GetGlobalOptions();

            try
            {
                if (!force && !globalOpts.Quiet && !globalOpts.Json)
                {
                    string packageList = string.Join(", ", packages);
                    if (!AnsiConsole.Confirm($"Remove {packages.Length} package(s): {packageList}?"))
                    {
                        ConsoleOutput.WriteWarning("Aborted.");
                        return ExitCodes.Success;
                    }
                }

                FhirPackageManagerOptions mgrOptions = globalOpts.BuildManagerOptions();
                FhirPackageManager manager = ManagerFactory.Create(mgrOptions);

                int removedCount = 0;
                int failedCount = 0;

                foreach (string? directive in packages)
                {
                    ct.ThrowIfCancellationRequested();

                    bool removed = await manager.RemoveAsync(directive, ct);

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
