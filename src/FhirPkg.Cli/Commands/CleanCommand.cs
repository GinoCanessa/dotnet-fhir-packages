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
            Description = "Only remove current and current$branch CI alias packages."
        };

        Option<int?> olderThanOption = new Option<int?>("--older-than")
        {
            Description = "Only remove packages installed more than N days ago."
        };
        olderThanOption.Validators.Add(result =>
        {
            string? value =
                result.Tokens.LastOrDefault()?.Value;
            if (int.TryParse(
                    value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int olderThan)
                && olderThan < 0)
            {
                result.AddError(
                    "--older-than must be zero or greater.");
            }
        });

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
                using FhirPackageManager manager =
                    ManagerFactory.Create(mgrOptions);

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
                    IReadOnlyList<PackageRecord> allPackages =
                        await manager.ListCachedSummariesAsync(
                                cancellationToken: ct)
                            .ConfigureAwait(false);
                    IReadOnlyList<PackageRecord> toRemove =
                        SelectPackagesForRemoval(
                            allPackages,
                            ciOnly,
                            olderThan,
                            DateTimeOffset.UtcNow);
                    removed = 0;

                    foreach (PackageRecord pkg in toRemove)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (await manager.RemoveIfUnchangedAsync(
                                pkg,
                                ct)
                            .ConfigureAwait(false))
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

    internal static IReadOnlyList<PackageRecord>
        SelectPackagesForRemoval(
            IEnumerable<PackageRecord> packages,
            bool ciOnly,
            int? olderThanDays,
            DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packages);
        DateTimeOffset? cutoff =
            CalculateInstallationCutoff(
                olderThanDays,
                now);
        IEnumerable<PackageRecord> candidates =
            packages;
        if (ciOnly)
        {
            candidates = candidates.Where(
                package =>
                {
                    VersionType versionType =
                        PackageDirective.ClassifyVersion(
                            package.Reference.Version);
                    return versionType
                        is VersionType.CiBuild
                            or VersionType.CiBuildBranch;
                });
        }

        if (cutoff is DateTimeOffset ageCutoff)
        {
            candidates = candidates.Where(
                package =>
                    package.InstalledAt
                        is DateTimeOffset installedAt
                    && installedAt < ageCutoff);
        }

        return candidates.ToList();
    }

    private static DateTimeOffset?
        CalculateInstallationCutoff(
            int? olderThanDays,
            DateTimeOffset now)
    {
        if (olderThanDays is null)
            return null;

        if (olderThanDays.Value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThanDays),
                olderThanDays,
                "Age must be zero or greater.");
        }

        long availableWholeDays =
            (now.UtcDateTime.Ticks
             - DateTimeOffset.MinValue.UtcDateTime.Ticks)
            / TimeSpan.TicksPerDay;
        if (olderThanDays.Value
            > availableWholeDays)
        {
            return DateTimeOffset.MinValue;
        }

        return now.AddDays(
            -olderThanDays.Value);
    }
}
