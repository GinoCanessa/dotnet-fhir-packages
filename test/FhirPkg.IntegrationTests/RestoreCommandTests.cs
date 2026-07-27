// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RestoreCommandTests : IntegrationTestBase
{
    private const int TimeoutSeconds = 60 * 10;

    private static readonly string s_cliAssemblyPath =
        Path.Combine(
            AppContext.BaseDirectory,
            "FhirPkg.Cli.dll");

    [Fact]
    public async Task Restore_NegativeMaxDepth_ExitsInvalidArgs()
    {
        string projectPath = CreateTestProject(
            """{"name":"root.package","version":"1.0.0","dependencies":{}}""");

        (int exitCode, string standardOutput, string _) =
            await RunCliAsync(
                $"restore \"{projectPath}\" --max-depth -1");

        exitCode.ShouldBe(2);
        standardOutput.ShouldContain("--max-depth");
    }

    [Theory]
    [InlineData("R99")]
    [InlineData("2")]
    public async Task Restore_InvalidFhirRelease_ExitsInvalidArgs(
        string release)
    {
        string projectPath = CreateTestProject(
            """{"name":"root.package","version":"1.0.0","dependencies":{}}""");

        (int exitCode, string standardOutput, string _) =
            await RunCliAsync(
                $"restore \"{projectPath}\" --fhir-version {release}");

        exitCode.ShouldBe(2);
        standardOutput.ShouldContain(release);
    }

    [Fact]
    public async Task Restore_HelpOmitsRemovedLockOptions()
    {
        (int exitCode, string standardOutput, string standardError) =
            await RunCliAsync("restore --help");

        exitCode.ShouldBe(
            0,
            standardError);
        standardOutput.ShouldNotContain("--lock-file");
        standardOutput.ShouldNotContain("--no-lock");
    }

    [Theory]
    [InlineData("--lock-file=custom.lock.json", "--lock-file")]
    [InlineData("-l custom.lock.json", "-l")]
    [InlineData("--no-lock", "--no-lock")]
    public async Task Restore_RemovedLockOptionsAreRejected(
        string optionArguments,
        string errorToken)
    {
        string projectPath = CreateTestProject(
            """{"name":"root.package","version":"1.0.0","dependencies":{}}""");

        (int exitCode, string _, string standardError) =
            await RunCliAsync(
                $"restore \"{projectPath}\" {optionArguments}");

        exitCode.ShouldNotBe(0);
        standardError.ShouldContain(errorToken);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)>
        RunCliAsync(string arguments)
    {
        string allArguments =
            $"{arguments} --package-cache-folder \"{TempCacheDir}\"";
        using CancellationTokenSource source =
            new(TimeSpan.FromSeconds(TimeoutSeconds));
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments =
                    $"\"{s_cliAssemblyPath}\" {allArguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync(source.Token);
        Task<string> standardError =
            process.StandardError.ReadToEndAsync(source.Token);

        try
        {
            await Task.WhenAll(
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(source.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort process cleanup.
            }

            throw new TimeoutException(
                $"CLI restore did not complete within {TimeoutSeconds} seconds.");
        }

        return (
            process.ExitCode,
            standardOutput.Result,
            standardError.Result);
    }
}
