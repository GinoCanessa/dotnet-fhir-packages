// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public class CliIntegrationTests : IntegrationTestBase
{
    private const int _timeoutSeconds = 60 * 10;

    private static readonly string CliProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "FhirPkg.Cli", "FhirPkg.Cli.csproj"));

    private static readonly string TargetFramework =
        $"net{Environment.Version.Major}.{Environment.Version.Minor}";

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCli(params string[] args)
    {
        string allArgs = string.Join(" ", args) + $" --package-cache-folder \"{TempCacheDir}\"";
        return await RunCliRaw(allArgs);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliRaw(string allArgs)
    {
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

        Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{CliProjectPath}\" --framework {TargetFramework} -- {allArgs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();

        // Read stdout and stderr concurrently to avoid deadlock when pipe buffers fill
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException(
                $"CLI process did not complete within the {_timeoutSeconds}-second timeout. Args: {allArgs}");
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    [Fact]
    public async Task List_EmptyCache_ExitCode0()
    {
        (int exitCode, string _, string _) = await RunCli("list");

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task List_JsonOutput_ValidJson()
    {
        (int exitCode, string? stdout, string _) = await RunCli("list", "--json");

        exitCode.ShouldBe(0);

        // The JSON output should be parseable
        Func<JsonDocument> act = () => JsonDocument.Parse(stdout);
        Should.NotThrow(act, "list --json should produce valid JSON");
    }

    [Fact]
    public async Task Help_ShowsHelp()
    {
        (int exitCode, string? stdout, string _) = await RunCli("--help");

        exitCode.ShouldBe(0);
        stdout.ShouldContain("fhir-pkg");
    }

    [Fact]
    public async Task PackageCacheFolderOption_UsesSpecifiedPath()
    {
        (int exitCode, string _, string _) = await RunCliRaw($"list --package-cache-folder \"{TempCacheDir}\"");

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidCommand_ExitCodeNonZero()
    {
        (int exitCode, string _, string _) = await RunCli("nonexistent-command-xyz");

        exitCode.ShouldNotBe(0);
    }
}
