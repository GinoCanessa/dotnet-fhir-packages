// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliRaw(
        string allArgs,
        string? workingDirectory = null)
    {
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{CliAssemblyPath}\" {allArgs}",
                WorkingDirectory = workingDirectory ?? string.Empty,
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

    [Theory]
    [InlineData("")]
    [InlineData("install")]
    [InlineData("restore")]
    [InlineData("list")]
    [InlineData("remove")]
    [InlineData("clean")]
    [InlineData("search")]
    [InlineData("info")]
    [InlineData("resolve")]
    [InlineData("publish")]
    public async Task UnmodifiedOption_AppearsInRecursiveHelp(string command)
    {
        string workingDirectory = await CreateIsolatedWorkingDirectoryAsync();

        (int exitCode, string stdout, string stderr) =
            await RunIsolatedCli(workingDirectory, command, "--help");

        exitCode.ShouldBe(0, stderr);
        stdout.ShouldContain("--unmodified");
    }

    public static TheoryData<bool, bool, string> ArchiveModes
    {
        get
        {
            TheoryData<bool, bool, string> cases = [];
            foreach (bool rootLayout in new[] { false, true })
            {
                foreach (bool unmodified in new[] { false, true })
                {
                    foreach (string indexState in new[] { "missing", "invalid", "valid", "occupied" })
                        cases.Add(rootLayout, unmodified, indexState);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ArchiveModes))]
    public async Task Install_LayoutAndMode_PreserveArchiveAndIndexPolicy(
        bool rootLayout,
        bool unmodified,
        string indexState)
    {
        CliArchiveFixture fixture = CreateCliArchive(rootLayout, indexState);
        await using SyntheticPackageServer server = new(fixture);
        string workingDirectory = await CreateIsolatedWorkingDirectoryAsync(server.Uri);
        string[] modeArguments = unmodified ? ["--unmodified"] : [];

        (int exitCode, string stdout, string stderr) = await RunIsolatedCli(
            workingDirectory, ["install", fixture.Reference.FhirDirective, "--json", .. modeArguments]);

        AssertInstallJson(exitCode, stdout, stderr, fixture.Reference);
        await AssertInstalledArchiveAsync(fixture, unmodified);
        server.ArchiveRequests.ShouldBe(1);
    }

    [Theory]
    [InlineData(true, "", true)]
    [InlineData(true, "--unmodified false", false)]
    [InlineData(false, "--unmodified", true)]
    public async Task UnmodifiedConfig_CommandLineOverridesOnlyWhenSpecified(
        bool configured,
        string optionArguments,
        bool expected)
    {
        CliArchiveFixture fixture = CreateCliArchive(rootLayout: true, "missing");
        await using SyntheticPackageServer server = new(fixture);
        string workingDirectory = await CreateIsolatedWorkingDirectoryAsync(server.Uri, configured);
        string[] optionTokens = optionArguments.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);

        (int exitCode, string stdout, string stderr) = await RunIsolatedCli(
            workingDirectory, ["install", fixture.Reference.FhirDirective, "--json", .. optionTokens]);

        AssertInstallJson(exitCode, stdout, stderr, fixture.Reference);
        await AssertInstalledArchiveAsync(fixture, expected);
        server.ArchiveRequests.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Install_UnmodifiedOverwrite_ReconstructsSameSourceOnlyWhenRequested(
        bool rootLayout,
        bool initiallyUnmodified)
    {
        CliArchiveFixture fixture = CreateCliArchive(rootLayout, "missing");
        await using SyntheticPackageServer server = new(fixture);
        string workingDirectory = await CreateIsolatedWorkingDirectoryAsync(server.Uri);
        string initialMode = initiallyUnmodified ? "true" : "false";
        string replacementMode = initiallyUnmodified ? "false" : "true";

        (int firstExitCode, string firstOutput, string firstError) = await RunIsolatedCli(
            workingDirectory, "install", fixture.Reference.FhirDirective,
            "--unmodified", initialMode, "--json");
        AssertInstallJson(firstExitCode, firstOutput, firstError, fixture.Reference);
        PackageRecord original = await AssertInstalledArchiveAsync(fixture, initiallyUnmodified);
        Dictionary<string, byte[]> originalFiles = ReadPayload(original.DirectoryPath);
        string[] originalDirectories = ReadDirectories(original.DirectoryPath);
        original.ContentGeneration.ShouldNotBeNull();
        server.ArchiveRequests.ShouldBe(1);

        (int hitExitCode, string hitOutput, string hitError) = await RunIsolatedCli(
            workingDirectory, "install", fixture.Reference.FhirDirective,
            "--unmodified", replacementMode, "--json");
        AssertInstallJson(hitExitCode, hitOutput, hitError, fixture.Reference);
        PackageRecord hit = await AssertInstalledArchiveAsync(fixture, initiallyUnmodified);
        hit.ContentGeneration.ShouldBe(original.ContentGeneration);
        Dictionary<string, byte[]> hitFiles = ReadPayload(hit.DirectoryPath);
        hitFiles.Keys.ShouldBe(originalFiles.Keys, ignoreOrder: true);
        foreach ((string path, byte[] bytes) in originalFiles)
            hitFiles[path].ShouldBe(bytes);
        ReadDirectories(hit.DirectoryPath).ShouldBe(originalDirectories, ignoreOrder: true);
        server.ArchiveRequests.ShouldBe(1, "a cache hit must not claim source reconstruction");

        (int overwriteExitCode, string overwriteOutput, string overwriteError) =
            await RunIsolatedCli(
                workingDirectory, "install", fixture.Reference.FhirDirective,
                "--unmodified", replacementMode, "--overwrite", "--json");
        AssertInstallJson(overwriteExitCode, overwriteOutput, overwriteError, fixture.Reference);
        PackageRecord replacement = await AssertInstalledArchiveAsync(fixture, !initiallyUnmodified);
        replacement.ContentGeneration.ShouldNotBe(original.ContentGeneration);
        server.ArchiveRequests.ShouldBe(2, "--overwrite must reacquire the same synthetic archive");
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("clean")]
    public async Task Unmodified_ExplicitRemoveAndCleanRemainAvailable(string command)
    {
        CliArchiveFixture fixture = CreateCliArchive(rootLayout: true, "occupied");
        await using SyntheticPackageServer server = new(fixture);
        string workingDirectory = await CreateIsolatedWorkingDirectoryAsync(server.Uri);
        (int installExitCode, string installOutput, string installError) = await RunIsolatedCli(
            workingDirectory, "install", fixture.Reference.FhirDirective, "--unmodified", "--json");
        AssertInstallJson(installExitCode, installOutput, installError, fixture.Reference);
        PackageRecord installed = await AssertInstalledArchiveAsync(fixture, unmodified: true);
        string[] operands = command == "remove" ? [fixture.Reference.FhirDirective] : [];

        (int exitCode, string stdout, string stderr) = await RunIsolatedCli(
            workingDirectory, [command, .. operands, "--unmodified", "--force", "--json"]);

        exitCode.ShouldBe(0, stderr);
        using JsonDocument document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        Directory.Exists(installed.DirectoryPath).ShouldBeFalse();
        server.ArchiveRequests.ShouldBe(1);
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

    private string IsolatedCachePath => Path.Combine(TempCacheDir, "cli-cache");

    private Task<(int ExitCode, string StdOut, string StdErr)> RunIsolatedCli(
        string workingDirectory,
        params string[] args)
    {
        string allArgs = string.Join(" ", args)
            + $" --package-cache-folder \"{IsolatedCachePath}\" --verbose false --no-color";
        return RunCliRaw(allArgs, workingDirectory);
    }

    private async Task<string> CreateIsolatedWorkingDirectoryAsync(
        Uri? registryUri = null,
        bool? unmodified = null)
    {
        string workingDirectory = Path.Combine(TempCacheDir, "cli-work");
        Directory.CreateDirectory(workingDirectory);
        Dictionary<string, object> config = new()
        {
            ["includeCiBuilds"] = false,
        };
        if (registryUri is not null)
        {
            config["registries"] = new[]
            {
                new { url = registryUri.AbsoluteUri, type = "FhirNpm" },
            };
        }
        if (unmodified is not null)
            config["unmodified"] = unmodified.Value;

        // Every child has a valid local config, so loading cannot fall through to home.
        await File.WriteAllTextAsync(
            Path.Combine(workingDirectory, ".fhir-pkg.json"),
            JsonSerializer.Serialize(config),
            TestContext.Current.CancellationToken);
        return workingDirectory;
    }

    private void AssertInstallJson(
        int exitCode,
        string stdout,
        string stderr,
        PackageReference reference)
    {
        exitCode.ShouldBe(0, $"{stderr}{Environment.NewLine}{stdout}");
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement root = document.RootElement;
        JsonElement result = root.GetProperty("results").EnumerateArray().ShouldHaveSingleItem();
        result.GetProperty("directive").GetString().ShouldBe(reference.FhirDirective);
        // Exact directives retain this coarse status even on an unchanged cache hit.
        result.GetProperty("status").GetString().ShouldBe("Installed");
        result.GetProperty("dependencyFailures").GetArrayLength().ShouldBe(0);
        result.TryGetProperty("errorMessage", out _).ShouldBeFalse();
        result.TryGetProperty("disposition", out _).ShouldBeFalse();
        result.TryGetProperty("previousManifestDate", out _).ShouldBeFalse();
        result.TryGetProperty("manifestDate", out _).ShouldBeFalse();
        JsonElement package = result.GetProperty("package");
        package.GetProperty("name").GetString().ShouldBe(reference.Name);
        package.GetProperty("version").GetString().ShouldBe(reference.Version);
        package.GetProperty("directoryPath").GetString().ShouldBe(
            PackageCacheKey.Create(reference).GetPackageDirectoryPath(IsolatedCachePath));
        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().ShouldBe(1);
        summary.GetProperty("installed").GetInt32().ShouldBe(1);
        summary.GetProperty("alreadyCached").GetInt32().ShouldBe(0);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        JsonElement dispositions = summary.GetProperty("dispositions");
        dispositions.GetProperty("installed").GetInt32().ShouldBe(1);
        dispositions.GetProperty("updated").GetInt32().ShouldBe(0);
        dispositions.GetProperty("alreadyCurrent").GetInt32().ShouldBe(0);
        dispositions.GetProperty("refreshed").GetInt32().ShouldBe(0);
    }

    private static CliArchiveFixture CreateCliArchive(bool rootLayout, string indexState)
    {
        PackageReference reference = new("cli.unmodified", "1.0.0");
        string prefix = rootLayout ? string.Empty : "package/";
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [$"{prefix}package.json"] = Encoding.UTF8.GetBytes(
                """{"name":"cli.unmodified","version":"1.0.0","fhirVersions":["4.0.1"]}"""),
            [$"{prefix}patient.json"] = Encoding.UTF8.GetBytes(
                "{\r\n  \"resourceType\": \"Patient\", \"id\": \"cli-patient\"\r\n}\r\n"),
            ["openapi/spec.json"] = Encoding.UTF8.GetBytes("""{"$ref":"../other/schema.json"}"""),
            ["other/schema.json"] = Encoding.UTF8.GetBytes("""{"title":"opaque schema"}"""),
            ["other/package.json"] = Encoding.UTF8.GetBytes("{ malformed ancillary manifest"),
            ["other/.index.json"] = Encoding.UTF8.GetBytes("opaque ancillary index"),
            ["unknown/nested/binary.bin"] = [0x00, 0xff, 0x10, 0x80, 0x0d, 0x0a],
            ["packages.ini"] = Encoding.UTF8.GetBytes("distributed payload, not cache metadata\r\n"),
            [".fhirpkg/payload.bin"] = [0xff, 0x00, 0x12],
            ["readme.txt"] = Encoding.UTF8.GetBytes("distributed root readme"),
            ["package/readme.txt"] = Encoding.UTF8.GetBytes("distributed package child"),
        };
        List<string> directories = ["empty/", "openapi/empty/", "other/explicit/"];
        if (!rootLayout)
        {
            files[".index.json"] = Encoding.UTF8.GetBytes("opaque root index");
            files["package/openapi/spec.json"] = Encoding.UTF8.GetBytes("distinct content-relative file");
        }
        switch (indexState)
        {
            case "missing":
                break;
            case "invalid":
                files[$"{prefix}.index.json"] = Encoding.UTF8.GetBytes("{ invalid publisher index\r\n");
                break;
            case "valid":
                files[$"{prefix}.index.json"] = Encoding.UTF8.GetBytes(
                    "{\r\n  \"index-version\" : 2,\r\n  \"files\" : [\r\n"
                    + "    { \"filename\" : \"patient.json\", \"resourceType\" : \"Patient\", \"id\" : \"cli-patient\" }\r\n"
                    + "  ]\r\n}\r\n");
                break;
            case "occupied":
                directories.Add($"{prefix}.index.json/");
                files[$"{prefix}.index.json/distributed.bin"] = [0x12, 0x34, 0x00, 0xff];
                break;
            default:
                throw new ArgumentException($"Unknown index fixture '{indexState}'.", nameof(indexState));
        }

        using MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (string directory in directories)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory));
            foreach ((string path, byte[] bytes) in files)
            {
                using MemoryStream content = new(bytes, writable: false);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = content,
                });
            }
        }

        return new CliArchiveFixture(reference, rootLayout, indexState, files, directories, archive.ToArray());
    }

    private async Task<PackageRecord> AssertInstalledArchiveAsync(
        CliArchiveFixture fixture,
        bool unmodified)
    {
        string directoryPath = PackageCacheKey.Create(fixture.Reference)
            .GetPackageDirectoryPath(IsolatedCachePath);
        string wrapper = fixture.RootLayout && !unmodified ? "package/" : string.Empty;
        Dictionary<string, byte[]> expectedFiles = fixture.Files.ToDictionary(
            entry => wrapper + entry.Key, entry => entry.Value, StringComparer.Ordinal);
        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (string path in expectedFiles.Keys)
            AddArchiveDirectories(expectedDirectories, path, includePath: false);
        foreach (string path in fixture.Directories)
            AddArchiveDirectories(expectedDirectories, wrapper + path.TrimEnd('/'), includePath: true);
        string activeIndexPath = fixture.RootLayout && unmodified ? ".index.json" : "package/.index.json";
        bool rebuiltIndex = !unmodified && fixture.IndexState is "missing" or "invalid";
        HashSet<string> expectedFilePaths = new(expectedFiles.Keys, StringComparer.Ordinal);
        if (rebuiltIndex)
            expectedFilePaths.Add(activeIndexPath);

        Dictionary<string, byte[]> actualFiles = ReadPayload(directoryPath);
        actualFiles.Keys.ShouldBe(expectedFilePaths, ignoreOrder: true);
        foreach ((string path, byte[] bytes) in expectedFiles)
        {
            if (rebuiltIndex && path == activeIndexPath)
                continue;
            actualFiles[path].ShouldBe(bytes, path);
        }
        ReadDirectories(directoryPath).ShouldBe(expectedDirectories, ignoreOrder: true);

        using DiskPackageCache cache = new(IsolatedCachePath);
        PackageRecord? record = await cache.GetPackageAsync(
            fixture.Reference, TestContext.Current.CancellationToken);
        record.ShouldNotBeNull();
        record.DirectoryPath.ShouldBe(directoryPath);
        record.ContentPath.ShouldBe(fixture.RootLayout && unmodified
            ? directoryPath
            : Path.Combine(directoryPath, "package"));
        record.SizeBytes.ShouldBe(fixture.Files.Values.Sum(bytes => (long)bytes.Length));
        if (rebuiltIndex || fixture.IndexState == "valid")
        {
            record.Index.ShouldNotBeNull();
            ResourceIndexEntry resource = record.Index.Files.ShouldHaveSingleItem();
            resource.Filename.ShouldBe("patient.json");
            resource.Id.ShouldBe("cli-patient");
        }
        else
        {
            // A fresh reader exposes only usable on-disk indexes, not the child's transient index.
            record.Index.ShouldBeNull();
        }

        return record;
    }

    private static void AddArchiveDirectories(HashSet<string> directories, string path, bool includePath)
    {
        string[] segments = path.Split('/');
        int count = includePath ? segments.Length : segments.Length - 1;
        for (int length = 1; length <= count; length++)
            directories.Add(string.Join('/', segments.Take(length)));
    }

    private static Dictionary<string, byte[]> ReadPayload(string directoryPath) =>
        Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(directoryPath, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static string[] ReadDirectories(string directoryPath) =>
        Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directoryPath, path).Replace('\\', '/'))
            .ToArray();

    private sealed record CliArchiveFixture(
        PackageReference Reference,
        bool RootLayout,
        string IndexState,
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlyList<string> Directories,
        byte[] ArchiveBytes);

    // The real CLI accepts directives, so serve local synthetic bytes through its
    // existing registry transport rather than adding a tarball-path command route.
    private sealed class SyntheticPackageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly byte[] _archive;
        private readonly byte[] _listing;
        private readonly string _listingPath;
        private readonly Task _serverTask;
        private int _archiveRequests;

        internal SyntheticPackageServer(CliArchiveFixture fixture)
        {
            _archive = fixture.ArchiveBytes;
            _listingPath = $"/{fixture.Reference.Name}";
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Uri = new Uri($"http://127.0.0.1:{port}/");
            _listing = JsonSerializer.SerializeToUtf8Bytes(new PackageListing
            {
                PackageId = fixture.Reference.Name,
                Versions = new Dictionary<string, PackageVersionInfo>
                {
                    [fixture.Reference.Version!] = new()
                    {
                        Name = fixture.Reference.Name,
                        Version = fixture.Reference.Version!,
                        Distribution = new NpmDistribution(
                            ShaSum: Convert.ToHexString(SHA1.HashData(_archive)).ToLowerInvariant(),
                            TarballUrl: new Uri(Uri, "package.tgz").AbsoluteUri),
                    },
                },
            });
            _serverTask = ServeAsync();
        }

        internal Uri Uri { get; }

        internal int ArchiveRequests => Volatile.Read(ref _archiveRequests);

        private async Task ServeAsync()
        {
            CancellationToken cancellationToken = _shutdown.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                string? request = await reader.ReadLineAsync(cancellationToken);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                string? path = request?.Split(' ').ElementAtOrDefault(1);
                byte[] body;
                string contentType;
                string status = "200 OK";
                if (path == _listingPath)
                {
                    body = _listing;
                    contentType = "application/json";
                }
                else if (path == "/package.tgz")
                {
                    Interlocked.Increment(ref _archiveRequests);
                    body = _archive;
                    contentType = "application/gzip";
                }
                else
                {
                    body = [];
                    contentType = "text/plain";
                    status = "404 Not Found";
                }

                byte[] headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n"
                    + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
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
