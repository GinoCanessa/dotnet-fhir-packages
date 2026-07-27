// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FhirPkg.Tests;

internal static class ReleaseValidationFixture
{
    private const string InvalidRepositoryCommit =
        "fedcba9876543210fedcba9876543210fedcba98";

    internal const string Version = "2099.101.1";
    internal const string Tag = "v2099.101.1";
    internal const string RepositoryCommit =
        "0123456789abcdef0123456789abcdef01234567";

    internal static readonly string[] Frameworks =
        ["net8.0", "net9.0", "net10.0"];

    internal static string CreateCandidate(
        bool mismatchCliAssembly = false,
        bool invalidSdkPackageMetadata = false)
    {
        string candidateDirectory =
            CreateArtifactsDirectory("fhirpkg-release-contract");

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

        CreateSdkPackage(
            sdkPackagePath,
            repositoryCommit: invalidSdkPackageMetadata
                ? InvalidRepositoryCommit
                : RepositoryCommit);
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

    internal static string CreateArtifactsDirectory(
        string namePrefix = "release-validation")
    {
        string artifactsRoot = Path.Combine(
            AppContext.BaseDirectory,
            "ReleaseValidationArtifacts");
        Directory.CreateDirectory(artifactsRoot);

        string directory = Path.Combine(
            artifactsRoot,
            $"{namePrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void CreateSdkPackage(
        string path,
        bool mismatchSdkAssembly = false,
        string? packageId = null,
        string? version = null,
        string? repositoryCommit = null,
        string nuspecEntryName = "fhir-pkg-lib.nuspec")
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            nuspecEntryName,
            CreateNuspec(
                packageId ?? "fhir-pkg-lib",
                version: version,
                repositoryCommit: repositoryCommit));
        foreach (string framework in Frameworks)
        {
            byte[] assembly =
                mismatchSdkAssembly && framework == "net9.0"
                    ? Encoding.ASCII.GetBytes("mismatched-published-sdk")
                    : GetSdkAssembly(framework);
            AddBytes(
                archive,
                $"lib/{framework}/FhirPkg.dll",
                assembly);
        }
    }

    internal static void CreateSdkSymbolsPackage(string path)
    {
        CreateSdkSymbolsPackage(
            path,
            packageType: "SymbolsPackage");
    }

    internal static void CreateSdkSymbolsPackage(
        string path,
        string? packageId = null,
        string? version = null,
        string? repositoryCommit = null,
        string? packageType = "SymbolsPackage",
        string nuspecEntryName = "fhir-pkg-lib.nuspec")
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            nuspecEntryName,
            CreateNuspec(
                packageId ?? "fhir-pkg-lib",
                packageType,
                version,
                repositoryCommit));
        foreach (string framework in Frameworks)
        {
            AddText(
                archive,
                $"lib/{framework}/FhirPkg.pdb",
                $"sdk-pdb-{framework}");
        }
    }

    internal static void CreateCliPackage(
        string path,
        bool mismatchCliAssembly = false,
        string? packageId = null,
        string? version = null,
        string? repositoryCommit = null,
        string? packageType = "DotnetTool",
        bool includeDependency = false,
        string settingsEntryName = "DotnetToolSettings.xml",
        string commandName = "fhir-pkg",
        string entryPoint = "FhirPkg.Cli.dll",
        string runner = "dotnet",
        string nuspecEntryName = "fhir-pkg-cli.nuspec")
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            nuspecEntryName,
            CreateNuspec(
                packageId ?? "fhir-pkg-cli",
                packageType,
                version,
                repositoryCommit,
                includeDependency));
        foreach (string framework in Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            AddText(
                archive,
                $"{toolRoot}/{settingsEntryName}",
                CreateToolSettings(
                    commandName,
                    entryPoint,
                    runner));
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

    internal static void CreateCliSymbolsPackage(string path)
    {
        CreateCliSymbolsPackage(
            path,
            packageType: "SymbolsPackage");
    }

    internal static void CreateCliSymbolsPackage(
        string path,
        string? packageId = null,
        string? version = null,
        string? repositoryCommit = null,
        string? packageType = "SymbolsPackage",
        string nuspecEntryName = "fhir-pkg-cli.nuspec")
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            nuspecEntryName,
            CreateNuspec(
                packageId ?? "fhir-pkg-cli",
                packageType,
                version,
                repositoryCommit));
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
        string? packageType = null,
        string? version = null,
        string? repositoryCommit = null,
        bool includeDependency = false)
    {
        string packageTypes = packageType is null
            ? string.Empty
            : $"""
                 <packageTypes>
                   <packageType name="{packageType}" />
                 </packageTypes>
               """;
        string dependencies = includeDependency
            ? """
                 <dependencies>
                   <group targetFramework="net10.0">
                     <dependency id="Example.Dependency" version="1.0.0" />
                   </group>
                 </dependencies>
               """
            : string.Empty;
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{version ?? Version}</version>
                    <description>Release contract fixture.</description>
                {packageTypes}
                {dependencies}
                    <repository type="git" url="https://github.com/GinoCanessa/dotnet-fhir-packages" commit="{repositoryCommit ?? RepositoryCommit}" />
                  </metadata>
                </package>
                """;
    }

    private static string CreateToolSettings(
        string commandName,
        string entryPoint,
        string runner) =>
        $"""
          <?xml version="1.0" encoding="utf-8"?>
          <DotNetCliTool Version="1">
            <Commands>
              <Command Name="{commandName}" EntryPoint="{entryPoint}" Runner="{runner}" />
            </Commands>
          </DotNetCliTool>
          """;

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
