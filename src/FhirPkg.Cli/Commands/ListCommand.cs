// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg list</c> command for listing FHIR packages in the local cache.
/// </summary>
internal static class ListCommand
{
    /// <summary>
    /// Builds the <c>list</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the list subcommand.</returns>
    public static Command Build()
    {
        Argument<string?> filterArg = new Argument<string?>("filter")
        {
            Description = "Optional filter to match package names (supports glob patterns).",
            DefaultValueFactory = _ => null
        };

        Option<string> sortOption = new Option<string>("--sort", "-s")
        {
            Description = "Sort order: name, version, date, size.",
            DefaultValueFactory = _ => "name"
        };

        Option<bool> showSizeOption = new Option<bool>("--show-size")
        {
            Description = "Include package sizes in the output."
        };

        Command command = new Command("list", "List FHIR packages in the local cache.")
        {
            filterArg,
            sortOption,
            showSizeOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string? filter = parseResult.GetValue(filterArg);
            string? sort = parseResult.GetValue(sortOption);
            bool showSize = parseResult.GetValue(showSizeOption);

            GlobalOptions globalOpts = parseResult.GetGlobalOptions();

            try
            {
                FhirPackageManagerOptions mgrOptions = globalOpts.BuildManagerOptions();
                FhirPackageManager manager = ManagerFactory.Create(mgrOptions);

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Listing cached packages (filter: {filter ?? "*"}, sort: {sort})");
                }

                IReadOnlyList<PackageRecord> packages = await manager.ListCachedAsync(filter, ct);

                // Apply client-side sorting
                IReadOnlyList<PackageRecord> sorted = sort?.ToLowerInvariant() switch
                {
                    "version" => packages.OrderBy(p => p.Reference.Version).ToList(),
                    "date" => packages.OrderByDescending(p => p.InstalledAt).ToList(),
                    "size" => packages.OrderByDescending(p => p.SizeBytes ?? 0).ToList(),
                    _ => packages.OrderBy(p => p.Reference.Name).ToList()
                };

                if (globalOpts.Json)
                {
                    JsonOutput.WritePackageList(sorted, showSize);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WritePackageList(sorted, showSize);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CommandHelpers.WriteErrorOutput(globalOpts, $"Cache error: {ex.Message}");
                return ExitCodes.CacheError;
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
