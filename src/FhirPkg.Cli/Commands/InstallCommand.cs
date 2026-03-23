// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using FhirPkg.Registry;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg install</c> command for installing one or more FHIR packages
/// from a registry into the local package cache.
/// </summary>
internal static class InstallCommand
{
    /// <summary>
    /// Builds the <c>install</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the install subcommand.</returns>
    public static Command Build()
    {
        var packagesArg = new Argument<string[]>("packages")
        {
            Description = "One or more package directives (e.g. hl7.fhir.r4.core#4.0.1)",
            Arity = ArgumentArity.OneOrMore
        };

        var withDependenciesOption = new Option<bool>("--with-dependencies", "-d")
        {
            Description = "Install transitive dependencies"
        };

        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite packages already in the cache"
        };

        var fhirVersionOption = new Option<string?>("--fhir-version", "-f")
        {
            Description = "Preferred FHIR release (R4, R4B, R5, R6)"
        };

        var preReleaseOption = new Option<bool?>("--pre-release")
        {
            Description = "Include pre-release versions"
        };

        var noPreReleaseOption = new Option<bool>("--no-pre-release")
        {
            Description = "Exclude pre-release versions"
        };

        var registryOption = new Option<string?>("--registry", "-r")
        {
            Description = "Custom registry URL"
        };

        var authOption = new Option<string?>("--auth")
        {
            Description = "Authentication header value (e.g. 'Bearer <token>')"
        };

        var noCiOption = new Option<bool>("--no-ci")
        {
            Description = "Exclude CI build registries"
        };

        var progressOption = new Option<bool>("--progress")
        {
            Description = "Show download progress (default: true)",
            DefaultValueFactory = _ => true
        };

        var command = new Command("install", "Install one or more FHIR packages into the local cache.")
        {
            packagesArg,
            withDependenciesOption,
            overwriteOption,
            fhirVersionOption,
            preReleaseOption,
            noPreReleaseOption,
            registryOption,
            authOption,
            noCiOption,
            progressOption
        };

        command.Validators.Add(result =>
        {
            var pre = result.GetValue(preReleaseOption);
            var noPre = result.GetValue(noPreReleaseOption);
            if (pre == true && noPre)
                result.AddError("Cannot specify both --pre-release and --no-pre-release.");
        });

        command.SetAction(async (parseResult, ct) =>
        {
            var packages = parseResult.GetValue(packagesArg);
            var withDeps = parseResult.GetValue(withDependenciesOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            var fhirVersion = parseResult.GetValue(fhirVersionOption);
            var preRelease = parseResult.GetValue(preReleaseOption);
            var noPreRelease = parseResult.GetValue(noPreReleaseOption);
            var registry = parseResult.GetValue(registryOption);
            var auth = parseResult.GetValue(authOption);
            var noCi = parseResult.GetValue(noCiOption);
            var showProgress = parseResult.GetValue(progressOption);

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                var mgrOptions = globalOpts.BuildManagerOptions();

                if (registry is not null)
                {
                    mgrOptions.Registries.Insert(0, new RegistryEndpoint
                    {
                        Url = registry,
                        Type = RegistryType.FhirNpm,
                        AuthHeaderValue = auth
                    });
                }

                if (noCi)
                {
                    mgrOptions.IncludeCiBuilds = false;
                }

                var manager = ManagerFactory.Create(mgrOptions);

                var installOptions = new InstallOptions
                {
                    IncludeDependencies = withDeps,
                    OverwriteExisting = overwrite,
                    AllowPreRelease = noPreRelease ? false : preRelease ?? true
                };

                if (fhirVersion is not null && Enum.TryParse<FhirRelease>(fhirVersion, ignoreCase: true, out var release))
                {
                    installOptions.PreferredFhirRelease = release;
                }

                if (showProgress && !globalOpts.Quiet && !globalOpts.Json)
                {
                    installOptions.Progress = new Progress<PackageProgress>(p =>
                    {
                        if (globalOpts.Verbose)
                        {
                            ConsoleOutput.WriteVerbose(
                                $"[{p.Phase}] {p.PackageId}: {p.PercentComplete:P0}");
                        }
                    });
                }

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Installing {packages.Length} package(s)...");
                }

                IReadOnlyList<PackageInstallResult> results;

                if (!globalOpts.Quiet && !globalOpts.Json && showProgress)
                {
                    results = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Installing packages...", async _ =>
                            await manager.InstallManyAsync(packages, installOptions, ct));
                }
                else
                {
                    results = await manager.InstallManyAsync(packages, installOptions, ct);
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WriteInstallResults(results);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WriteInstallResults(results);
                }

                var hasFailures = results.Any(r =>
                    r.Status is PackageInstallStatus.Failed or PackageInstallStatus.NotFound);

                return hasFailures ? ExitCodes.NotFound : ExitCodes.Success;
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
