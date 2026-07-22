// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;

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
        new("Validates synchronized fhir-pkg release inputs and artifacts.");
}
