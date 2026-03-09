// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg clean</c> command for clearing the local FHIR package cache.
/// </summary>
internal static class CleanCommand
{
    /// <summary>
    /// Builds the <c>clean</c> <see cref="Command"/> with all options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the clean subcommand.</returns>
    public static Command Build()
    {
        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt."
        };

        var ciOnlyOption = new Option<bool>("--ci-only")
        {
            Description = "Only remove CI build (pre-release snapshot) packages."
        };

        var olderThanOption = new Option<int?>("--older-than")
        {
            Description = "Remove packages not accessed in the last N days."
        };

        var command = new Command("clean", "Clear the local FHIR package cache.")
        {
            forceOption,
            ciOnlyOption,
            olderThanOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var force = parseResult.GetValue(forceOption);
            var ciOnly = parseResult.GetValue(ciOnlyOption);
            var olderThan = parseResult.GetValue(olderThanOption);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                var mgrOptions = globalOpts.BuildManagerOptions();
                var manager = new FhirPackageManager(mgrOptions);

                // Build a description of what will be cleaned
                var description = (ciOnly, olderThan) switch
                {
                    (true, int days) => $"CI build packages older than {days} days",
                    (true, null) => "all CI build packages",
                    (false, int days) => $"all packages older than {days} days",
                    _ => "the entire package cache"
                };

                if (!force && !globalOpts.Quiet && !globalOpts.Json)
                {
                    if (!AnsiConsole.Confirm($"Clean {description}?"))
                    {
                        ConsoleOutput.WriteWarning("Aborted.");
                        return ExitCodes.Success;
                    }
                }

                int removed;

                if (ciOnly || olderThan is not null)
                {
                    // Filter-based removal: list first, then remove matching packages
                    var allPackages = await manager.ListCachedAsync(cancellationToken: ct);

                    var candidates = allPackages.AsEnumerable();

                    if (ciOnly)
                    {
                        candidates = candidates.Where(p =>
                            p.Reference.Version?.Contains("-") == true ||
                            p.Reference.Version == "current" ||
                            p.Reference.Version == "dev");
                    }

                    if (olderThan is int days)
                    {
                        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
                        candidates = candidates.Where(p =>
                            p.InstalledAt is null || p.InstalledAt < cutoff);
                    }

                    var toRemove = candidates.ToList();
                    removed = 0;

                    foreach (var pkg in toRemove)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (await manager.RemoveAsync(pkg.Reference.FhirDirective, ct))
                        {
                            removed++;
                        }
                    }
                }
                else
                {
                    // Full cache clean
                    removed = await manager.CleanCacheAsync(ct);
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteSuccess($"Removed {removed} package(s).");
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WriteSuccess($"Removed {removed} package(s) from cache.");
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteErrorOutput(globalOpts, $"Cache error: {ex.Message}");
                return ExitCodes.CacheError;
            }
            catch (OperationCanceledException)
            {
                WriteErrorOutput(globalOpts, "Operation was cancelled.");
                return ExitCodes.GeneralError;
            }
            catch (Exception ex)
            {
                WriteErrorOutput(globalOpts, ex.Message);
                return ExitCodes.GeneralError;
            }
        });

        return command;
    }

    private static void WriteErrorOutput(GlobalOptions opts, string message)
    {
        if (opts.Json)
            JsonOutput.WriteError(message);
        else
            ConsoleOutput.WriteError(message);
    }
}
