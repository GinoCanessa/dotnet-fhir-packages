// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ManagerIndexingIntegrationTests :
    IntegrationTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagerIndexing_FreshProcessesPersistDiscoverAndReadResources(
        bool rootLayout,
        bool unmodified)
    {
        const string packageName = "resource.package";
        const string packageVersion = "1.0.0";
        const string canonicalUrl =
            "https://example.test/StructureDefinition/resource";
        string archivePath =
            Path.Combine(
                TempCacheDir,
                "resource-package.tgz");
        string prefix = rootLayout ? string.Empty : "package/";
        Dictionary<string, byte[]> members = new(StringComparer.Ordinal)
        {
            [$"{prefix}package.json"] = Encoding.UTF8.GetBytes(
                $$"""{"name":"{{packageName}}","version":"{{packageVersion}}"}"""),
            [$"{prefix}resource.json"] = Encoding.UTF8.GetBytes(
                $$"""{"resourceType":"StructureDefinition","id":"resource-profile","url":"{{canonicalUrl}}","kind":"resource","derivation":"constraint","type":"Patient"}"""),
            ["README.txt"] = Encoding.UTF8.GetBytes("distributed root file"),
            ["other/.index.json"] = Encoding.UTF8.GetBytes("opaque ancillary index"),
            ["package/other/sentinel.txt"] = [0, 255, 128],
        };
        string[] emptyDirectories = ["empty/", $"{prefix}content-empty/"];
        using (FileStream destination = new(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read))
        using (GZipStream gzip = new(destination, CompressionLevel.Fastest))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax))
        {
            foreach ((string path, byte[] bytes) in members)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes),
                });
            }
            foreach (string directory in emptyDirectories)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory));
        }

        List<string> installArguments =
        [
            "--cache", TempCacheDir,
            "--archive", archivePath,
            "--name", packageName,
            "--version", packageVersion,
        ];
        if (unmodified)
            installArguments.Add("--unmodified");
        HostExecution install =
            await RunHostAsync(
                "install",
                [.. installArguments]);
        install.ExitCode.ShouldBe(
            0,
            install.StandardError);
        PackageReference reference = new(packageName, packageVersion);
        string target = Path.Combine(TempCacheDir, reference.FhirDirective);
        string contentPath = rootLayout && unmodified ? target : Path.Combine(target, "package");
        string indexPath = Path.Combine(contentPath, ".index.json");
        using DiskPackageCache cache = new(TempCacheDir);
        PackageRecord record = (await cache.GetPackageAsync(
            reference, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        record.Reference.ShouldBe(reference);
        record.DirectoryPath.ShouldBe(target);
        record.ContentPath.ShouldBe(contentPath);
        record.SizeBytes.ShouldBe(members.Values.Sum(bytes => bytes.LongLength));
        string wrapper = rootLayout && !unmodified ? "package/" : string.Empty;
        Dictionary<string, byte[]> expectedFiles = members.ToDictionary(
            member => wrapper + member.Key, member => member.Value, StringComparer.Ordinal);
        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (string path in members.Keys.Concat(emptyDirectories))
        {
            string[] segments = (wrapper + path).TrimEnd('/').Split('/');
            int depth = path.EndsWith('/') ? segments.Length : segments.Length - 1;
            for (int index = 1; index <= depth; index++)
                expectedDirectories.Add(string.Join("/", segments.Take(index)));
        }
        File.Exists(indexPath).ShouldBeFalse();
        AssertPayload(target, expectedFiles, expectedDirectories, indexPath, persisted: false);

        HostResult first =
            await FindResourceAsync(
                packageName,
                packageVersion,
                canonicalUrl,
                "first-result.json",
                unmodified);
        if (unmodified)
        {
            File.Exists(indexPath).ShouldBeFalse();
            AssertPayload(target, expectedFiles, expectedDirectories, indexPath, persisted: false);
            HostResult unmodifiedRestart = await FindResourceAsync(
                packageName, packageVersion, canonicalUrl, "unmodified-restart.json", unmodified: true);
            unmodifiedRestart.ShouldBe(first);
            File.Exists(indexPath).ShouldBeFalse();
            (await cache.GetIndexAsync(reference, TestContext.Current.CancellationToken)).ShouldBeNull();
            AssertPayload(target, expectedFiles, expectedDirectories, indexPath, persisted: false);

            HostResult ordinary = await FindResourceAsync(
                packageName, packageVersion, canonicalUrl, "ordinary-result.json");
            ordinary.ShouldBe(first);
        }
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
        AssertPayload(target, expectedFiles, expectedDirectories, indexPath, persisted: true);
        (await cache.GetPackageAsync(reference, TestContext.Current.CancellationToken))!
            .ContentPath.ShouldBe(contentPath);
    }

    private static void AssertPayload(
        string target,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlySet<string> expectedDirectories,
        string indexPath,
        bool persisted)
    {
        string activeIndex = Path.GetRelativePath(target, indexPath).Replace('\\', '/');
        IEnumerable<string> files = persisted ? expectedFiles.Keys.Append(activeIndex) : expectedFiles.Keys;
        Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(files.OrderBy(path => path, StringComparer.Ordinal));
        Directory.GetDirectories(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedDirectories.OrderBy(path => path, StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expectedFiles)
            File.ReadAllBytes(Path.Combine(target, path)).ShouldBe(bytes, path);
    }

    private async Task<HostResult> FindResourceAsync(
        string packageName,
        string packageVersion,
        string canonicalUrl,
        string resultFileName,
        bool unmodified = false)
    {
        string resultPath =
            Path.Combine(
                TempCacheDir,
                resultFileName);
        List<string> arguments =
        [
            "--cache", TempCacheDir,
            "--canonical", canonicalUrl,
            "--scope", $"{packageName}#{packageVersion}",
            "--result", resultPath,
        ];
        if (unmodified)
            arguments.Add("--unmodified");
        HostExecution execution = await RunHostAsync("manager-find", [.. arguments]);
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
