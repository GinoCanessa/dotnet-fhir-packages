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
        Option<bool> forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt."
        };

        Option<bool> ciOnlyOption = new Option<bool>("--ci-only")
        {
            Description = "Only remove CI build (pre-release snapshot) packages."
        };

        Option<int?> olderThanOption = new Option<int?>("--older-than")
        {
            Description = "Remove packages not accessed in the last N days."
        };

        Command command = new Command("clean", "Clear the local FHIR package cache.")
        {
            forceOption,
            ciOnlyOption,
            olderThanOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            bool force = parseResult.GetValue(forceOption);
            bool ciOnly = parseResult.GetValue(ciOnlyOption);
            int? olderThan = parseResult.GetValue(olderThanOption);

            GlobalOptions globalOpts = parseResult.GetGlobalOptions();

            try
            {
                FhirPackageManagerOptions mgrOptions = globalOpts.BuildManagerOptions();
                FhirPackageManager manager = ManagerFactory.Create(mgrOptions);

                // Build a description of what will be cleaned
                string description = (ciOnly, olderThan) switch
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
                    IReadOnlyList<PackageRecord> allPackages = await manager.ListCachedAsync(cancellationToken: ct);

                    IEnumerable<PackageRecord> candidates = allPackages.AsEnumerable();

                    if (ciOnly)
                    {
                        candidates = candidates.Where(p =>
                            p.Reference.Version?.Contains("-") == true ||
                            p.Reference.Version == "current" ||
                            p.Reference.Version == "dev");
                    }

                    if (olderThan is int days)
                    {
                        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-days);
                        candidates = candidates.Where(p =>
                            p.InstalledAt is null || p.InstalledAt < cutoff);
                    }

                    List<PackageRecord> toRemove = candidates.ToList();
                    removed = 0;

                    foreach (PackageRecord pkg in toRemove)
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
