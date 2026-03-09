// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using FhirPkg.Registry;
using Spectre.Console;

namespace FhirPkg.Cli.Commands;

/// <summary>
/// Defines the <c>fhir-pkg publish</c> command for publishing a FHIR package
/// tarball to a registry.
/// </summary>
internal static class PublishCommand
{
    /// <summary>
    /// Builds the <c>publish</c> <see cref="Command"/> with all arguments and options.
    /// </summary>
    /// <returns>A fully configured <see cref="Command"/> for the publish subcommand.</returns>
    public static Command Build()
    {
        var tarballArg = new Argument<string>("tarball")
        {
            Description = "Path to the .tgz package tarball to publish."
        };

        var registryOption = new Option<string>("--registry", "-r")
        {
            Description = "Registry URL to publish to.",
            Required = true
        };

        var authOption = new Option<string>("--auth")
        {
            Description = "Authentication header value (e.g. 'Bearer <token>').",
            Required = true
        };

        var command = new Command("publish", "Publish a FHIR package tarball to a registry.")
        {
            tarballArg,
            registryOption,
            authOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var tarball = parseResult.GetValue(tarballArg);
            var registry = parseResult.GetValue(registryOption)!;
            var auth = parseResult.GetValue(authOption)!;

            var globalOpts = parseResult.GetGlobalOptions();

            try
            {
                if (!File.Exists(tarball))
                {
                    WriteErrorOutput(globalOpts, $"Tarball not found: {tarball}");
                    return ExitCodes.NotFound;
                }

                var mgrOptions = globalOpts.BuildManagerOptions();
                var manager = new FhirPackageManager(mgrOptions);

                var endpoint = new RegistryEndpoint
                {
                    Url = registry,
                    Type = RegistryType.FhirNpm,
                    AuthHeaderValue = auth
                };

                if (globalOpts.Verbose)
                {
                    ConsoleOutput.WriteVerbose($"Publishing {tarball} to {registry}...");
                }

                PublishResult result;

                if (!globalOpts.Quiet && !globalOpts.Json)
                {
                    result = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Publishing...", async _ =>
                            await manager.PublishAsync(tarball, endpoint, ct));
                }
                else
                {
                    result = await manager.PublishAsync(tarball, endpoint, ct);
                }

                if (globalOpts.Json)
                {
                    JsonOutput.WritePublishResult(result);
                }
                else if (!globalOpts.Quiet)
                {
                    ConsoleOutput.WritePublishResult(result);
                }

                return result.Success ? ExitCodes.Success : ExitCodes.GeneralError;
            }
            catch (HttpRequestException ex)
            {
                WriteErrorOutput(globalOpts, $"Network error: {ex.Message}");
                return ExitCodes.NetworkError;
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteErrorOutput(globalOpts, $"Authentication error: {ex.Message}");
                return ExitCodes.AuthError;
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
