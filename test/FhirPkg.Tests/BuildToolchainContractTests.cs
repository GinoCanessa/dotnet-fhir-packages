// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class BuildToolchainContractTests
{
    private static readonly string[] ExpectedFrameworks =
        ["net11.0", "net10.0", "net9.0", "net8.0"];
    private static readonly string[] ProjectPaths =
    [
        "src/FhirPkg/FhirPkg.csproj",
        "src/FhirPkg.Cli/FhirPkg.Cli.csproj",
        "test/FhirPkg.Tests/FhirPkg.Tests.csproj",
        "test/FhirPkg.IntegrationTests/FhirPkg.IntegrationTests.csproj",
        "test/FhirPkg.ProcessTestHost/FhirPkg.ProcessTestHost.csproj",
        "test/FhirPkg.Qualification/FhirPkg.Qualification.csproj",
        "tools/FhirPkg.Release/FhirPkg.Release.csproj",
    ];
    private const string PrimaryFramework = "net11.0";
    private const string OsMatrix =
        "os: [ubuntu-latest, windows-latest, macos-latest]";
    private const string FrameworkMatrix =
        "framework: [net8.0, net9.0, net10.0, net11.0]";

    [Fact]
    public void FrameworkPolicy_DeclaresFourSupportedTargetsAndNet11Primary()
    {
        XDocument policy = ReadXml("Directory.Build.props");

        GetProperty(policy, "FhirPkgSupportedTargetFrameworks").Value
            .Split(';').ShouldBe(ExpectedFrameworks);
        GetProperty(policy, "FhirPkgPrimaryTargetFramework").Value
            .ShouldBe(PrimaryFramework);
        GetProperty(policy, "TargetFrameworks").Value
            .ShouldBe("$(FhirPkgSupportedTargetFrameworks)");
        foreach ((string name, string value) in new[]
        {
            ("LangVersion", "14"),
            ("Nullable", "enable"),
            ("ImplicitUsings", "enable"),
        })
        {
            XElement property = GetProperty(policy, name);
            property.Value.ShouldBe(value);
            ((string?)property.Attribute("Condition"))
                .ShouldBe($"'$({name})' == ''");
        }

        policy.Descendants().ShouldNotContain(
            element => element.Name.LocalName == "PackageId"
                || element.Name.LocalName == "Version"
                || element.Name.LocalName == "VersionPrefix"
                || element.Name.LocalName == "IsPackable");
        policy.Descendants("Import").ShouldBeEmpty();
    }

    [Fact]
    public void Projects_InheritFrameworkAndCompilerDefaultsWithoutPackagingLeakage()
    {
        string[] policyNames =
        [
            "FhirPkgPrimaryTargetFramework",
            "FhirPkgSupportedTargetFrameworks",
            "TargetFramework",
            "TargetFrameworks",
            "LangVersion",
            "Nullable",
            "ImplicitUsings",
        ];
        XDocument packaging = ReadXml("src/common.props");
        packaging.Descendants().ShouldNotContain(
            element => policyNames.Contains(element.Name.LocalName));
        ReadBuildContract("src/common.props").ShouldNotContain("netstandard2.0");

        foreach (string projectPath in ProjectPaths)
        {
            XDocument project = ReadXml(projectPath);
            project.Descendants().ShouldNotContain(
                element => policyNames.Contains(element.Name.LocalName),
                projectPath);
            string[] imports = project.Descendants("Import")
                .Select(element => (string)element.Attribute("Project")!)
                .ToArray();
            if (projectPath.StartsWith("src/", StringComparison.Ordinal))
            {
                imports.ShouldBe([@"..\common.props", @"..\common.targets"]);
            }
            else
            {
                imports.ShouldBeEmpty();
                GetProperty(project, "IsPackable").Value.ShouldBe("false");
            }

            project.Descendants("PackageReference").ShouldNotContain(
                reference => reference.Attribute("Version") != null
                    || reference.Element("Version") != null);
        }

        XDocument sdk = ReadXml("src/FhirPkg/FhirPkg.csproj");
        XElement[] references = sdk.Descendants("PackageReference").ToArray();
        references.Select(reference => (string)reference.Attribute("Include")!)
            .ShouldBe(
            [
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Http",
                "Microsoft.Extensions.Logging.Abstractions",
                "Microsoft.Extensions.Options",
            ]);
        foreach (XElement reference in references)
        {
            reference.AncestorsAndSelf().ShouldNotContain(
                element => element.Attribute("Condition") != null);
        }

        AssertPublishedPackageMode(
            ReadXml("test/FhirPkg.ProcessTestHost/FhirPkg.ProcessTestHost.csproj"));
        XDocument qualification =
            ReadXml("test/FhirPkg.Qualification/FhirPkg.Qualification.csproj");
        AssertPublishedPackageMode(qualification);
        XElement hostReference = qualification.Descendants("ProjectReference")
            .Single(reference => (string?)reference.Attribute("Include")
                == @"..\FhirPkg.ProcessTestHost\FhirPkg.ProcessTestHost.csproj");
        ((string?)hostReference.Attribute("ReferenceOutputAssembly"))
            .ShouldBe("false");
        hostReference.Attribute("AdditionalProperties").ShouldBeNull();
        XElement additionalProperties =
            hostReference.Elements("AdditionalProperties").ShouldHaveSingleItem();
        ((string?)additionalProperties.Attribute("Condition"))
            .ShouldBe("'$(UsePublishedFhirPkg)' == 'true'");
        additionalProperties.Value.ShouldBe(
            "UsePublishedFhirPkg=$(UsePublishedFhirPkg);FhirPkgQualificationPackageVersion=$(FhirPkgQualificationPackageVersion)");

        XDocument tests = ReadXml("test/FhirPkg.Tests/FhirPkg.Tests.csproj");
        string[] copiedPaths =
        [
            "Directory.Build.props",
            "global.json",
            "src/common.props",
            .. ProjectPaths,
            ".github/scripts/Test-QualificationRestore.ps1",
            ".github/scripts/Test-ToolQualification.ps1",
        ];
        XElement[] copiedItems = tests.Descendants("None")
            .Where(item => ((string?)item.Attribute("Link"))?
                .StartsWith(@"BuildContracts\", StringComparison.Ordinal) == true)
            .ToArray();
        copiedItems.Select(item => item.Attribute("Link")!.Value.Replace('\\', '/'))
            .ShouldBe(copiedPaths.Select(path => "BuildContracts/" + path));
        foreach (XElement item in copiedItems)
        {
            ((string?)item.Attribute("CopyToOutputDirectory"))
                .ShouldBe("PreserveNewest");
            item.Attribute("Include")!.Value.ShouldNotContain("*");
        }
    }

    [Fact]
    public void FrameworkPolicy_DefaultsOnlyWhenBothTargetPropertiesAreEmpty()
    {
        XDocument policy = ReadXml("Directory.Build.props");

        ((string?)GetProperty(policy, "TargetFrameworks").Attribute("Condition"))
            .ShouldBe("'$(TargetFramework)' == '' and '$(TargetFrameworks)' == ''");
        policy.Descendants("TargetFramework").ShouldBeEmpty();
        GetProperty(policy, "TargetFrameworks").Parent!.Attribute("Condition")
            .ShouldBeNull();
    }

    [Fact]
    public void Workflows_ProjectSdkPrimaryAndSupportedFrameworkPolicy()
    {
        using JsonDocument global = JsonDocument.Parse(ReadBuildContract("global.json"));
        JsonElement sdk = global.RootElement.GetProperty("sdk");
        string sdkVersion = sdk.GetProperty("version").GetString()!;
        sdkVersion.ShouldBe("11.0.100");
        sdk.GetProperty("rollForward").GetString().ShouldBe("latestFeature");
        sdk.GetProperty("allowPrerelease").GetBoolean().ShouldBeFalse();
        string[] allSdks = [sdkVersion, "10.0.302", "9.0.119", "8.0.423"];
        string build = ReadWorkflow("build-and-test.yaml");
        Dictionary<string, string> buildJobs = GetJobs(build);
        buildJobs.Keys.ToArray().ShouldBe(["build-and-test", "hardened-integration"]);
        foreach (string job in buildJobs.Values)
        {
            AssertSetupSdks(job, allSdks);
            job.ShouldContain("\n        " + OsMatrix + "\n");
            job.ShouldContain("timeout-minutes: 45");
        }

        string main = buildJobs["build-and-test"];
        main.ShouldNotContain("\n        framework:");
        AssertUnpinned(GetStep(main, "Restore"));
        AssertUnpinned(GetStep(main, "Build"));
        string unit = GetStep(main, "Unit tests");
        AssertUnpinned(unit);
        unit.ShouldContain("-p:TestTfmsInParallel=false");
        Regex.Matches(build, Regex.Escape("-p:TestTfmsInParallel=false"))
            .Count.ShouldBe(1);
        build.ShouldContain("env:\n  VSTEST_CONNECTION_TIMEOUT: \"300\"\n");
        GetStep(main, "Remaining integration tests (net11.0)")
            .ShouldContain("--framework " + PrimaryFramework);
        GetStep(main, "Validate qualification corpus")
            .ShouldContain("--framework " + PrimaryFramework);
        string hardened = buildJobs["hardened-integration"];
        hardened.ShouldContain("\n        " + FrameworkMatrix + "\n");
        AssertUnpinned(GetStep(hardened, "Restore"));
        GetStep(hardened, "Build")
            .ShouldContain("--framework ${{ matrix.framework }}");
        GetStep(hardened, "Hardened integration tests")
            .ShouldContain("--framework ${{ matrix.framework }}");
        AssertFrameworkSelectors(main, PrimaryFramework);
        AssertFrameworkSelectors(hardened, "${{ matrix.framework }}");
        foreach (string section in new[]
        {
            GetSection(build, "  push:\n", "  pull_request:\n"),
            GetSection(build, "  pull_request:\n", "  workflow_call:\n"),
        })
        {
            Regex.Matches(section, Regex.Escape("- 'Directory.Build.props'"))
                .Count.ShouldBe(1);
        }

        Dictionary<string, string> releaseJobs =
            GetJobs(ReadWorkflow("nuget-generator.yaml"));
        releaseJobs.Keys.ToArray().ShouldBe(
        [
            "validate", "source-ci", "pack", "retrieve",
            "release-identity", "package-qualification", "publish",
        ]);
        foreach ((string name, string job) in releaseJobs)
        {
            if (name is "source-ci" or "release-identity")
            {
                job.ShouldNotContain("actions/setup-dotnet");
                continue;
            }

            AssertSetupSdks(
                job,
                name is "pack" or "package-qualification" ? allSdks : [sdkVersion]);
            foreach (string step in GetSteps(job))
            {
                if (step.Contains(
                        "tools/FhirPkg.Release/FhirPkg.Release.csproj",
                        StringComparison.Ordinal)
                    && (step.Contains("dotnet build", StringComparison.Ordinal)
                        || step.Contains("dotnet run", StringComparison.Ordinal)))
                {
                    step.ShouldContain("--framework " + PrimaryFramework);
                }
            }

            AssertFrameworkSelectors(
                job,
                name == "package-qualification"
                    ? [PrimaryFramework, "$env:TARGET_FRAMEWORK"]
                    : [PrimaryFramework]);
        }

        string pack = releaseJobs["pack"];
        AssertUnpinned(GetStep(pack, "Build release graph once"));
        string packStep = GetStep(pack, "Pack synchronized release candidate once");
        AssertUnpinned(packStep);
        packStep.ShouldContain("dotnet pack src/FhirPkg/FhirPkg.csproj");
        packStep.ShouldContain("dotnet pack src/FhirPkg.Cli/FhirPkg.Cli.csproj");
        GetStep(releaseJobs["retrieve"], "Derive approved qualification inputs")
            .ShouldContain("--framework " + PrimaryFramework);
        string qualification = releaseJobs["package-qualification"];
        qualification.ShouldContain("\n        " + OsMatrix + "\n");
        qualification.ShouldContain("\n        " + FrameworkMatrix + "\n");
        GetStep(qualification, "Qualify exact local package")
            .ShouldContain("--framework $env:TARGET_FRAMEWORK");
        GetStep(qualification, "Install exact CLI package for requested framework")
            .ShouldContain("--framework $env:TARGET_FRAMEWORK");

        Dictionary<string, string> driftJobs =
            GetJobs(ReadWorkflow("fixture-drift.yaml"));
        driftJobs.Keys.ToArray().ShouldBe(["fixture-drift", "live-registry-interop"]);
        foreach (string job in driftJobs.Values)
        {
            AssertSetupSdks(job, [sdkVersion]);
            AssertFrameworkSelectors(job, PrimaryFramework);
        }
        GetStep(driftJobs["fixture-drift"], "Freeze verified fixture bundle")
            .ShouldContain("--framework " + PrimaryFramework);
        GetStep(driftJobs["live-registry-interop"], "Run LIVE registry interoperability")
            .ShouldContain("--framework " + PrimaryFramework);
    }

    [Fact]
    public void QualificationScripts_AllowExactlyTheSupportedFrameworks()
    {
        (string path, string pattern)[] scripts =
        [
            (".github/scripts/Test-QualificationRestore.ps1",
                @"if \(\$Framework -notin @\((?<values>[^)]*)\)\)"),
            (".github/scripts/Test-ToolQualification.ps1",
                @"\[ValidateSet\((?<values>[^)]*)\)\]\s*\[string\] \$Framework"),
        ];
        foreach ((string path, string pattern) in scripts)
        {
            Match match = Regex.Matches(ReadBuildContract(path), pattern)
                .Cast<Match>().ShouldHaveSingleItem();
            string values = match.Groups["values"].Value;
            string[] frameworks = Regex.Matches(values, "'(?<framework>[^']+)'")
                .Cast<Match>()
                .Select(value => value.Groups["framework"].Value)
                .ToArray();
            frameworks.Order(StringComparer.Ordinal)
                .ShouldBe(ExpectedFrameworks.Order(StringComparer.Ordinal));
            values.Replace(" ", string.Empty, StringComparison.Ordinal)
                .ShouldBe(string.Join(",", frameworks.Select(value => $"'{value}'")));
        }
    }

    private static void AssertPublishedPackageMode(XDocument project)
    {
        XElement mode = GetProperty(project, "UsePublishedFhirPkg");
        mode.Value.ShouldBe("false");
        ((string?)mode.Attribute("Condition"))
            .ShouldBe("'$(UsePublishedFhirPkg)' == ''");
        XElement sourceReference = project.Descendants("ProjectReference")
            .Single(reference => reference.Attribute("Include")?.Value
                == @"..\..\src\FhirPkg\FhirPkg.csproj");
        ((string?)sourceReference.Parent!.Attribute("Condition"))
            .ShouldBe("'$(UsePublishedFhirPkg)' != 'true'");
        XElement package = project.Descendants("PackageReference")
            .Single(reference => reference.Attribute("Include")?.Value == "fhir-pkg-lib");
        XElement publishedGroup = package.Parent.ShouldNotBeNull();
        ((string?)publishedGroup.Attribute("Condition"))
            .ShouldBe("'$(UsePublishedFhirPkg)' == 'true'");
        XElement removedReference =
            publishedGroup.Elements("ProjectReference").ShouldHaveSingleItem();
        ((string?)removedReference.Attribute("Remove"))
            .ShouldBe(@"..\..\src\FhirPkg\FhirPkg.csproj");
        XElement validation = project.Descendants("Target")
            .Single(target => target.Attribute("Name")?.Value
                == "ValidatePublishedQualificationProperties");
        ((string?)validation.Attribute("Condition"))
            .ShouldBe("'$(UsePublishedFhirPkg)' == 'true'");
        ((string?)validation.Attribute("BeforeTargets"))
            .ShouldBe("CollectPackageReferences");
        XElement error = validation.Elements("Error").ShouldHaveSingleItem();
        ((string?)error.Attribute("Condition"))
            .ShouldBe("'$(FhirPkgQualificationPackageVersion)' == ''");
        ((string?)error.Attribute("Text")).ShouldBe(
            "FhirPkgQualificationPackageVersion is required when UsePublishedFhirPkg=true.");
    }

    private static void AssertSetupSdks(string job, string[] expected)
    {
        string setup = GetSteps(job).Where(step => step.StartsWith(
            "      - uses: actions/setup-dotnet@v5\n",
            StringComparison.Ordinal)).ShouldHaveSingleItem();
        string versions = GetSection(setup, "          dotnet-version: ", null)
            ["          dotnet-version: ".Length..].Trim();
        string[] actual = versions.StartsWith('|')
            ? versions[1..].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).ToArray()
            : [versions];
        actual.ShouldBe(expected);
        Regex.Matches(job, "uses: actions/setup-dotnet@").Count.ShouldBe(1);
    }

    private static void AssertUnpinned(string step)
    {
        step = Regex.Replace(step, @"^ *#.*$", string.Empty, RegexOptions.Multiline);
        step.ShouldNotContain("--framework");
        step.ShouldNotContain("-p:TargetFramework");
    }

    private static void AssertFrameworkSelectors(string job, params string[] allowed)
    {
        foreach (Match match in Regex.Matches(
            job,
            @"--framework (?<framework>\$\{\{ matrix\.framework \}\}|\S+)"))
        {
            allowed.ShouldContain(match.Groups["framework"].Value);
        }
    }

    private static XElement GetProperty(XDocument project, string name) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == name)
            .ShouldHaveSingleItem();

    private static XDocument ReadXml(string path) =>
        XDocument.Parse(ReadBuildContract(path));

    private static string ReadBuildContract(string path) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "BuildContracts",
            path.Replace('/', Path.DirectorySeparatorChar))).ReplaceLineEndings("\n");

    private static string ReadWorkflow(string fileName) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "ReleaseContracts",
            fileName)).ReplaceLineEndings("\n");

    private static Dictionary<string, string> GetJobs(string workflow)
    {
        string jobs = GetSection(workflow, "jobs:\n", null);
        Match[] markers = Regex.Matches(
            jobs, @"^  (?<name>[\w-]+):\n", RegexOptions.Multiline)
            .Cast<Match>().ToArray();
        Dictionary<string, string> sections = new(StringComparer.Ordinal);
        for (int index = 0; index < markers.Length; index++)
        {
            int end = index + 1 < markers.Length
                ? markers[index + 1].Index
                : jobs.Length;
            sections.Add(
                markers[index].Groups["name"].Value,
                jobs[markers[index].Index..end]);
        }

        return sections;
    }

    private static string[] GetSteps(string job)
    {
        Match[] markers = Regex.Matches(
            job, @"^      - (?:name:|uses:)", RegexOptions.Multiline)
            .Cast<Match>().ToArray();
        return markers.Select((marker, index) =>
            job[marker.Index..(index + 1 < markers.Length
                ? markers[index + 1].Index
                : job.Length)]).ToArray();
    }

    private static string GetStep(string job, string name) =>
        GetSteps(job).Where(step => step.StartsWith(
            "      - name: " + name + "\n",
            StringComparison.Ordinal)).ShouldHaveSingleItem();

    private static string GetSection(string text, string start, string? end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        startIndex.ShouldBeGreaterThanOrEqualTo(0, $"Missing section '{start}'.");
        int endIndex = end is null
            ? text.Length
            : text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        endIndex.ShouldBeGreaterThan(startIndex, $"Missing section end '{end}'.");
        return text[startIndex..endIndex];
    }
}
