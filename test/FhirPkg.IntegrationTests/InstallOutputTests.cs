// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cli;
using FhirPkg.Cli.Commands;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace FhirPkg.IntegrationTests;

[CollectionDefinition("InstallOutput", DisableParallelization = true)]
public sealed class InstallOutputCollection
    : ICollectionFixture<InstallOutputCollection>;

[Collection("InstallOutput")]
[Trait("Category", "Integration")]
public sealed class InstallOutputTests
{
    [Fact]
    public void ConsoleOutput_MutableCiResultsShowLabelsDatesAndCounts()
    {
        IReadOnlyList<PackageInstallResult> results =
        [
            CreateInstallResult(
                "first.package#current",
                PackageInstallDisposition.Installed,
                previousManifestDate: null,
                manifestDate: "[bold]20260721[/]"),
            CreateInstallResult(
                "updated.package#current",
                PackageInstallDisposition.Updated,
                "20260720",
                "20260721"),
            CreateInstallResult(
                "current.package#current",
                PackageInstallDisposition.AlreadyCurrent,
                "20260721",
                "20260721"),
            CreateInstallResult(
                "refreshed.package#current",
                PackageInstallDisposition.Refreshed,
                "20260721",
                "20260721"),
            new PackageInstallResult
            {
                Directive = "cached.package#1.0.0",
                Status = PackageInstallStatus.AlreadyCached
            },
            new PackageInstallResult
            {
                Directive = "missing.package#1.0.0",
                Status = PackageInstallStatus.NotFound
            }
        ];

        string output = CaptureAnsiConsole(
            () => ConsoleOutput.WriteInstallResults(results));

        output.ShouldContain("installed");
        output.ShouldContain("updated from CI");
        output.ShouldContain("already current");
        output.ShouldContain("refreshed");
        output.ShouldContain(
            "manifest date: [bold]20260721[/]");
        output.ShouldContain(
            "manifest date: 20260720 -> 20260721");
        output.ShouldContain(
            "manifest date: 20260721 (unchanged)");
        output.ShouldContain("1 installed");
        output.ShouldContain("1 updated");
        output.ShouldContain("1 already current");
        output.ShouldContain("1 refreshed");
        output.ShouldContain("1 already cached");
        output.ShouldContain("1 failed");
    }

    [Fact]
    public void ConsoleOutput_MissingManifestDateShowsUnavailable()
    {
        PackageInstallResult result = CreateInstallResult(
            "updated.package#current",
            PackageInstallDisposition.Updated,
            previousManifestDate: null,
            manifestDate: null);

        string output = CaptureAnsiConsole(
            () => ConsoleOutput.WriteInstallResult(result));

        output.ShouldContain(
            "manifest date: unavailable -> unavailable");
    }

    [Fact]
    public void JsonOutput_MutableCiResultsPreserveStatusAndExposeDisposition()
    {
        PackageInstallResult updated = CreateInstallResult(
            "updated.package#current",
            PackageInstallDisposition.Updated,
            "20260720",
            "20260721");
        PackageInstallResult exact = new()
        {
            Directive = "exact.package#1.0.0",
            Status = PackageInstallStatus.Installed,
            Package = CreatePackage("exact.package", "1.0.0")
        };
        PackageInstallResult failed = new()
        {
            Directive = "failed.package#current",
            Status = PackageInstallStatus.Failed,
            Disposition = PackageInstallDisposition.Updated,
            PreviousManifestDate = "20260720",
            ManifestDate = "20260721"
        };

        string json = CaptureConsoleOut(
            () => JsonOutput.WriteInstallResults(
                [updated, exact, failed]));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement updatedJson = root
            .GetProperty("results")[0];
        JsonElement exactJson = root
            .GetProperty("results")[1];
        JsonElement failedJson = root
            .GetProperty("results")[2];
        JsonElement summary = root.GetProperty("summary");
        JsonElement dispositions =
            summary.GetProperty("dispositions");

        updatedJson.GetProperty("status").GetString()
            .ShouldBe("Installed");
        updatedJson.GetProperty("disposition").GetString()
            .ShouldBe("Updated");
        updatedJson.GetProperty("previousManifestDate").GetString()
            .ShouldBe("20260720");
        updatedJson.GetProperty("manifestDate").GetString()
            .ShouldBe("20260721");
        exactJson.TryGetProperty("disposition", out _).ShouldBeFalse();
        exactJson.TryGetProperty(
            "previousManifestDate",
            out _).ShouldBeFalse();
        exactJson.TryGetProperty("manifestDate", out _).ShouldBeFalse();
        failedJson.TryGetProperty("disposition", out _).ShouldBeFalse();
        failedJson.TryGetProperty(
            "previousManifestDate",
            out _).ShouldBeFalse();
        failedJson.TryGetProperty("manifestDate", out _).ShouldBeFalse();
        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("installed").GetInt32().ShouldBe(2);
        summary.GetProperty("alreadyCached").GetInt32().ShouldBe(0);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        dispositions.GetProperty("installed").GetInt32().ShouldBe(1);
        dispositions.GetProperty("updated").GetInt32().ShouldBe(1);
        dispositions.GetProperty("alreadyCurrent").GetInt32()
            .ShouldBe(0);
        dispositions.GetProperty("refreshed").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void JsonOutput_MissingManifestDateIsExplicitNull()
    {
        PackageInstallResult result = CreateInstallResult(
            "refreshed.package#current",
            PackageInstallDisposition.Refreshed,
            previousManifestDate: null,
            manifestDate: null);

        string json = CaptureConsoleOut(
            () => JsonOutput.WriteInstallResult(result));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("previousManifestDate").ValueKind
            .ShouldBe(JsonValueKind.Null);
        root.GetProperty("manifestDate").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void InstallCommand_AlreadyCurrentDispositionKeepsSuccessExitCode()
    {
        PackageInstallResult result = CreateInstallResult(
            "current.package#current",
            PackageInstallDisposition.AlreadyCurrent,
            "20260721",
            "20260721");

        InstallCommand.GetExitCode([result])
            .ShouldBe(ExitCodes.Success);
    }

    private static PackageInstallResult CreateInstallResult(
        string directive,
        PackageInstallDisposition disposition,
        string? previousManifestDate,
        string? manifestDate) =>
        new()
        {
            Directive = directive,
            Status = PackageInstallStatus.Installed,
            Disposition = disposition,
            PreviousManifestDate = previousManifestDate,
            ManifestDate = manifestDate,
            Package = CreatePackage(
                directive[..directive.IndexOf('#')],
                "current")
        };

    private static PackageRecord CreatePackage(
        string name,
        string version) =>
        new()
        {
            Reference = new PackageReference(name, version),
            DirectoryPath = $@"C:\cache\{name}#{version}",
            ContentPath = $@"C:\cache\{name}#{version}\package",
            Manifest = new PackageManifest
            {
                Name = name,
                Version = version
            }
        };

    private static string CaptureAnsiConsole(Action action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        using StringWriter writer = new();
        try
        {
            AnsiConsole.Console = AnsiConsole.Create(
                new AnsiConsoleSettings
                {
                    Ansi = AnsiSupport.No,
                    ColorSystem =
                        ColorSystemSupport.NoColors,
                    Out = new AnsiConsoleOutput(writer)
                });
            AnsiConsole.Console.Profile.Width = 240;
            action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static string CaptureConsoleOut(Action action)
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
