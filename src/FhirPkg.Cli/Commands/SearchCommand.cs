// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using FhirPkg.Registry;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg search</c> command for searching FHIR package registries.
/// </summary>
internal static class SearchCommand
{
    /// <summary>
    /// Builds the <c>search</c> <see cref="Command"/> with all options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the search subcommand.</returns>
    public static Command Build()
    {
        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "Package name prefix to search for."
        };

        var canonicalOption = new Option<string?>("--canonical")
        {
            Description = "Filter by implementation guide canonical URL."
        };

        var fhirVersionOption = new Option<string?>("--fhir-version", "-f")
        {
            Description = "Filter by FHIR version (R4, R4B, R5, R6)."
        };

        var sortOption = new Option<string?>("--sort", "-s")
        {
            Description = "Sort results by: name, date, version."
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of results to return.",
            DefaultValueFactory = _ => 50
        };

        var registryOption = new Option<string?>("--registry", "-r")
        {
            Description = "Custom registry URL to search."
        };

        var command = new Command("search", "Search FHIR package registries.")
        {
            nameOption,
            canonicalOption,
            fhirVersionOption,
            sortOption,
            limitOption,
            registryOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameOption);
            var canonical = parseResult.GetValue(canonicalOption);
            var fhirVersion = parseResult.GetValue(fhirVersionOption);
            var sort = parseResult.GetValue(sortOption);
            var limit = parseResult.GetValue(limitOption);
            var registry = parseResult.GetValue(registryOption);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                var mgrOptions = globalOpts.BuildManagerOptions();

                if (registry is not null)
                {
                    mgrOptions.Registries.Insert(0, new RegistryEndpoint
                    {
                        Url = registry,
                        Type = RegistryType.FhirNpm
                    });
                }

                var manager = new FhirPackageManager(mgrOptions);

                var criteria = new PackageSearchCriteria
                {
                    Name = name,
                    Canonical = canonical,
                    FhirVersion = fhirVersion,
                    Sort = sort
                };

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Searching for packages (name: {name ?? "*"}, FHIR: {fhirVersion ?? "any"})...");
                }

                IReadOnlyList<CatalogEntry> results;

                if (!globalOpts.Quiet && !globalOpts.Json)
                {
                    results = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Searching registries...", async _ =>
                            await manager.SearchAsync(criteria, ct));
                }
                else
                {
                    results = await manager.SearchAsync(criteria, ct);
                }

                // Apply client-side limit
                if (results.Count > limit)
                {
                    results = results.Take(limit).ToList();
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteSearchResults(results);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WriteSearchResults(results);
                }

                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                WriteErrorOutput(globalOpts, $"Network error: {ex.Message}");
                return ExitCodes.NetworkError;
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
