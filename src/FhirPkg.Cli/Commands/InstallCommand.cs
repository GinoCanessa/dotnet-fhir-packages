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
        Argument<string[]> packagesArg = new Argument<string[]>("packages")
        {
            Description = "One or more package directives (e.g. hl7.fhir.r4.core#4.0.1)",
            Arity = ArgumentArity.OneOrMore
        };

        Option<bool> withDependenciesOption = new Option<bool>("--with-dependencies", "-d")
        {
            Description = "Install transitive dependencies"
        };

        Option<bool> overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite packages already in the cache"
        };

        Option<string?> fhirVersionOption = new Option<string?>("--fhir-version", "-f")
        {
            Description = "Preferred FHIR release (R4, R4B, R5, R6)"
        };

        Option<bool?> preReleaseOption = new Option<bool?>("--pre-release")
        {
            Description = "Include pre-release versions"
        };

        Option<bool> noPreReleaseOption = new Option<bool>("--no-pre-release")
        {
            Description = "Exclude pre-release versions"
        };

        Option<string?> registryOption = new Option<string?>("--registry", "-r")
        {
            Description = "Custom registry URL"
        };

        Option<string?> authOption = new Option<string?>("--auth")
        {
            Description = "Authentication header value (e.g. 'Bearer <token>')"
        };

        Option<bool> noCiOption = new Option<bool>("--no-ci")
        {
            Description = "Exclude CI build registries"
        };

        Option<bool> progressOption = new Option<bool>("--progress")
        {
            Description = "Show download progress (default: true)",
            DefaultValueFactory = _ => true
        };

        Command command = new Command("install", "Install one or more FHIR packages into the local cache.")
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
            bool? pre = result.GetValue(preReleaseOption);
            bool noPre = result.GetValue(noPreReleaseOption);
            if (pre == true && noPre)
                result.AddError("Cannot specify both --pre-release and --no-pre-release.");
        });

        command.SetAction(async (parseResult, ct) =>
        {
            string[] packages = parseResult.GetValue(packagesArg)!;
            bool withDeps = parseResult.GetValue(withDependenciesOption);
            bool overwrite = parseResult.GetValue(overwriteOption);
            string? fhirVersion = parseResult.GetValue(fhirVersionOption);
            bool? preRelease = parseResult.GetValue(preReleaseOption);
            bool noPreRelease = parseResult.GetValue(noPreReleaseOption);
            string? registry = parseResult.GetValue(registryOption);
            string? auth = parseResult.GetValue(authOption);
            bool noCi = parseResult.GetValue(noCiOption);
            bool showProgress = parseResult.GetValue(progressOption);

            GlobalOptions globalOpts = parseResult.GetGlobalOptions();

            try
            {
                FhirPackageManagerOptions mgrOptions = globalOpts.BuildManagerOptions();

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

                FhirPackageManager manager = ManagerFactory.Create(mgrOptions);

                InstallOptions installOptions = new InstallOptions
                {
                    IncludeDependencies = withDeps,
                    OverwriteExisting = overwrite,
                    AllowPreRelease = noPreRelease ? false : preRelease ?? true
                };

                if (fhirVersion is not null && Enum.TryParse<FhirRelease>(fhirVersion, ignoreCase: true, out FhirRelease release))
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

                bool hasFailures = results.Any(r =>
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
