// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.RegularExpressions;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseWorkflowContractTests
{
    private const string ReleaseToolProject =
        "tools/FhirPkg.Release/FhirPkg.Release.csproj";
    private const string RecoveryVersionBypass =
        "${{ steps.inputs.outputs.mode == 'recovery' && '--allow-published-version' || '' }}";

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
    public void ReleaseWorkflow_PublishGateIsExactAndVersionBypassIsScoped()
    {
        AssertPublicationSafetyContract(ReadContract("nuget-generator.yaml"));
    }

    [Fact]
    public void ReleaseWorkflow_PublicationSafetyAssertions_RejectCounterexamples()
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string branchOnlyDryRunGuard = releaseWorkflow.Replace(
            "        needs.validate.outputs.validation_only == 'false' &&\n"
            + "        (\n"
            + "          (github.event_name == 'release' &&",
            "        (\n"
            + "          (needs.validate.outputs.validation_only == 'false' &&\n"
            + "           github.event_name == 'release' &&",
            StringComparison.Ordinal);
        branchOnlyDryRunGuard.ShouldNotBe(releaseWorkflow);
        Should.Throw<ShouldAssertException>(
            () => AssertPublicationSafetyContract(branchOnlyDryRunGuard));

        string unconditionalInitialBypass = releaseWorkflow.Replace(
            RecoveryVersionBypass,
            "--allow-published-version",
            StringComparison.Ordinal);
        unconditionalInitialBypass.ShouldNotBe(releaseWorkflow);
        Should.Throw<ShouldAssertException>(
            () => AssertPublicationSafetyContract(unconditionalInitialBypass));
    }

    [Fact]
    public void ReleaseWorkflow_IdentityAndJobNamesMatchToolContracts()
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string driftSection = GetSection(
            ReadContract("fixture-drift.yaml"),
            "  fixture-drift:\n",
            "  live-registry-interop:\n");
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

        GetSection(driftSection, "    name: ", "\n")
            ["    name: ".Length..].ShouldBe(FixtureBundleLocator.DriftJobName);
        GetSection(packSection, "    name: ", "\n")
            ["    name: ".Length..].ShouldBe(ReleaseRecoveryVerifier.PackJobName);
        (GetSection(sourceCiSection, "    name: ", "\n")
            ["    name: ".Length..] + " / ")
            .ShouldBe(ReleaseRecoveryVerifier.SourceCiJobPrefix);

        identitySection.ShouldContain(
            "          PACK_ARTIFACT_DIGEST: ${{ needs.pack.outputs.artifact_digest }}\n");
        identitySection.ShouldContain(
            "          RETRIEVE_ARTIFACT_DIGEST: ${{ needs.retrieve.outputs.artifact_digest }}\n");
        string recoveryIdentity = GetSection(
            identitySection,
            "          if ($env:MODE -ceq 'recovery')\n",
            "          else\n");
        string packIdentity = GetSection(
            identitySection,
            "          else\n",
            "          $sha256Values = [ordered]@{\n");
        recoveryIdentity.Split('\n')
            .Where(line => line.TrimStart().StartsWith(
                "$artifactDigest =",
                StringComparison.Ordinal))
            .ShouldHaveSingleItem().Trim()
            .ShouldBe("$artifactDigest = $env:RETRIEVE_ARTIFACT_DIGEST");
        packIdentity.Split('\n')
            .Where(line => line.TrimStart().StartsWith(
                "$artifactDigest =",
                StringComparison.Ordinal))
            .ShouldHaveSingleItem().Trim()
            .ShouldBe("$artifactDigest = \"sha256:$env:PACK_ARTIFACT_DIGEST\"");

        string extractApproval = GetSection(
            retrieveSection,
            "      - name: Extract fixture approval\n",
            "      - name: Restore qualification harness\n");
        string deriveInputs = GetSection(
            retrieveSection,
            "      - name: Derive approved qualification inputs\n",
            "      - name: Reuse original qualification inputs\n");
        foreach (string step in new[] { extractApproval, deriveInputs })
        {
            step.ShouldContain(
                "        if: ${{ needs.validate.outputs.approval_commit != '' }}\n");
            step.ShouldContain(
                "          APPROVAL_COMMIT: ${{ needs.validate.outputs.approval_commit }}\n");
            step.ShouldContain(
                "          RELEASE_TAG: ${{ needs.validate.outputs.tag }}\n");
        }

        extractApproval.ShouldContain("        shell: bash\n");
        GetRunBlocks(extractApproval).ShouldHaveSingleItem().Trim()
            .ShouldBe(
                "git show \"${APPROVAL_COMMIT}:docs/releases/approvals/${RELEASE_TAG}.json\" > \"${RUNNER_TEMP}/fixture-approval.json\"");
        deriveInputs.ShouldContain("        shell: pwsh\n");
        deriveInputs.ShouldContain(
            "          BASE_BUNDLE_SHA256: ${{ steps.base_identity.outputs.base_bundle_sha256 }}\n");
        GetRunBlocks(deriveInputs).ShouldHaveSingleItem().Trim()
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray()
            .ShouldBe(
            [
                "$bundle = Join-Path '${{ runner.temp }}' 'fixture-bundle'",
                "$baseBundle = Join-Path '${{ runner.temp }}' 'original-fixture-bundle'",
                "$approval = Join-Path '${{ runner.temp }}' 'fixture-approval.json'",
                "$cache = Join-Path '${{ runner.temp }}' 'derive-cache'",
                "$report = Join-Path '${{ runner.temp }}' 'derive-report.json'",
                "dotnet run `",
                "--project test/FhirPkg.Qualification/FhirPkg.Qualification.csproj `",
                "--configuration Release `",
                "--framework net10.0 `",
                "--no-restore `",
                "-- `",
                "--derive-bundle $bundle `",
                "--bundle $baseBundle `",
                "--bundle-sha256 $env:BASE_BUNDLE_SHA256 `",
                "--approval $approval `",
                "--approval-commit $env:APPROVAL_COMMIT `",
                "--release-tag $env:RELEASE_TAG `",
                "--cache $cache `",
                "--output $report `",
                "--corpus test/FhirPkg.Qualification/qualification-corpus.json `",
                "--summary $env:GITHUB_STEP_SUMMARY `",
                "--summary-title retrieve-release-candidate"
            ]);
    }

    [Fact]
    public void ReleaseWorkflow_NoDispatchInputExpandedInRunScripts()
    {
        const string dispatchInputExpansionPattern =
            @"\$\{\{[^}]*?(?:(?:steps\.inputs|needs\.validate)\.outputs\.(?:version|tag)\b|github\.event\.inputs\.|github\.event\.release\.tag_name\b|(?<![\w.])inputs\.)";
        const string releaseTagEntry =
            "RELEASE_TAG: ${{ needs.validate.outputs.tag }}";
        const string inputTagEntry = "INPUT_TAG: ${{ inputs.tag }}";
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        List<string> runBlocks = GetRunBlocks(releaseWorkflow);
        Regex dispatchInputExpansion = new(
            dispatchInputExpansionPattern,
            RegexOptions.IgnoreCase);
        string matrixCandidateSection = GetSection(
            releaseWorkflow,
            "      - name: Verify downloaded release candidate\n",
            "      - name: Create isolated qualification package source\n");

        runBlocks.Count.ShouldBe(
            Regex.Matches(
                releaseWorkflow,
                "^ *(?:- +)?run:",
                RegexOptions.Multiline).Count);

        dispatchInputExpansion.IsMatch(releaseTagEntry).ShouldBeTrue();
        dispatchInputExpansion.IsMatch(inputTagEntry).ShouldBeTrue();
        releaseWorkflow.ShouldContain(releaseTagEntry);
        releaseWorkflow.ShouldContain(inputTagEntry);

        List<string> recoveryBlocks =
        [
            .. runBlocks.Where(
                static block => block.Contains(
                    "verify-recovery",
                    StringComparison.Ordinal)),
        ];
        recoveryBlocks.Count.ShouldBe(1);
        recoveryBlocks[0].ShouldContain("--version \"$RELEASE_VERSION\"");
        recoveryBlocks[0].ShouldContain("--tag \"$RELEASE_TAG\"");

        matrixCandidateSection.ShouldContain("shell: pwsh");

        List<string> expandingBlocks =
        [
            .. runBlocks.Where(
                block => dispatchInputExpansion.IsMatch(block)),
        ];
        expandingBlocks.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(
        "Validate release inputs, tag, and main ancestry",
        "RELEASE_VERSION: ${{ steps.inputs.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Validate release inputs, tag, and main ancestry",
        "RELEASE_TAG: ${{ steps.inputs.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Locate frozen fixture bundle",
        "RELEASE_TAG: ${{ steps.inputs.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Authorize recovery",
        "RELEASE_VERSION: ${{ steps.inputs.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Authorize recovery",
        "RELEASE_TAG: ${{ steps.inputs.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Verify release candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Verify release candidate",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Verify original release candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Verify original release candidate",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Verify downloaded release candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$env:RELEASE_VERSION\"")]
    [InlineData(
        "Verify downloaded release candidate",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$env:RELEASE_TAG\"")]
    [InlineData(
        "Revalidate release tag and main ancestry",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Revalidate release tag and main ancestry",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Verify GitHub Release is published",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Revalidate qualified release candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Revalidate qualified release candidate",
        "RELEASE_TAG: ${{ needs.validate.outputs.tag }}",
        "--tag \"$RELEASE_TAG\"")]
    [InlineData(
        "Reconcile existing NuGet publication state",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Verify published CLI primary matches candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--published-package-uri \"https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli/$RELEASE_VERSION/fhir-pkg-cli.$RELEASE_VERSION.nupkg\"")]
    [InlineData(
        "Verify published CLI primary matches candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    [InlineData(
        "Verify published SDK primary matches candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--published-package-uri \"https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib/$RELEASE_VERSION/fhir-pkg-lib.$RELEASE_VERSION.nupkg\"")]
    [InlineData(
        "Verify published SDK primary matches candidate",
        "RELEASE_VERSION: ${{ needs.validate.outputs.version }}",
        "--version \"$RELEASE_VERSION\"")]
    public void ReleaseWorkflow_PassesVersionAndTagToScriptsThroughEnvironment(
        string stepName,
        string environmentEntry,
        string argument)
    {
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string stepSection = GetSection(
            releaseWorkflow,
            "      - name: " + stepName + "\n",
            "\n\n");

        stepSection.ShouldContain(environmentEntry);
        stepSection.ShouldContain(argument);
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesVersionAndTagBeforeWritingOutputs()
    {
        const string versionCheck =
            @"if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')";
        const string tagCheck =
            "if (-not [string]::Equals($tag, \"v$version\", [StringComparison]::Ordinal))";
        string releaseWorkflow = ReadContract("nuget-generator.yaml");
        string validateSection = GetSection(
            releaseWorkflow,
            "  validate:\n",
            "  source-ci:\n");
        string resolveInputsSection = GetSection(
            validateSection,
            "      - name: Resolve release inputs\n",
            "      - uses: actions/setup-dotnet@v5\n");

        CountOccurrences(resolveInputsSection, versionCheck).ShouldBe(1);
        CountOccurrences(resolveInputsSection, tagCheck).ShouldBe(1);
        resolveInputsSection.ShouldContain(@"'^[1-9][0-9]*\z'");
        resolveInputsSection.ShouldContain(@"'^[0-9a-f]{40}\z'");
        resolveInputsSection.ShouldNotContain("'^[1-9][0-9]*$'");
        resolveInputsSection.ShouldNotContain("'^[0-9a-f]{40}$'");

        int unsupportedEventIndex = resolveInputsSection.IndexOf(
            "throw \"Unsupported release event",
            StringComparison.Ordinal);
        int versionCheckIndex = resolveInputsSection.IndexOf(
            versionCheck,
            StringComparison.Ordinal);
        int tagCheckIndex = resolveInputsSection.IndexOf(
            tagCheck,
            StringComparison.Ordinal);
        int firstOutputIndex = resolveInputsSection.IndexOf(
            "Add-Content -Path $env:GITHUB_OUTPUT",
            StringComparison.Ordinal);
        unsupportedEventIndex.ShouldBeGreaterThanOrEqualTo(0);
        versionCheckIndex.ShouldBeGreaterThan(unsupportedEventIndex);
        tagCheckIndex.ShouldBeGreaterThan(versionCheckIndex);
        firstOutputIndex.ShouldBeGreaterThan(tagCheckIndex);
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

    private static void AssertPublicationSafetyContract(string workflow)
    {
        const string conditionMarker = "    if: >-\n";
        const string expectedCondition = """
            ${{
              !cancelled() &&
              needs.release-identity.result == 'success' &&
              needs.package-qualification.result == 'success' &&
              needs.validate.outputs.validation_only == 'false' &&
              (
                (github.event_name == 'release' &&
                 github.event.action == 'published' &&
                 github.event.release.tag_name == needs.validate.outputs.tag &&
                 needs.validate.outputs.mode == 'release') ||
                (github.event_name == 'workflow_dispatch' &&
                 needs.validate.outputs.mode == 'recovery' &&
                 needs.validate.outputs.release_published == 'true')
              )
            }}
            """;
        workflow = NormalizeContractText(workflow);
        string validateSection = GetSection(
            workflow,
            "  validate:\n",
            "  source-ci:\n");
        string publishSection = GetSection(
            workflow,
            "  publish:\n",
            null);
        string publishCondition = GetSection(
            publishSection,
            conditionMarker,
            "    runs-on: ubuntu-latest\n")[conditionMarker.Length..];
        Regex.Replace(publishCondition, @"\s+", string.Empty)
            .ShouldBe(Regex.Replace(expectedCondition, @"\s+", string.Empty));

        string initialValidation = GetSection(
            validateSection,
            "      - name: Validate release inputs, tag, and main ancestry\n",
            "      - name: Locate frozen fixture bundle\n");
        string revalidation = GetSection(
            publishSection,
            "      - name: Revalidate release tag and main ancestry\n",
            "      - name: Verify GitHub Release is published\n");
        CountOccurrences(initialValidation, "--allow-published-version")
            .ShouldBe(1);
        GetRunBlocks(initialValidation).ShouldHaveSingleItem()
            .Split('\n')
            .Where(line => line.Contains(
                "--allow-published-version",
                StringComparison.Ordinal))
            .ShouldHaveSingleItem().Trim().ShouldBe(RecoveryVersionBypass);
        CountOccurrences(revalidation, "--allow-published-version")
            .ShouldBe(1);
        GetRunBlocks(revalidation).ShouldHaveSingleItem()
            .Split('\n')
            .Where(line => line.Contains(
                "--allow-published-version",
                StringComparison.Ordinal))
            .ShouldHaveSingleItem().Trim().ShouldBe("--allow-published-version");
        CountOccurrences(workflow, "--allow-published-version").ShouldBe(2);
    }

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

    private static List<string> GetRunBlocks(string workflow)
    {
        Regex runKeyRegex = new("^(?<prefix> *(?:- +)?)run:(?<value>.*)$");
        string[] lines = workflow.Split('\n');
        List<string> runBlocks = [];
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            Match runKey = runKeyRegex.Match(lines[lineIndex]);
            if (!runKey.Success)
            {
                continue;
            }

            string value = runKey.Groups["value"].Value.Trim();
            if (!value.StartsWith('|') &&
                !value.StartsWith('>'))
            {
                runBlocks.Add(value);
                continue;
            }

            int keyIndent = runKey.Groups["prefix"].Length;
            List<string> blockLines = [];
            while (lineIndex + 1 < lines.Length)
            {
                string line = lines[lineIndex + 1];
                int lineIndent = line.Length - line.TrimStart(' ').Length;
                if (!string.IsNullOrWhiteSpace(line) &&
                    lineIndent <= keyIndent)
                {
                    break;
                }

                blockLines.Add(line);
                lineIndex++;
            }

            runBlocks.Add(string.Join("\n", blockLines));
        }

        return runBlocks;
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
