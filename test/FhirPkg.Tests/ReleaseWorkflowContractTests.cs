// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseWorkflowContractTests
{
    private const string ReleaseToolProject =
        "tools/FhirPkg.Release/FhirPkg.Release.csproj";

    [Fact]
    public void ReleaseWorkflow_UsesDirectReleaseToolCommandsAndPreservesReleaseSafety()
    {
        string buildWorkflow = ReadContract("build-and-test.yaml");
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string qualificationProject =
            ReadContract("FhirPkg.Qualification.csproj");
        string testProject = ReadTestProject();
        string[] commandNames =
        [
            .. FhirPkg.Release.Program.BuildRootCommand()
                .Subcommands
                .Select(static command => command.Name),
        ];

        string validateSection = GetSection(
            releaseWorkflow,
            "  validate:\n",
            "  source-ci:\n");
        string packSection = GetSection(
            releaseWorkflow,
            "  pack:\n",
            "  package-qualification:\n");
        string qualificationSection = GetSection(
            releaseWorkflow,
            "  package-qualification:\n",
            "  publish:\n");
        string publishSection = GetSection(
            releaseWorkflow,
            "  publish:\n",
            null);

        buildWorkflow.ShouldContain("workflow_call:\n");
        buildWorkflow.ShouldContain("commit_sha:\n");
        buildWorkflow.ShouldContain("required: true");
        CountOccurrences(
            buildWorkflow,
            "- name: Verify exact source commit").ShouldBe(2);
        CountOccurrences(
            buildWorkflow,
            "if: ${{ inputs.commit_sha != '' }}").ShouldBe(4);
        buildWorkflow.ShouldNotContain(
            "github.event_name == 'workflow_call'");
        buildWorkflow.ShouldContain("--framework net10.0");
        CountOccurrences(buildWorkflow, "'tools/**'").ShouldBe(2);
        commandNames.ShouldBe(
            [
                "validate-inputs",
                "validate-version",
                "validate-candidate",
                "inspect-publication",
                "validate-published-package",
            ]);

        releaseWorkflow.ShouldContain(
            "uses: ./.github/workflows/build-and-test.yaml");
        releaseWorkflow.ShouldContain(
            "commit_sha: ${{ needs.validate.outputs.commit }}");
        releaseWorkflow.ShouldNotContain("secrets: inherit");
        CountOccurrences(
            releaseWorkflow,
            "dotnet pack src/FhirPkg/FhirPkg.csproj").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "dotnet pack src/FhirPkg.Cli/FhirPkg.Cli.csproj").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "dotnet build src/FhirPkg.Cli/FhirPkg.Cli.csproj").ShouldBe(1);
        releaseWorkflow.ShouldContain(
            "name: fhir-pkg-${{ needs.validate.outputs.version }}-candidate");
        releaseWorkflow.ShouldContain(
            "<package pattern=\"fhir-pkg-lib\" />");
        releaseWorkflow.ShouldContain(
            "<package pattern=\"fhir-pkg-cli\" />");
        releaseWorkflow.ShouldContain("sdk_package_sha256:");
        releaseWorkflow.ShouldContain("cli_package_sha256:");
        releaseWorkflow.ShouldContain("dotnet tool install fhir-pkg-cli");
        releaseWorkflow.ShouldContain("--framework $env:TARGET_FRAMEWORK");
        releaseWorkflow.ShouldContain("Test-ToolQualification.ps1");
        releaseWorkflow.ShouldContain("toolInformationalVersion");
        releaseWorkflow.ShouldContain("corpusResult = 'passed'");
        releaseWorkflow.ShouldContain(
            "os: [ubuntu-latest, windows-latest, macos-latest]");
        releaseWorkflow.ShouldContain(
            "framework: [net8.0, net9.0, net10.0]");
        releaseWorkflow.ShouldContain("UsePublishedFhirPkg=true");
        releaseWorkflow.ShouldContain("release:");
        releaseWorkflow.ShouldContain("types: [published]");
        releaseWorkflow.ShouldNotContain("  push:");
        releaseWorkflow.ShouldContain("ref: ${{ github.sha }}");
        releaseWorkflow.ShouldContain("github.event_name == 'release'");
        releaseWorkflow.ShouldContain("github.event.action == 'published'");
        releaseWorkflow.ShouldContain(
            "needs.validate.outputs.validation_only == 'false'");
        releaseWorkflow.ShouldContain(
            "group: nuget-release-${{ github.event.release.tag_name || inputs.tag }}");
        releaseWorkflow.ShouldContain("cancel-in-progress: false");
        CountOccurrences(
            releaseWorkflow,
            "environment: nuget.org").ShouldBe(1);
        releaseWorkflow.ShouldContain("vars.NUGET_PUBLISH_ENVIRONMENT_READY");
        CountOccurrences(
            releaseWorkflow,
            "secrets.GINOC_NUGET").ShouldBe(4);
        CountOccurrences(
            releaseWorkflow,
            "dotnet nuget push").ShouldBe(4);
        CountOccurrences(releaseWorkflow, "--no-symbols").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "steps.candidate.outputs.cli_symbols_path").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "steps.candidate.outputs.sdk_symbols_path").ShouldBe(1);
        releaseWorkflow.ShouldContain("timeout-minutes: 90");

        releaseWorkflow.ShouldContain(ReleaseToolProject);
        CountOccurrences(
            releaseWorkflow,
            "dotnet restore tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(4);
        CountOccurrences(
            releaseWorkflow,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(4);
        CountOccurrences(
            releaseWorkflow,
            "--project tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(8);
        CountOccurrences(releaseWorkflow, "validate-version").ShouldBe(0);
        CountOccurrences(releaseWorkflow, "validate-inputs").ShouldBe(2);
        CountOccurrences(releaseWorkflow, "validate-candidate").ShouldBe(3);
        CountOccurrences(releaseWorkflow, "inspect-publication").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "validate-published-package").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "--github-ref \"${{ github.ref }}\"").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "--allow-published-version").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "--no-build\n          --no-restore\n          --").ShouldBe(8);
        releaseWorkflow.ShouldNotContain("Test-Release");
        releaseWorkflow.ShouldNotContain("Test-PublishedRelease");
        releaseWorkflow.ShouldNotContain("skip-signature-verification");

        validateSection.ShouldContain("dotnet-version: 10.0.302");
        CountOccurrences(
            validateSection,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(1);
        validateSection.IndexOf(
            "uses: actions/setup-dotnet@v4",
            StringComparison.Ordinal).ShouldBeGreaterThanOrEqualTo(0);
        validateSection.IndexOf(
            "validate-inputs",
            StringComparison.Ordinal).ShouldBeGreaterThan(
                validateSection.IndexOf(
                    "uses: actions/setup-dotnet@v4",
                    StringComparison.Ordinal));

        CountOccurrences(
            packSection,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(1);
        packSection.IndexOf(
            "validate-candidate",
            StringComparison.Ordinal).ShouldBeLessThan(
                packSection.IndexOf(
                    "Upload immutable release candidate",
                    StringComparison.Ordinal));

        CountOccurrences(
            qualificationSection,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(1);
        qualificationSection.IndexOf(
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj",
            StringComparison.Ordinal).ShouldBeLessThan(
                qualificationSection.IndexOf(
                    "Configure isolated global packages folder",
                    StringComparison.Ordinal));

        CountOccurrences(
            publishSection,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(1);
        publishSection.IndexOf(
            "uses: actions/setup-dotnet@v4",
            StringComparison.Ordinal).ShouldBeLessThan(
                publishSection.IndexOf(
                    "validate-inputs",
                    StringComparison.Ordinal));

        int preflightIndex = publishSection.IndexOf(
            "inspect-publication",
            StringComparison.Ordinal);
        int firstPushIndex = publishSection.IndexOf(
            "dotnet nuget push",
            StringComparison.Ordinal);
        int cliPrimaryIndex = publishSection.IndexOf(
            "steps.candidate.outputs.cli_package_path",
            StringComparison.Ordinal);
        int sdkPrimaryIndex = publishSection.IndexOf(
            "steps.candidate.outputs.sdk_package_path",
            StringComparison.Ordinal);
        preflightIndex.ShouldBeGreaterThanOrEqualTo(0);
        preflightIndex.ShouldBeLessThan(firstPushIndex);
        cliPrimaryIndex.ShouldBeLessThan(sdkPrimaryIndex);

        qualificationProject.ShouldContain(
            "<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>");
        testProject.ShouldNotContain(
            "..\\..\\.github\\scripts\\*.ps1");
    }

    private static string ReadContract(string fileName) =>
        File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "ReleaseContracts",
                fileName));

    private static string ReadTestProject() =>
        File.ReadAllText(
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "FhirPkg.Tests.csproj")));

    private static string GetSection(
        string value,
        string startMarker,
        string? endMarker)
    {
        int startIndex = value.IndexOf(
            startMarker,
            StringComparison.Ordinal);
        startIndex.ShouldBeGreaterThanOrEqualTo(0);

        int endIndex = endMarker is null
            ? value.Length
            : value.IndexOf(
                endMarker,
                startIndex + startMarker.Length,
                StringComparison.Ordinal);
        if (endMarker is not null)
        {
            endIndex.ShouldBeGreaterThan(startIndex);
        }

        return value[startIndex..endIndex];
    }

    private static int CountOccurrences(
        string value,
        string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(
                   search,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
