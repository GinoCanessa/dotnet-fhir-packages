// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Cli.Commands;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public class CliIntegrationTests : IntegrationTestBase
{
    private const int _timeoutSeconds = 60 * 10;

    private static readonly string CliAssemblyPath =
        Path.Combine(
            AppContext.BaseDirectory,
            "FhirPkg.Cli.dll");

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
                Arguments = $"\"{CliAssemblyPath}\" {allArgs}",
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
    public async Task List_PopulatedCache_PreservesSummaryOutput()
    {
        DateTimeOffset installedAt =
            DateTimeOffset.Parse(
                "2026-07-21T15:30:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
        await SeedCachedPackageAsync(
            "list.populated",
            "1.2.3",
            installedAt,
            fhirVersion: "4.0.1",
            sizeBytes: 2048,
            persistIndex: true);

        (int consoleExitCode, string consoleOutput, string _) =
            await RunCli("list", "--show-size");
        (int jsonExitCode, string jsonOutput, string _) =
            await RunCli("list", "--json", "--show-size");

        consoleExitCode.ShouldBe(0);
        consoleOutput.ShouldContain("list.populated");
        consoleOutput.ShouldContain("1.2.3");
        consoleOutput.ShouldContain("4.0.1");
        consoleOutput.ShouldContain("2026-07-21 15:30");
        consoleOutput.ShouldContain("2.0 KB");
        jsonExitCode.ShouldBe(0);

        using JsonDocument document =
            JsonDocument.Parse(jsonOutput);
        JsonElement root = document.RootElement;
        root.GetProperty("count").GetInt32().ShouldBe(1);
        JsonElement package = root
            .GetProperty("packages")
            .EnumerateArray()
            .ShouldHaveSingleItem();
        package.GetProperty("name").GetString()
            .ShouldBe("list.populated");
        package.GetProperty("version").GetString()
            .ShouldBe("1.2.3");
        package.GetProperty("fhirVersion").GetString()
            .ShouldBe("4.0.1");
        package.GetProperty("installedAt").GetDateTimeOffset()
            .ShouldBe(installedAt);
        package.GetProperty("size").GetInt64().ShouldBe(2048);
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

    [Fact]
    public void CleanSelection_CiOnlyMatchesOnlyCurrentAliases()
    {
        DateTimeOffset now =
            DateTimeOffset.Parse(
                "2026-07-18T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
        string[] versions =
        [
            "1.0.0",
            "1.0.0-ballot",
            "1.0.0-draft",
            "1.0.0-snapshot",
            "1.0.0-cibuild",
            "dev",
            "current",
            "CURRENT",
            "current$main",
            "Current$Feature",
        ];
        PackageRecord[] packages = versions
            .Select(version =>
                CreateRecord(
                    version,
                    now.AddDays(-100)))
            .ToArray();

        IReadOnlyList<PackageRecord> selected =
            CleanCommand.SelectPackagesForRemoval(
                packages,
                ciOnly: true,
                olderThanDays: null,
                now);

        selected
            .Select(package =>
                package.Reference.Version)
            .ShouldBe(
            [
                "current",
                "CURRENT",
                "current$main",
                "Current$Feature",
            ]);
    }

    [Fact]
    public void CleanSelection_AgeIsStrictSafeAndPreservesUnknown()
    {
        DateTimeOffset now =
            DateTimeOffset.Parse(
                "2026-07-18T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
        PackageRecord old =
            CreateRecord(
                "1.0.0",
                now.AddDays(-30).AddTicks(-1));
        PackageRecord exact =
            CreateRecord(
                "2.0.0",
                now.AddDays(-30));
        PackageRecord recent =
            CreateRecord(
                "3.0.0",
                now.AddDays(-30).AddTicks(1));
        PackageRecord unknown =
            CreateRecord(
                "4.0.0",
                installedAt: null);

        IReadOnlyList<PackageRecord> selected =
            CleanCommand.SelectPackagesForRemoval(
                [old, exact, recent, unknown],
                ciOnly: false,
                olderThanDays: 30,
                now);
        IReadOnlyList<PackageRecord> zeroDay =
            CleanCommand.SelectPackagesForRemoval(
                [
                    CreateRecord(
                        "5.0.0",
                        now.AddTicks(-1)),
                    CreateRecord(
                        "6.0.0",
                        now),
                    unknown,
                ],
                ciOnly: false,
                olderThanDays: 0,
                now);
        IReadOnlyList<PackageRecord> hugeAge =
            CleanCommand.SelectPackagesForRemoval(
                [old],
                ciOnly: false,
                olderThanDays: int.MaxValue,
                now);

        selected.ShouldBe([old]);
        zeroDay.Select(package =>
                package.Reference.Version)
            .ShouldBe(["5.0.0"]);
        hugeAge.ShouldBeEmpty();
    }

    [Fact]
    public void CleanSelection_CombinesCiAndAgeWithLogicalAnd()
    {
        DateTimeOffset now =
            DateTimeOffset.Parse(
                "2026-07-18T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
        PackageRecord oldCi =
            CreateRecord(
                "current",
                now.AddDays(-31));
        PackageRecord recentCi =
            CreateRecord(
                "current$main",
                now.AddDays(-29));
        PackageRecord oldStable =
            CreateRecord(
                "1.0.0",
                now.AddDays(-31));

        IReadOnlyList<PackageRecord> selected =
            CleanCommand.SelectPackagesForRemoval(
                [oldCi, recentCi, oldStable],
                ciOnly: true,
                olderThanDays: 30,
                now);

        selected.ShouldBe([oldCi]);
        Should.Throw<ArgumentOutOfRangeException>(
            () => CleanCommand.SelectPackagesForRemoval(
                [oldCi],
                ciOnly: false,
                olderThanDays: -1,
                now));
    }

    [Fact]
    public async Task Clean_CiOnly_RemovesOnlyCurrentAliases()
    {
        DateTimeOffset installedAt =
            DateTimeOffset.UtcNow.AddDays(-60);
        string[] versions =
        [
            "1.0.0",
            "1.0.0-ballot",
            "1.0.0-draft",
            "1.0.0-snapshot",
            "1.0.0-cibuild",
            "dev",
            "current",
            "current$main",
        ];
        foreach (string version in versions)
        {
            await SeedCachedPackageAsync(
                "clean.matrix",
                version,
                installedAt);
        }

        (int exitCode, string _, string _) =
            await RunCli(
                "clean",
                "--ci-only",
                "--force");

        exitCode.ShouldBe(0);
        (await ListCachedVersionsAsync(
                "clean.matrix"))
            .ShouldBe(
            [
                "1.0.0",
                "1.0.0-ballot",
                "1.0.0-cibuild",
                "1.0.0-draft",
                "1.0.0-snapshot",
                "dev",
            ]);
    }

    [Fact]
    public async Task Clean_OlderThan_UsesInstallAgeAndPreservesUnknown()
    {
        DateTimeOffset now =
            DateTimeOffset.UtcNow;
        await SeedCachedPackageAsync(
            "clean.age",
            "1.0.0",
            now.AddDays(-40));
        await SeedCachedPackageAsync(
            "clean.age",
            "2.0.0",
            now);
        await SeedCachedPackageAsync(
            "clean.age",
            "3.0.0",
            installedAt: null);
        await SeedCachedPackageAsync(
            "clean.age",
            "4.0.0",
            now.AddDays(-100));
        await CorruptInstallTimestampAsync(
            "clean.age",
            "4.0.0");

        (int exitCode, string _, string _) =
            await RunCli(
                "clean",
                "--older-than",
                "30",
                "--force");

        exitCode.ShouldBe(0);
        (await ListCachedVersionsAsync(
                "clean.age"))
            .ShouldBe(
            [
                "2.0.0",
                "3.0.0",
                "4.0.0",
            ]);
    }

    [Fact]
    public async Task Clean_CombinedFiltersUseLogicalAnd()
    {
        DateTimeOffset now =
            DateTimeOffset.UtcNow;
        await SeedCachedPackageAsync(
            "clean.combined",
            "current",
            now.AddDays(-40));
        await SeedCachedPackageAsync(
            "clean.combined",
            "current$main",
            now);
        await SeedCachedPackageAsync(
            "clean.combined",
            "1.0.0",
            now.AddDays(-40));
        await SeedCachedPackageAsync(
            "clean.combined",
            "current$unknown",
            installedAt: null);

        (int exitCode, string _, string _) =
            await RunCli(
                "clean",
                "--ci-only",
                "--older-than",
                "30",
                "--force");

        exitCode.ShouldBe(0);
        (await ListCachedVersionsAsync(
                "clean.combined"))
            .ShouldBe(
            [
                "1.0.0",
                "current$main",
                "current$unknown",
            ]);
    }

    [Fact]
    public async Task Clean_NegativeOlderThanIsRejectedWithoutRemoval()
    {
        await SeedCachedPackageAsync(
            "clean.negative",
            "1.0.0",
            DateTimeOffset.UtcNow.AddDays(-100));

        (int exitCode, string _, string stderr) =
            await RunCli(
                "clean",
                "--older-than",
                "-1",
                "--force");

        exitCode.ShouldNotBe(0);
        stderr.ShouldContain(
            "--older-than must be zero or greater");
        (await ListCachedVersionsAsync(
                "clean.negative"))
            .ShouldBe(["1.0.0"]);
    }

    [Fact]
    public async Task Clean_MalformedOlderThanIsParseErrorWithoutCrash()
    {
        await SeedCachedPackageAsync(
            "clean.malformed",
            "1.0.0",
            DateTimeOffset.UtcNow.AddDays(-100));

        (int exitCode, string _, string stderr) =
            await RunCli(
                "clean",
                "--older-than",
                "abc",
                "--force");

        exitCode.ShouldNotBe(0);
        stderr.ShouldNotContain(
            "Unhandled exception");
        (await ListCachedVersionsAsync(
                "clean.malformed"))
            .ShouldBe(["1.0.0"]);
    }

    [Fact]
    public async Task Clean_ConcurrentRefreshIsNotRemoved()
    {
        DateTimeOffset now =
            DateTimeOffset.UtcNow;
        await SeedCachedPackageAsync(
            "clean.refresh",
            "1.0.0",
            now.AddDays(-100));
        FhirPackageManagerOptions options = new()
        {
            CachePath = TempCacheDir,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            Registries = [],
        };
        using FhirPackageManager manager =
            new(options);
        PackageRecord selected =
            CleanCommand.SelectPackagesForRemoval(
                await manager.ListCachedSummariesAsync(
                    cancellationToken:
                        TestContext.Current.CancellationToken),
                ciOnly: false,
                olderThanDays: 30,
                now)
            .Single();
        using (DiskPackageCache refreshingCache =
               new(TempCacheDir))
        {
            CacheMetadataEntry current =
                (await refreshingCache.GetMetadataAsync(
                    TestContext.Current.CancellationToken))
                .Packages[
                    PackageCacheKey.Create(
                            selected.Reference)
                        .MetadataKey];
            await refreshingCache.UpdateMetadataAsync(
                selected.Reference,
                current with
                {
                    DownloadDateTime =
                        now.UtcDateTime,
                },
                TestContext.Current.CancellationToken);
        }

        bool removed =
            await manager.RemoveIfUnchangedAsync(
                selected,
                TestContext.Current.CancellationToken);

        removed.ShouldBeFalse();
        (await ListCachedVersionsAsync(
                "clean.refresh"))
            .ShouldBe(["1.0.0"]);
    }

    private async Task SeedCachedPackageAsync(
        string packageName,
        string version,
        DateTimeOffset? installedAt,
        string? fhirVersion = null,
        long? sizeBytes = null,
        bool persistIndex = false)
    {
        PackageReference reference = new(
            packageName,
            version);
        PackageCacheKey cacheKey =
            PackageCacheKey.Create(reference);
        string contentPath = Path.Combine(
            cacheKey.GetPackageDirectoryPath(
                TempCacheDir),
            "package");
        Directory.CreateDirectory(contentPath);
        VersionType versionType =
            PackageDirective.ClassifyVersion(version);
        string manifestVersion = versionType
            is VersionType.CiBuild
                or VersionType.CiBuildBranch
                or VersionType.LocalBuild
            ? "1.0.0"
            : version;
        await File.WriteAllTextAsync(
            Path.Combine(
                contentPath,
                "package.json"),
            JsonSerializer.Serialize(
                new
                {
                    name = packageName,
                    version = manifestVersion,
                    fhirVersions = fhirVersion is null
                        ? null
                        : new[] { fhirVersion },
                }),
            TestContext.Current.CancellationToken);
        if (persistIndex)
        {
            await File.WriteAllTextAsync(
                Path.Combine(contentPath, "patient.json"),
                """{"resourceType":"Patient","id":"listed"}""",
                TestContext.Current.CancellationToken);
            PackageIndex index = new()
            {
                IndexVersion = 2,
                Files =
                [
                    new ResourceIndexEntry
                    {
                        Filename = "patient.json",
                        ResourceType = "Patient",
                        Id = "listed"
                    }
                ]
            };
            await File.WriteAllTextAsync(
                Path.Combine(contentPath, ".index.json"),
                JsonSerializer.Serialize(index),
                TestContext.Current.CancellationToken);
        }

        if (installedAt is null)
            return;

        using DiskPackageCache cache =
            new(TempCacheDir);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime =
                    installedAt.Value.UtcDateTime,
                SizeBytes = sizeBytes,
            },
            TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<string>>
        ListCachedVersionsAsync(
            string packageName)
    {
        using DiskPackageCache cache =
            new(TempCacheDir);
        IReadOnlyList<PackageRecord> packages =
            await cache.ListPackagesAsync(
                packageName,
                ct:
                    TestContext.Current.CancellationToken);
        return packages
            .Where(package =>
                package.Reference.Name.Equals(
                    packageName,
                    StringComparison.OrdinalIgnoreCase))
            .Select(package =>
                package.Reference.Version!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CorruptInstallTimestampAsync(
        string packageName,
        string version)
    {
        string metadataPath =
            Path.Combine(
                TempCacheDir,
                "packages.ini");
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>> parsed =
            await IniParser.ParseFileAsync(
                metadataPath,
                TestContext.Current.CancellationToken);
        Dictionary<
            string,
            IReadOnlyDictionary<string, string>> rewritten =
            parsed.ToDictionary(
                section => section.Key,
                section =>
                    (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(
                        section.Value,
                        StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> packageDates =
            new(
                rewritten["packages"],
                StringComparer.OrdinalIgnoreCase)
            {
                [PackageCacheKey.Create(
                        new PackageReference(
                            packageName,
                            version))
                    .MetadataKey] = "not-a-date",
            };
        rewritten["packages"] =
            packageDates;
        await IniParser.WriteFileAsync(
            metadataPath,
            rewritten,
            TestContext.Current.CancellationToken);
    }

    private static PackageRecord CreateRecord(
        string version,
        DateTimeOffset? installedAt) =>
        new()
        {
            Reference = new PackageReference(
                "clean.selection",
                version),
            DirectoryPath = "cache",
            ContentPath = "cache/package",
            Manifest = new PackageManifest
            {
                Name = "clean.selection",
                Version = version,
            },
            InstalledAt = installedAt,
        };
}
