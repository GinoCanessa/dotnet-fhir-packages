// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using FhirPkg.Models;
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
    public async Task Restore_CustomRelativeLockPath_WritesRequestedFile()
    {
        string projectPath = CreateTestProject(
            """{"name":"root.package","version":"1.0.0","dependencies":{}}""");
        string expectedPath = Path.Combine(
            projectPath,
            "locks",
            "custom.lock.json");
        string relativePath = Path.Combine(
            "locks",
            "custom.lock.json");

        (int exitCode, string _, string standardError) =
            await RunCliAsync(
                $"restore \"{projectPath}\" --lock-file \"{relativePath}\" --quiet");

        exitCode.ShouldBe(
            0,
            standardError);
        File.Exists(expectedPath).ShouldBeTrue();
        File.Exists(
            Path.Combine(
                projectPath,
                "fhirpkg.lock.json"))
            .ShouldBeFalse();
        PackageLockFile lockFile =
            await PackageLockFile.LoadAsync(
                expectedPath,
                TestContext.Current.CancellationToken);
        lockFile.SchemaVersion.ShouldBe(
            PackageLockFile.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Restore_CustomAbsoluteLockPath_WritesRequestedFile()
    {
        string projectPath = CreateTestProject(
            """{"name":"root.package","version":"1.0.0","dependencies":{}}""");
        string expectedPath = Path.GetFullPath(
            Path.Combine(
                projectPath,
                "..",
                "locks",
                "absolute.lock.json"));

        (int exitCode, string _, string standardError) =
            await RunCliAsync(
                $"restore \"{projectPath}\" --lock-file \"{expectedPath}\" --quiet");

        exitCode.ShouldBe(
            0,
            standardError);
        File.Exists(expectedPath).ShouldBeTrue();
        File.Exists(
            Path.Combine(
                projectPath,
                "fhirpkg.lock.json"))
            .ShouldBeFalse();
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
