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
            "  retrieve:\n");
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
                "locate-fixture-bundle",
                "fetch-artifact",
                "verify-recovery",
                "verify-release-published",
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
            "dotnet restore tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(5);
        CountOccurrences(
            releaseWorkflow,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(5);
        CountOccurrences(
            releaseWorkflow,
            "--project tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(15);
        CountOccurrences(releaseWorkflow, "validate-version").ShouldBe(0);
        CountOccurrences(releaseWorkflow, "validate-inputs").ShouldBe(2);
        CountOccurrences(releaseWorkflow, "validate-candidate").ShouldBe(4);
        CountOccurrences(releaseWorkflow, "inspect-publication").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "validate-published-package").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "--github-ref \"${{ github.ref }}\"").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "--allow-published-version").ShouldBe(2);
        CountOccurrences(
            releaseWorkflow,
            "--no-build\n          --no-restore\n          --").ShouldBe(15);
        releaseWorkflow.ShouldNotContain("Test-Release");
        releaseWorkflow.ShouldNotContain("Test-PublishedRelease");
        releaseWorkflow.ShouldNotContain("skip-signature-verification");

        validateSection.ShouldContain("dotnet-version: 10.0.302");
        CountOccurrences(
            validateSection,
            "dotnet build tools/FhirPkg.Release/FhirPkg.Release.csproj").ShouldBe(1);
        validateSection.IndexOf(
            "uses: actions/setup-dotnet@v5",
            StringComparison.Ordinal).ShouldBeGreaterThanOrEqualTo(0);
        validateSection.IndexOf(
            "validate-inputs",
            StringComparison.Ordinal).ShouldBeGreaterThan(
                validateSection.IndexOf(
                    "uses: actions/setup-dotnet@v5",
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
            "uses: actions/setup-dotnet@v5",
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

    [Fact]
    public void BuildWorkflow_SerializesPerFrameworkUnitTestRunsAndBoundsTesthostStalls()
    {
        string buildWorkflow = ReadContract("build-and-test.yaml");

        buildWorkflow.ShouldContain("VSTEST_CONNECTION_TIMEOUT: \"300\"");
        CountOccurrences(
            buildWorkflow,
            "-p:TestTfmsInParallel=false").ShouldBe(1);
        CountOccurrences(
            buildWorkflow,
            "timeout-minutes: 45").ShouldBe(2);
    }

    [Fact]
    public void QualificationProject_OnlyAddsProcessHostPropertiesForPublishedMode()
    {
        string qualificationProject =
            ReadContract("FhirPkg.Qualification.csproj");
        string expectedReference = NormalizeContractText("""
                <ProjectReference
                  Include="..\FhirPkg.ProcessTestHost\FhirPkg.ProcessTestHost.csproj"
                  ReferenceOutputAssembly="false">
                  <AdditionalProperties
                    Condition="'$(UsePublishedFhirPkg)' == 'true'">UsePublishedFhirPkg=$(UsePublishedFhirPkg);FhirPkgQualificationPackageVersion=$(FhirPkgQualificationPackageVersion)</AdditionalProperties>
                </ProjectReference>
            """);

        qualificationProject.ShouldContain(expectedReference);
        qualificationProject.ShouldNotContain(
            "AdditionalProperties=\"UsePublishedFhirPkg=$(UsePublishedFhirPkg);FhirPkgQualificationPackageVersion=$(FhirPkgQualificationPackageVersion)\"");
    }

    [Fact]
    public void ReleaseWorkflow_QualificationUsesSingularTargetFrameworkProperty()
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string qualificationSection = GetSection(
            releaseWorkflow,
            "  package-qualification:\n",
            "  publish:\n");
        string restoreVerificationSection = GetSection(
            qualificationSection,
            "      - name: Verify exact package restore and selected framework asset\n",
            "      - name: Install exact CLI package for requested framework\n");

        CountOccurrences(
            qualificationSection,
            "\"-p:TargetFramework=$env:TARGET_FRAMEWORK\"").ShouldBe(2);
        qualificationSection.ShouldNotContain(
            "-p:TargetFrameworks=$env:TARGET_FRAMEWORK");
        restoreVerificationSection.ShouldContain(
            "test/FhirPkg.Qualification/obj/project.assets.json");
        restoreVerificationSection.ShouldContain(
            "test/FhirPkg.ProcessTestHost/obj/project.assets.json");
        CountOccurrences(
            restoreVerificationSection,
            "Test-QualificationRestore.ps1").ShouldBe(1);
    }

    [Fact]
    public void ReleaseWorkflow_QualificationAlwaysWritesSummaries()
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string qualificationSection = GetSection(
            releaseWorkflow,
            "  package-qualification:\n",
            "  publish:\n");
        string missingReportSection = GetSection(
            qualificationSection,
            "      - name: Report missing qualification report\n",
            "      - name: Record qualification candidate evidence\n");
        int identitySummaryIndex = qualificationSection.IndexOf(
            "Write candidate identity summary",
            StringComparison.Ordinal);

        qualificationSection.ShouldContain(
            "--summary $env:GITHUB_STEP_SUMMARY");
        missingReportSection.ShouldContain("if: ${{ always() }}");
        identitySummaryIndex.ShouldBeGreaterThanOrEqualTo(0);
        identitySummaryIndex.ShouldBeLessThan(
            qualificationSection.IndexOf(
                "Qualify exact local package",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseWorkflow_QualifiesOfflineFromFrozenInputs()
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string validateSection = GetSection(
            releaseWorkflow,
            "  validate:\n",
            "  source-ci:\n");
        string packSection = GetSection(
            releaseWorkflow,
            "  pack:\n",
            "  retrieve:\n");
        string qualificationSection = GetSection(
            releaseWorkflow,
            "  package-qualification:\n",
            "  publish:\n");

        releaseWorkflow.ShouldContain(
            "permissions:\n  contents: read\n  actions: read\n");
        CountOccurrences(
            releaseWorkflow,
            "locate-fixture-bundle").ShouldBe(1);
        CountOccurrences(releaseWorkflow, "fetch-artifact").ShouldBe(3);
        CountOccurrences(
            releaseWorkflow,
            "--repository \"${{ github.repository }}\"").ShouldBe(6);

        int validateInputsIndex = validateSection.IndexOf(
            "validate-inputs",
            StringComparison.Ordinal);
        validateInputsIndex.ShouldBeGreaterThanOrEqualTo(0);
        validateSection.IndexOf(
            "locate-fixture-bundle",
            StringComparison.Ordinal).ShouldBeGreaterThan(
                validateInputsIndex);

        int fetchArtifactIndex = packSection.IndexOf(
            "fetch-artifact",
            StringComparison.Ordinal);
        fetchArtifactIndex.ShouldBeGreaterThanOrEqualTo(0);
        fetchArtifactIndex.ShouldBeLessThan(
            packSection.IndexOf(
                "dotnet build src/FhirPkg.Cli/FhirPkg.Cli.csproj",
                StringComparison.Ordinal));
        int candidateUploadIndex = packSection.IndexOf(
            "Upload immutable release candidate",
            StringComparison.Ordinal);
        candidateUploadIndex.ShouldBeGreaterThanOrEqualTo(0);
        candidateUploadIndex.ShouldBeLessThan(
            packSection.IndexOf(
                "Upload qualification inputs",
                StringComparison.Ordinal));
        packSection.ShouldContain(
            "name: fhir-pkg-${{ needs.validate.outputs.version }}-fixture-bundle");
        CountOccurrences(packSection, "retention-days: 90").ShouldBe(2);

        qualificationSection.ShouldContain(
            "--bundle-sha256 $env:BUNDLE_SHA256");
        qualificationSection.ShouldContain(
            "--release-tag $env:RELEASE_TAG");
        qualificationSection.ShouldContain(
            "HTTP_PROXY: http://127.0.0.1:1");
        qualificationSection.ShouldContain(
            "HTTPS_PROXY: http://127.0.0.1:1");
        qualificationSection.ShouldContain(
            "ALL_PROXY: http://127.0.0.1:1");
        qualificationSection.ShouldContain("NO_PROXY: ''");
        qualificationSection.ShouldContain(
            "run-id: ${{ needs.release-identity.outputs.candidate_run_id }}");
        int inputsDownloadIndex = qualificationSection.IndexOf(
            "Download frozen qualification inputs",
            StringComparison.Ordinal);
        inputsDownloadIndex.ShouldBeGreaterThanOrEqualTo(0);
        inputsDownloadIndex.ShouldBeLessThan(
            qualificationSection.IndexOf(
                "Qualify exact local package",
                StringComparison.Ordinal));
        qualificationSection.ShouldNotContain("--freeze-bundle");
        qualificationSection.ShouldNotContain("--derive-bundle");
    }

    [Fact]
    public void ReleaseWorkflow_RecoveryReusesQualificationAndPublication()
    {
        const string dryRunExpression =
            "steps.inputs.outputs.validation_only == 'true' && '--dry-run'";
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string dispatchSection = GetSection(
            releaseWorkflow,
            "  workflow_dispatch:\n",
            "\npermissions:\n");
        string validateSection = GetSection(
            releaseWorkflow,
            "  validate:\n",
            "  source-ci:\n");
        string sourceCiSection = GetSection(
            releaseWorkflow,
            "  source-ci:\n",
            "  pack:\n");
        string packSection = GetSection(
            releaseWorkflow,
            "  pack:\n",
            "  retrieve:\n");
        string retrieveSection = GetSection(
            releaseWorkflow,
            "  retrieve:\n",
            "  release-identity:\n");
        string identitySection = GetSection(
            releaseWorkflow,
            "  release-identity:\n",
            "  package-qualification:\n");
        string qualificationSection = GetSection(
            releaseWorkflow,
            "  package-qualification:\n",
            "  publish:\n");
        string publishSection = GetSection(
            releaseWorkflow,
            "  publish:\n",
            null);
        string publishCondition = GetSection(
            publishSection,
            "    if: >-\n",
            "    runs-on: ubuntu-latest\n");

        dispatchSection.ShouldContain("      recover_run_id:\n");
        dispatchSection.ShouldContain("      approval_commit:\n");
        validateSection.ShouldContain(
            "approval_commit requires recover_run_id.");
        validateSection.ShouldContain(
            "Manual workflow dispatch is validation-only unless recover_run_id names the release run being finished.");
        releaseWorkflow.ShouldContain(
            "concurrency:\n  group: nuget-release-${{ github.event.release.tag_name || inputs.tag }}\n  cancel-in-progress: false\n");

        sourceCiSection.ShouldContain(
            "if: ${{ needs.validate.outputs.mode != 'recovery' }}");
        packSection.ShouldContain(
            "if: ${{ needs.validate.outputs.mode != 'recovery' }}");
        retrieveSection.ShouldContain(
            "if: ${{ needs.validate.outputs.mode == 'recovery' }}");
        qualificationSection.ShouldContain(
            "if: ${{ !cancelled() && needs.release-identity.result == 'success' }}");
        CountOccurrences(
            releaseWorkflow,
            "needs.pack.outputs.").ShouldBe(
                CountOccurrences(identitySection, "needs.pack.outputs."));

        retrieveSection.ShouldNotContain("dotnet pack");
        retrieveSection.ShouldNotContain("dotnet build src/");
        retrieveSection.ShouldNotContain(
            "fhir-pkg-${{ needs.validate.outputs.version }}-candidate");
        retrieveSection.ShouldNotContain("environment:");
        retrieveSection.ShouldNotContain("secrets.");
        retrieveSection.ShouldContain("--derive-bundle");
        CountOccurrences(retrieveSection, "fetch-artifact").ShouldBe(2);
        int secondFetchArtifactIndex = retrieveSection.IndexOf(
            "fetch-artifact",
            retrieveSection.IndexOf(
                "fetch-artifact",
                StringComparison.Ordinal) + 1,
            StringComparison.Ordinal);
        secondFetchArtifactIndex.ShouldBeGreaterThanOrEqualTo(0);
        secondFetchArtifactIndex.ShouldBeLessThan(
            retrieveSection.IndexOf(
                "Upload qualification inputs",
                StringComparison.Ordinal));

        identitySection.ShouldContain("needs: [validate, pack, retrieve]");
        identitySection.ShouldContain("!cancelled()");
        identitySection.ShouldNotContain("actions/checkout");
        identitySection.ShouldNotContain("secrets.");

        publishCondition.ShouldContain("!cancelled()");
        publishCondition.ShouldContain(
            "needs.validate.outputs.validation_only == 'false'");
        publishCondition.ShouldContain(
            "github.event.action == 'published'");
        publishCondition.ShouldContain(
            "needs.validate.outputs.mode == 'recovery'");
        publishCondition.ShouldContain(
            "needs.validate.outputs.release_published == 'true'");
        publishCondition.ShouldContain(
            "needs.package-qualification.result == 'success'");

        int releasePublishedIndex = publishSection.IndexOf(
            "verify-release-published",
            StringComparison.Ordinal);
        releasePublishedIndex.ShouldBeGreaterThanOrEqualTo(0);
        releasePublishedIndex.ShouldBeLessThan(
            publishSection.IndexOf(
                "secrets.GINOC_NUGET",
                StringComparison.Ordinal));

        validateSection.ShouldContain("verify-recovery");
        CountOccurrences(releaseWorkflow, "verify-recovery").ShouldBe(1);
        CountOccurrences(
            releaseWorkflow,
            "verify-release-published").ShouldBe(1);
        CountOccurrences(validateSection, dryRunExpression).ShouldBe(1);
        CountOccurrences(releaseWorkflow, "--dry-run").ShouldBe(
            CountOccurrences(releaseWorkflow, dryRunExpression));
    }

    [Fact]
    public void FixtureDriftWorkflow_FreezesOnlyVerifiedBytesAndNeverGates()
    {
        string driftWorkflow = ReadContract("fixture-drift.yaml");
        string driftSection = GetSection(
            driftWorkflow,
            "  fixture-drift:\n",
            "  live-registry-interop:\n");
        string liveSection = GetSection(
            driftWorkflow,
            "  live-registry-interop:\n",
            null);

        driftWorkflow.ShouldContain("schedule:");
        driftWorkflow.ShouldContain("workflow_dispatch:");
        driftWorkflow.ShouldNotContain("pull_request");
        driftWorkflow.ShouldNotContain("  push:");
        driftWorkflow.ShouldNotContain("\n  release:");
        driftWorkflow.ShouldNotContain("workflow_run");
        driftWorkflow.ShouldContain("permissions:\n  contents: read\n");
        driftWorkflow.ShouldNotContain("environment:");
        driftWorkflow.ShouldNotContain("secrets.");
        driftWorkflow.ShouldNotContain("GINOC_NUGET");
        driftWorkflow.ShouldContain("dotnet-version: 10.0.302");

        driftSection.ShouldContain("--freeze-bundle");
        driftSection.ShouldContain(
            "name: fixture-bundle-v1-${{ steps.key.outputs.corpus_blob }}");
        driftSection.ShouldContain("retention-days: 90");
        driftSection.ShouldContain("if: ${{ success() }}");
        driftSection.ShouldContain(
            "HEAD:test/FhirPkg.Qualification/qualification-corpus.json");

        liveSection.ShouldContain("continue-on-error: true");
        liveSection.ShouldNotContain("--freeze-bundle");
        liveSection.ShouldNotContain("--bundle");
        liveSection.ShouldNotContain("fixture-bundle-v1-");
    }

    [Fact]
    public void BuildWorkflow_SourceCiStaysOffline()
    {
        string buildWorkflow = ReadContract("build-and-test.yaml");

        buildWorkflow.ShouldContain("--validate-only true");
        buildWorkflow.ShouldNotContain("--bundle");
        buildWorkflow.ShouldNotContain("--freeze-bundle");
        buildWorkflow.ShouldNotContain("fixture-drift");
    }

    [Fact]
    public void BuildWorkflow_ValidatesApprovalFilesWhenTheyChange()
    {
        string buildWorkflow = ReadContract("build-and-test.yaml");
        string testProject = ReadTestProject();

        CountOccurrences(
            buildWorkflow,
            "'docs/releases/approvals/**'").ShouldBe(2);
        testProject.ShouldContain(
            "Include=\"..\\..\\docs\\releases\\approvals\\*.json\"");
    }

    [Theory]
    [InlineData("LF", "\n")]
    [InlineData("CRLF", "\r\n")]
    [InlineData("CR", "\r")]
    public void ContractSectionParsing_IsIndependentOfSourceLineEndings(
        string caseName,
        string lineEnding)
    {
        string[] lines =
        [
            "jobs:",
            "  validate:",
            "    runs-on: ubuntu-latest",
            "  source-ci:",
            "    uses: ./.github/workflows/build-and-test.yaml",
        ];
        string contract = string.Join(lineEnding, lines) + lineEnding;
        string normalizedContract = NormalizeContractText(contract);
        string expectedContract = string.Join("\n", lines) + "\n";

        normalizedContract.ShouldBe(
            expectedContract,
            $"Failed for {caseName} line endings.");

        string validateSection = GetSection(
            normalizedContract,
            "  validate:\n",
            "  source-ci:\n");

        validateSection.ShouldBe(
            "  validate:\n    runs-on: ubuntu-latest\n",
            $"Failed for {caseName} line endings.");
    }

    private static string ReadContract(string fileName) =>
        NormalizeContractText(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "ReleaseContracts",
                    fileName)));

    private static string NormalizeContractText(string value) =>
        value.ReplaceLineEndings("\n");

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
