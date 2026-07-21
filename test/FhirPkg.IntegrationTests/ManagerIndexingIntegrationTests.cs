// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ManagerIndexingIntegrationTests :
    IntegrationTestBase
{
    [Fact]
    public async Task ManagerIndexing_FreshProcessesPersistDiscoverAndReadResources()
    {
        const string packageName = "resource.package";
        const string packageVersion = "1.0.0";
        const string canonicalUrl =
            "https://example.test/StructureDefinition/resource";
        string archivePath =
            Path.Combine(
                TempCacheDir,
                "resource-package.tgz");
        await using (Stream archive = CreateTestTarball(
            packageName,
            packageVersion,
            new Dictionary<string, string>
            {
                ["resource.json"] =
                    $$"""{"resourceType":"StructureDefinition","id":"resource-profile","url":"{{canonicalUrl}}","kind":"resource","derivation":"constraint","type":"Patient"}""",
            }))
        await using (FileStream destination = new(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read))
        {
            await archive.CopyToAsync(
                destination,
                TestContext.Current.CancellationToken);
        }

        HostExecution install =
            await RunHostAsync(
                "install",
                "--cache", TempCacheDir,
                "--archive", archivePath,
                "--name", packageName,
                "--version", packageVersion);
        install.ExitCode.ShouldBe(
            0,
            install.StandardError);
        string indexPath = Path.Combine(
            TempCacheDir,
            $"{packageName}#{packageVersion}",
            "package",
            ".index.json");
        File.Exists(indexPath).ShouldBeFalse();

        HostResult first =
            await FindResourceAsync(
                packageName,
                packageVersion,
                canonicalUrl,
                "first-result.json");
        byte[] persistedBytes =
            await File.ReadAllBytesAsync(
                indexPath,
                TestContext.Current.CancellationToken);
        HostResult restarted =
            await FindResourceAsync(
                packageName,
                packageVersion,
                canonicalUrl,
                "restart-result.json");
        byte[] restartedBytes =
            await File.ReadAllBytesAsync(
                indexPath,
                TestContext.Current.CancellationToken);

        first.ResourceFound.ShouldBe(true);
        first.ResourceId.ShouldBe("resource-profile");
        first.ParsedResourceId.ShouldBe(
            "resource-profile");
        first.ResourcePackage.ShouldBe(packageName);
        first.ResourceVersion.ShouldBe(
            packageVersion);
        first.ResourcePath.ShouldBe(
            "resource.json");
        restarted.ShouldBe(first);
        restartedBytes.ShouldBe(persistedBytes);
    }

    private async Task<HostResult> FindResourceAsync(
        string packageName,
        string packageVersion,
        string canonicalUrl,
        string resultFileName)
    {
        string resultPath =
            Path.Combine(
                TempCacheDir,
                resultFileName);
        HostExecution execution =
            await RunHostAsync(
                "manager-find",
                "--cache", TempCacheDir,
                "--canonical", canonicalUrl,
                "--scope",
                $"{packageName}#{packageVersion}",
                "--result", resultPath);
        execution.ExitCode.ShouldBe(
            0,
            execution.StandardError);
        string json =
            await File.ReadAllTextAsync(
                resultPath,
                TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<HostResult>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })
            ?? throw new InvalidDataException(
                "The process host returned an empty result.");
    }

    private static async Task<HostExecution> RunHostAsync(
        string command,
        params string[] arguments)
    {
        string hostPath = GetHostPath();
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add(command);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = new()
        {
            StartInfo = startInfo,
        };
        process.Start();
        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken);
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync(
                TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(
            TestContext.Current.CancellationToken);
        return new HostExecution(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string GetHostPath()
    {
        DirectoryInfo targetFrameworkDirectory =
            new(AppContext.BaseDirectory);
        string configuration =
            targetFrameworkDirectory.Parent?.Name
            ?? "Debug";
        string targetFramework =
            $"net{Environment.Version.Major}.0";
        string testRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                ".."));
        string hostPath = Path.Combine(
            testRoot,
            "FhirPkg.ProcessTestHost",
            "bin",
            configuration,
            targetFramework,
            "FhirPkg.ProcessTestHost.dll");
        File.Exists(hostPath).ShouldBeTrue(
            $"Process test host was not built at '{hostPath}'.");
        return hostPath;
    }

    private sealed record HostExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record HostResult
    {
        public bool Success { get; init; }
        public bool? ResourceFound { get; init; }
        public string? ResourceId { get; init; }
        public string? ResourcePackage { get; init; }
        public string? ResourceVersion { get; init; }
        public string? ResourcePath { get; init; }
        public string? ParsedResourceId { get; init; }
    }
}
