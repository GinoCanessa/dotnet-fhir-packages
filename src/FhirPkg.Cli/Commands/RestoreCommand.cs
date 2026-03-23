// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg restore</c> command for restoring FHIR package dependencies
/// from a project manifest (e.g. package.json or .fhir-pkg.json).
/// </summary>
internal static class RestoreCommand
{
    /// <summary>
    /// Builds the <c>restore</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the restore subcommand.</returns>
    public static Command Build()
    {
        Argument<string> projectPathArg = new Argument<string>("project-path")
        {
            Description = "Path to the project directory containing a package manifest.",
            DefaultValueFactory = _ => "."
        };

        Option<string?> lockFileOption = new Option<string?>("--lock-file", "-l")
        {
            Description = "Path to a lock file to use for deterministic restores."
        };

        Option<bool> noLockOption = new Option<bool>("--no-lock")
        {
            Description = "Do not write or update the lock file."
        };

        Option<ConflictResolutionStrategy> conflictStrategyOption = new Option<ConflictResolutionStrategy>("--conflict-strategy")
        {
            Description = "Strategy for resolving version conflicts (HighestWins, FirstWins, Error).",
            DefaultValueFactory = _ => ConflictResolutionStrategy.HighestWins
        };

        Option<int> maxDepthOption = new Option<int>("--max-depth")
        {
            Description = "Maximum recursion depth for dependency resolution.",
            DefaultValueFactory = _ => 20
        };

        Option<string?> fhirVersionOption = new Option<string?>("--fhir-version", "-f")
        {
            Description = "Preferred FHIR release (R4, R4B, R5, R6)."
        };

        Command command = new Command("restore", "Restore FHIR package dependencies from a project manifest.")
        {
            projectPathArg,
            lockFileOption,
            noLockOption,
            conflictStrategyOption,
            maxDepthOption,
            fhirVersionOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string projectPath = parseResult.GetValue(projectPathArg)!;
            string? lockFile = parseResult.GetValue(lockFileOption);
            bool noLock = parseResult.GetValue(noLockOption);
            ConflictResolutionStrategy conflictStrategy = parseResult.GetValue(conflictStrategyOption);
            int maxDepth = parseResult.GetValue(maxDepthOption);
            string? fhirVersion = parseResult.GetValue(fhirVersionOption);

            GlobalOptions globalOpts = parseResult.GetGlobalOptions();

            try
            {
                FhirPackageManagerOptions mgrOptions = globalOpts.BuildManagerOptions();
                FhirPackageManager manager = ManagerFactory.Create(mgrOptions);

                RestoreOptions restoreOptions = new RestoreOptions
                {
                    ConflictStrategy = conflictStrategy,
                    WriteLockFile = !noLock,
                    MaxDepth = maxDepth
                };

                if (fhirVersion is not null && Enum.TryParse<FhirRelease>(fhirVersion, ignoreCase: true, out FhirRelease release))
                {
                    restoreOptions.PreferredFhirRelease = release;
                }

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Restoring from: {Path.GetFullPath(projectPath)}");
                    ConsoleOutput.WriteVerbose($"Conflict strategy: {conflictStrategy}");
                }

                PackageClosure closure;

                if (!globalOpts.Quiet && !globalOpts.Json)
                {
                    closure = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Restoring packages...", async _ =>
                            await manager.RestoreAsync(projectPath, restoreOptions, ct));
                }
                else
                {
                    closure = await manager.RestoreAsync(projectPath, restoreOptions, ct);
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteRestoreResult(closure);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WriteRestoreResult(closure);
                }

                return closure.IsComplete ? ExitCodes.Success : ExitCodes.DependencyResolutionFail;
            }
            catch (FileNotFoundException ex)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, $"Manifest not found: {ex.Message}");
                return ExitCodes.NotFound;
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
