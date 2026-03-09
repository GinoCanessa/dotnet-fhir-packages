// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public class CliIntegrationTests : IntegrationTestBase
{
    private static readonly string CliProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "FhirPkg.Cli", "FhirPkg.Cli.csproj"));

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCli(params string[] args)
    {
        var allArgs = string.Join(" ", args) + $" --package-cache-folder \"{TempCacheDir}\"";
        return await RunCliRaw(allArgs);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliRaw(string allArgs)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{CliProjectPath}\" -- {allArgs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public async Task List_EmptyCache_ExitCode0()
    {
        var (exitCode, _, _) = await RunCli("list");

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task List_JsonOutput_ValidJson()
    {
        var (exitCode, stdout, _) = await RunCli("list", "--json");

        exitCode.ShouldBe(0);

        // The JSON output should be parseable
        var act = () => JsonDocument.Parse(stdout);
        Should.NotThrow(act, "list --json should produce valid JSON");
    }

    [Fact]
    public async Task Help_ShowsHelp()
    {
        var (exitCode, stdout, _) = await RunCli("--help");

        exitCode.ShouldBe(0);
        stdout.ShouldContain("fhir-pkg");
    }

    [Fact]
    public async Task PackageCacheFolderOption_UsesSpecifiedPath()
    {
        var (exitCode, _, _) = await RunCliRaw($"list --package-cache-folder \"{TempCacheDir}\"");

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidCommand_ExitCodeNonZero()
    {
        var (exitCode, _, _) = await RunCli("nonexistent-command-xyz");

        exitCode.ShouldNotBe(0);
    }
}
