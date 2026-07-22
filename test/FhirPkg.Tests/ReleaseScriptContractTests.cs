// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseScriptContractTests
{
    private const string Version = "2099.101.1";
    private const string Tag = "v2099.101.1";
    private const string RepositoryCommit =
        "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ReleaseCandidate_AcceptsSynchronizedSdkAndCliArtifacts()
    {
        string candidateDirectory = CreateCandidate();
        try
        {
            ScriptResult result = InvokeCandidate(candidateDirectory);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain(
                "Verified synchronized release candidate");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseCandidate_RejectsCliSdkAssemblyMismatch()
    {
        string candidateDirectory =
            CreateCandidate(mismatchCliAssembly: true);
        try
        {
            ScriptResult result = InvokeCandidate(candidateDirectory);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain(
                "embedded SDK assembly for 'net9.0' does not match");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseCandidate_RejectsMissingOrUnexpectedArtifact()
    {
        string missingDirectory = CreateCandidate();
        string unexpectedDirectory = CreateCandidate();
        try
        {
            File.Delete(
                Path.Combine(
                    missingDirectory,
                    $"fhir-pkg-lib.{Version}.sha512"));
            ScriptResult missingResult =
                InvokeCandidate(missingDirectory);

            missingResult.ExitCode.ShouldNotBe(0);
            missingResult.Output.ShouldContain(
                "inventory must contain exactly");

            File.WriteAllText(
                Path.Combine(unexpectedDirectory, "unexpected.txt"),
                "unexpected",
                Encoding.ASCII);
            ScriptResult unexpectedResult =
                InvokeCandidate(unexpectedDirectory);

            unexpectedResult.ExitCode.ShouldNotBe(0);
            unexpectedResult.Output.ShouldContain(
                "inventory must contain exactly");
        }
        finally
        {
            Directory.Delete(missingDirectory, recursive: true);
            Directory.Delete(unexpectedDirectory, recursive: true);
        }
    }

    private static ScriptResult InvokeCandidate(
        string candidateDirectory)
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "ReleaseContracts",
            "Scripts",
            "Test-ReleaseCandidate.ps1");
        ProcessStartInfo startInfo = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-CandidateDirectory");
        startInfo.ArgumentList.Add(candidateDirectory);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(Version);
        startInfo.ArgumentList.Add("-Tag");
        startInfo.ArgumentList.Add(Tag);
        startInfo.ArgumentList.Add("-RepositoryCommit");
        startInfo.ArgumentList.Add(RepositoryCommit);
        startInfo.Environment["GITHUB_OUTPUT"] = string.Empty;

        using Process process =
            Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start pwsh.");
        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardError =
            process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "Release candidate validation timed out.");
        }

        string output = string.Concat(
            standardOutput.GetAwaiter().GetResult(),
            Environment.NewLine,
            standardError.GetAwaiter().GetResult());
        return new ScriptResult(process.ExitCode, output);
    }

    private static string CreateCandidate(
        bool mismatchCliAssembly = false)
    {
        string candidateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-release-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateDirectory);

        string sdkPackageName = $"fhir-pkg-lib.{Version}.nupkg";
        string sdkSymbolsName = $"fhir-pkg-lib.{Version}.snupkg";
        string cliPackageName = $"fhir-pkg-cli.{Version}.nupkg";
        string cliSymbolsName = $"fhir-pkg-cli.{Version}.snupkg";
        string sdkPackagePath =
            Path.Combine(candidateDirectory, sdkPackageName);
        string sdkSymbolsPath =
            Path.Combine(candidateDirectory, sdkSymbolsName);
        string cliPackagePath =
            Path.Combine(candidateDirectory, cliPackageName);
        string cliSymbolsPath =
            Path.Combine(candidateDirectory, cliSymbolsName);

        CreateSdkPackage(sdkPackagePath);
        CreateSdkSymbolsPackage(sdkSymbolsPath);
        CreateCliPackage(cliPackagePath, mismatchCliAssembly);
        CreateCliSymbolsPackage(cliSymbolsPath);
        WriteManifest(
            Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{Version}.sha512"),
            (sdkPackageName, sdkPackagePath),
            (sdkSymbolsName, sdkSymbolsPath));
        WriteManifest(
            Path.Combine(
                candidateDirectory,
                $"fhir-pkg-cli.{Version}.sha512"),
            (cliPackageName, cliPackagePath),
            (cliSymbolsName, cliSymbolsPath));

        PackageMetadata[] packages =
        [
            CreateMetadata(
                "fhir-pkg-lib",
                sdkPackageName,
                sdkPackagePath,
                sdkSymbolsName,
                sdkSymbolsPath),
            CreateMetadata(
                "fhir-pkg-cli",
                cliPackageName,
                cliPackagePath,
                cliSymbolsName,
                cliSymbolsPath),
        ];
        ReleaseMetadata metadata = new(
            Version,
            Tag,
            RepositoryCommit,
            "https://api.nuget.org/v3/index.json",
            packages);
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        File.WriteAllText(
            Path.Combine(candidateDirectory, "release-metadata.json"),
            JsonSerializer.Serialize(metadata, options),
            Encoding.UTF8);

        return candidateDirectory;
    }

    private static void CreateSdkPackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-lib.nuspec",
            CreateNuspec("fhir-pkg-lib"));
        foreach (string framework in Frameworks)
        {
            AddBytes(
                archive,
                $"lib/{framework}/FhirPkg.dll",
                GetSdkAssembly(framework));
        }
    }

    private static void CreateSdkSymbolsPackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-lib.nuspec",
            CreateNuspec("fhir-pkg-lib", "SymbolsPackage"));
        foreach (string framework in Frameworks)
        {
            AddText(
                archive,
                $"lib/{framework}/FhirPkg.pdb",
                $"sdk-pdb-{framework}");
        }
    }

    private static void CreateCliPackage(
        string path,
        bool mismatchCliAssembly)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-cli.nuspec",
            CreateNuspec("fhir-pkg-cli", "DotnetTool"));
        foreach (string framework in Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            AddText(
                archive,
                $"{toolRoot}/DotnetToolSettings.xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands>
                    <Command Name="fhir-pkg" EntryPoint="FhirPkg.Cli.dll" Runner="dotnet" />
                  </Commands>
                </DotNetCliTool>
                """);
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.dll",
                $"cli-{framework}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.deps.json",
                "{}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.runtimeconfig.json",
                "{}");
            byte[] assembly =
                mismatchCliAssembly && framework == "net9.0"
                    ? Encoding.ASCII.GetBytes("mismatched-sdk")
                    : GetSdkAssembly(framework);
            AddBytes(
                archive,
                $"{toolRoot}/FhirPkg.dll",
                assembly);
        }
    }

    private static void CreateCliSymbolsPackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-cli.nuspec",
            CreateNuspec("fhir-pkg-cli", "SymbolsPackage"));
        foreach (string framework in Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.pdb",
                $"cli-pdb-{framework}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.pdb",
                $"sdk-pdb-{framework}");
        }
    }

    private static string CreateNuspec(
        string packageId,
        string? packageType = null)
    {
        string packageTypes = packageType is null
            ? string.Empty
            : $"""
                 <packageTypes>
                   <packageType name="{packageType}" />
                 </packageTypes>
               """;
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{Version}</version>
                    <description>Release contract fixture.</description>
                {packageTypes}
                    <repository type="git" url="https://github.com/GinoCanessa/dotnet-fhir-packages" commit="{RepositoryCommit}" />
                  </metadata>
                </package>
                """;
    }

    private static void AddText(
        ZipArchive archive,
        string entryName,
        string content) =>
        AddBytes(
            archive,
            entryName,
            Encoding.UTF8.GetBytes(content));

    private static void AddBytes(
        ZipArchive archive,
        string entryName,
        byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] GetSdkAssembly(string framework) =>
        Encoding.ASCII.GetBytes($"sdk-{framework}");

    private static void WriteManifest(
        string manifestPath,
        (string Name, string Path) package,
        (string Name, string Path) symbols)
    {
        string[] lines =
        [
            $"{GetHash(package.Path, SHA512.HashData)}  {package.Name}",
            $"{GetHash(symbols.Path, SHA512.HashData)}  {symbols.Name}",
        ];
        File.WriteAllLines(manifestPath, lines, Encoding.ASCII);
    }

    private static PackageMetadata CreateMetadata(
        string packageId,
        string packageName,
        string packagePath,
        string symbolsName,
        string symbolsPath) =>
        new(
            packageId,
            packageName,
            symbolsName,
            GetHash(packagePath, SHA256.HashData),
            GetHash(symbolsPath, SHA256.HashData),
            GetHash(packagePath, SHA512.HashData),
            GetHash(symbolsPath, SHA512.HashData));

    private static string GetHash(
        string path,
        Func<byte[], byte[]> hash) =>
        Convert.ToHexString(hash(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static readonly string[] Frameworks =
        ["net8.0", "net9.0", "net10.0"];

    private sealed record ScriptResult(int ExitCode, string Output);

    private sealed record PackageMetadata(
        string PackageId,
        string PackageFile,
        string SymbolsFile,
        string PackageSha256,
        string SymbolsSha256,
        string PackageSha512,
        string SymbolsSha512);

    private sealed record ReleaseMetadata(
        string Version,
        string Tag,
        string RepositoryCommit,
        string Feed,
        IReadOnlyList<PackageMetadata> Packages);
}
