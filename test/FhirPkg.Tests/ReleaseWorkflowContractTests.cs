// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseWorkflowContractTests
{
    [Fact]
    public void ReleaseWorkflow_QualifiesSynchronizedSdkAndCliAcrossNineJobs()
    {
        string buildWorkflow = ReadContract("build-and-test.yaml");
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string qualificationProject =
            ReadContract("FhirPkg.Qualification.csproj");
        string releaseInputs =
            ReadScriptContract("Test-ReleaseInputs.ps1");
        string versionAvailability =
            ReadScriptContract(
                "Test-ReleaseVersionAvailability.ps1");

        buildWorkflow.ShouldContain("workflow_call:");
        buildWorkflow.ShouldContain("commit_sha:");
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
        releaseWorkflow.ShouldContain(
            "sdk_package_sha256:");
        releaseWorkflow.ShouldContain(
            "cli_package_sha256:");
        releaseWorkflow.ShouldContain(
            "dotnet tool install fhir-pkg-cli");
        releaseWorkflow.ShouldContain(
            "--framework $env:TARGET_FRAMEWORK");
        releaseWorkflow.ShouldContain(
            "Test-ToolQualification.ps1");
        releaseWorkflow.ShouldContain(
            "toolInformationalVersion");
        releaseWorkflow.ShouldContain(
            "corpusResult = 'passed'");
        releaseWorkflow.ShouldContain(
            "os: [ubuntu-latest, windows-latest, macos-latest]");
        releaseWorkflow.ShouldContain(
            "framework: [net8.0, net9.0, net10.0]");
        releaseWorkflow.ShouldContain(
            "UsePublishedFhirPkg=true");
        releaseWorkflow.ShouldContain("release:");
        releaseWorkflow.ShouldContain("types: [published]");
        releaseWorkflow.ShouldNotContain("  push:");
        releaseWorkflow.ShouldContain(
            "ref: ${{ github.sha }}");
        releaseWorkflow.ShouldContain(
            "github.event_name == 'release'");
        releaseWorkflow.ShouldContain(
            "github.event.action == 'published'");
        releaseWorkflow.ShouldContain(
            "needs.validate.outputs.validation_only == 'false'");
        releaseWorkflow.ShouldContain(
            "group: nuget-release-${{ github.event.release.tag_name || inputs.tag }}");
        releaseWorkflow.ShouldContain(
            "cancel-in-progress: false");
        CountOccurrences(
            releaseWorkflow,
            "environment: nuget.org").ShouldBe(1);
        releaseWorkflow.ShouldContain(
            "vars.NUGET_PUBLISH_ENVIRONMENT_READY");
        CountOccurrences(
            releaseWorkflow,
            "secrets.GINOC_NUGET").ShouldBe(4);
        CountOccurrences(
            releaseWorkflow,
            "dotnet nuget push").ShouldBe(4);
        CountOccurrences(
            releaseWorkflow,
            "--no-symbols").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "Test-PublishedReleasePackage.ps1").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "steps.candidate.outputs.cli_symbols_path").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "steps.candidate.outputs.sdk_symbols_path").ShouldBe(1);
        releaseWorkflow.ShouldContain(
            "Test-ReleasePublicationState.ps1");
        releaseWorkflow.ShouldContain(
            "timeout-minutes: 90");

        int preflightIndex = releaseWorkflow.IndexOf(
            "Test-ReleasePublicationState.ps1",
            StringComparison.Ordinal);
        int firstPushIndex = releaseWorkflow.IndexOf(
            "dotnet nuget push",
            StringComparison.Ordinal);
        int cliPrimaryIndex = releaseWorkflow.IndexOf(
            "steps.candidate.outputs.cli_package_path",
            StringComparison.Ordinal);
        int sdkPrimaryIndex = releaseWorkflow.IndexOf(
            "steps.candidate.outputs.sdk_package_path",
            StringComparison.Ordinal);
        preflightIndex.ShouldBeGreaterThanOrEqualTo(0);
        preflightIndex.ShouldBeLessThan(firstPushIndex);
        cliPrimaryIndex.ShouldBeLessThan(sdkPrimaryIndex);

        releaseInputs.ShouldContain(
            "Test-ReleaseVersionAvailability.ps1");
        versionAvailability.ShouldContain("fhir-pkg-cli");
        versionAvailability.ShouldContain("fhir-pkg-lib");

        qualificationProject.ShouldContain(
            "<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>");
    }

    private static string ReadContract(string fileName) =>
        File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "ReleaseContracts",
                fileName));

    private static string ReadScriptContract(string fileName) =>
        File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "ReleaseContracts",
                "Scripts",
                fileName));

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
