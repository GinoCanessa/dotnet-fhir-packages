// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace FhirPkg.IntegrationTests;

[CollectionDefinition(
    "FormattingOutput",
    DisableParallelization = true)]
public sealed class FormattingOutputCollection :
    ICollectionFixture<FormattingOutputCollection>;

[Collection("FormattingOutput")]
[Trait("Category", "Integration")]
public sealed class FormattingOutputTests
{
    [Fact]
    public void WriteRestoreResult_IncludesEveryExactIdentity()
    {
        PackageClosure closure = CreateClosure();

        string consoleOutput = CaptureAnsiConsole(
            () => ConsoleOutput.WriteRestoreResult(closure));
        string json = CaptureConsoleOut(
            () => JsonOutput.WriteRestoreResult(closure));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        consoleOutput.ShouldContain("shared.package");
        consoleOutput.ShouldContain("3.1.1");
        consoleOutput.ShouldContain("6.1.0");
        consoleOutput.ShouldContain("2 package(s) resolved");

        JsonElement preferredPackage = root.GetProperty("resolved")
            .GetProperty("shared.package");
        preferredPackage.EnumerateObject().Count().ShouldBe(2);
        preferredPackage.GetProperty("name")
            .GetString()
            .ShouldBe("shared.package");
        preferredPackage.GetProperty("version")
            .GetString()
            .ShouldBe("6.1.0");
        JsonElement resolvedPackages =
            root.GetProperty("resolvedPackages");
        resolvedPackages.GetArrayLength().ShouldBe(2);
        JsonElement firstResolvedPackage = resolvedPackages[0];
        firstResolvedPackage.EnumerateObject().Count().ShouldBe(2);
        firstResolvedPackage.GetProperty("name")
            .GetString()
            .ShouldBe("shared.package");
        firstResolvedPackage.GetProperty("version")
            .GetString()
            .ShouldBe("3.1.1");
        JsonElement secondResolvedPackage = resolvedPackages[1];
        secondResolvedPackage.EnumerateObject().Count().ShouldBe(2);
        secondResolvedPackage.GetProperty("name")
            .GetString()
            .ShouldBe("shared.package");
        secondResolvedPackage.GetProperty("version")
            .GetString()
            .ShouldBe("6.1.0");
    }

    [Fact]
    public void WriteResolveResult_WithResolutionWarnings_RendersEachWarning()
    {
        string plainWarning =
            "Resolved 'jkiddo/fhir-subscription-backport-ig' on branch 'fixing-missing-extensions'";
        string markupWarning =
            "2 organizations published [example.ig.core]; none is named by the prefix table";
        ResolvedDirective resolved = CreateResolvedDirective([plainWarning, markupWarning]);

        string consoleOutput = CaptureAnsiConsole(
            () => ConsoleOutput.WriteResolveResult(resolved));
        string json = CaptureConsoleOut(
            () => JsonOutput.WriteResolveResult(resolved));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        consoleOutput.ShouldContain("Warning:");
        consoleOutput.ShouldContain(plainWarning);
        consoleOutput.ShouldContain(markupWarning);

        JsonElement warnings = root.GetProperty("resolutionWarnings");
        warnings.GetArrayLength().ShouldBe(2);
        warnings[0].GetString().ShouldBe(plainWarning);
        warnings[1].GetString().ShouldBe(markupWarning);
    }

    [Fact]
    public void WriteResolveResult_WithoutResolutionWarnings_OmitsWarningOutput()
    {
        ResolvedDirective resolved = CreateResolvedDirective(warnings: null);

        string consoleOutput = CaptureAnsiConsole(
            () => ConsoleOutput.WriteResolveResult(resolved));
        string json = CaptureConsoleOut(
            () => JsonOutput.WriteResolveResult(resolved));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        consoleOutput.ShouldNotContain("Warning:");
        root.TryGetProperty("resolutionWarnings", out _).ShouldBeFalse();
    }

    private static ResolvedDirective CreateResolvedDirective(
        IReadOnlyList<string>? warnings) =>
        new()
        {
            Reference = new PackageReference(
                "hl7.fhir.uv.subscriptions-backport",
                "1.1.0"),
            TarballUri = new Uri(
                "https://build.fhir.org/ig/jkiddo/fhir-subscription-backport-ig/branches/fixing-missing-extensions/package.tgz"),
            ResolutionWarnings = warnings,
        };

    private static PackageClosure CreateClosure() =>
        new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["shared.package"] =
                        new PackageReference(
                            "shared.package",
                            "6.1.0"),
                },
            ResolvedPackages =
            [
                new PackageReference(
                    "shared.package",
                    "3.1.1"),
                new PackageReference(
                    "shared.package",
                    "6.1.0"),
            ],
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrderIsComplete = true,
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
                    Out = new AnsiConsoleOutput(writer),
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
