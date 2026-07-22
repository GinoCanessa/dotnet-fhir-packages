// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release.Commands;

internal static class ValidateVersionCommand
{
    public static Command Build(ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Option<string> versionOption = new("--version")
        {
            Description = "Release version to validate.",
            Required = true,
        };

        Option<string> sdkIndexUriOption = new("--sdk-index-uri")
        {
            Description = "SDK package index URI.",
            DefaultValueFactory =
                _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib/index.json",
        };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            sdkIndexUriOption,
            "--sdk-index-uri");

        Option<string> cliIndexUriOption = new("--cli-index-uri")
        {
            Description = "CLI package index URI.",
            DefaultValueFactory =
                _ => "https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli/index.json",
        };
        ReleaseCommandSupport.AddAbsoluteUriValidator(
            cliIndexUriOption,
            "--cli-index-uri");

        Command command = new(
            "validate-version",
            "Validate that a synchronized release version is unpublished and newer.")
        {
            versionOption,
            sdkIndexUriOption,
            cliIndexUriOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await ReleaseCommandSupport.ExecuteAsync(
                    services.StandardError,
                    async ct =>
                    {
                        string version = parseResult.GetValue(versionOption)!;
                        string sdkIndexUri =
                            parseResult.GetValue(sdkIndexUriOption)!;
                        string cliIndexUri =
                            parseResult.GetValue(cliIndexUriOption)!;

                        await services.VersionAvailabilityValidator
                            .ValidateAsync(
                                version,
                                sdkIndexUri,
                                cliIndexUri,
                                ct)
                            .ConfigureAwait(false);
                        await services.StandardOutput.WriteLineAsync(
                                $"Validated fresh synchronized release version {version}.")
                            .ConfigureAwait(false);
                        return 0;
                    },
                    cancellationToken)
                .ConfigureAwait(false));

        return command;
    }
}
