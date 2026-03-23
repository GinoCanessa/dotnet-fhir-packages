// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg info</c> command for displaying detailed information
/// about a specific FHIR package, including available versions and dependencies.
/// </summary>
internal static class InfoCommand
{
    /// <summary>
    /// Builds the <c>info</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the info subcommand.</returns>
    public static Command Build()
    {
        var packageArg = new Argument<string>("package")
        {
            Description = "Package identifier (e.g. hl7.fhir.r4.core)."
        };

        var versionsOption = new Option<bool>("--versions")
        {
            Description = "Show all available versions."
        };

        var dependenciesOption = new Option<bool>("--dependencies")
        {
            Description = "Show package dependencies."
        };

        var command = new Command("info", "Display detailed information about a FHIR package.")
        {
            packageArg,
            versionsOption,
            dependenciesOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var packageId = parseResult.GetValue(packageArg)!;
            var showVersions = parseResult.GetValue(versionsOption);
            var showDependencies = parseResult.GetValue(dependenciesOption);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                var mgrOptions = globalOpts.BuildManagerOptions();
                var manager = ManagerFactory.Create(mgrOptions);

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Fetching info for: {packageId}");
                }

                PackageListing? listing;

                if (!globalOpts.Quiet && !globalOpts.Json)
                {
                    listing = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Fetching package info...", async _ =>
                            await manager.GetPackageListingAsync(packageId, ct));
                }
                else
                {
                    listing = await manager.GetPackageListingAsync(packageId, ct);
                }

                if (listing is null)
                {
                    CommandHelpers.WriteErrorOutput(globalOpts, $"Package '{packageId}' not found.");
                    return ExitCodes.NotFound;
                }

                // Optionally fetch cached packages for cross-reference
                IReadOnlyList<PackageRecord>? cached = null;
                if (showVersions)
                {
                    cached = await manager.ListCachedAsync(packageId, ct);
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WritePackageInfo(listing, cached);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WritePackageInfo(listing, cached);

                    if (showDependencies && listing.Versions.Count > 0)
                    {
                        WriteDependencies(listing);
                    }
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

    private static void WriteDependencies(PackageListing listing)
    {
        // Show dependencies of the latest version if available
        var latestVersion = listing.LatestVersion;
        if (latestVersion is null || !listing.Versions.TryGetValue(latestVersion, out var versionInfo))
        {
            return;
        }

        var deps = versionInfo.Dependencies;
        if (deps is null or { Count: 0 })
        {
            AnsiConsole.MarkupLine("\n[grey]No dependencies.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Dependencies (v{Markup.Escape(latestVersion)}):[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Package[/]")
            .AddColumn("[bold]Version[/]");

        foreach (var (name, version) in deps)
        {
            table.AddRow(Markup.Escape(name), Markup.Escape(version));
        }

        AnsiConsole.Write(table);
    }

}
