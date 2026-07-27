// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Release.Commands;
using FhirPkg.Release.Infrastructure;

namespace FhirPkg.Release;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = BuildRootCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync(
            new InvocationConfiguration(),
            CancellationToken.None);
    }

    internal static RootCommand BuildRootCommand() =>
        BuildRootCommand(ReleaseCommandServices.CreateDefault());

    internal static RootCommand BuildRootCommand(
        ReleaseCommandServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        RootCommand rootCommand = new(
            "Validates synchronized fhir-pkg release inputs and artifacts.");

        rootCommand.Add(ValidateInputsCommand.Build(services));
        rootCommand.Add(ValidateVersionCommand.Build(services));
        rootCommand.Add(ValidateCandidateCommand.Build(services));
        rootCommand.Add(InspectPublicationCommand.Build(services));
        rootCommand.Add(ValidatePublishedPackageCommand.Build(services));

        return rootCommand;
    }
}
