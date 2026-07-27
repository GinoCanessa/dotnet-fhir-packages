// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Generic;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirPkg.Release.Validation;

internal interface IReleaseCandidateValidator
{
    ReleaseCandidateValidationResult Validate(
        string candidateDirectory,
        string version,
        string tag,
        string repositoryCommit,
        string? expectedSdkPackageSha256 = null,
        string? expectedCliPackageSha256 = null);
}

internal sealed class ReleaseCandidateValidator : IReleaseCandidateValidator
{
    private static readonly Regex Sha512ManifestEntryRegex = new(
        "^(?<hash>[0-9a-f]{128})  (?<name>[^\\\\/]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReleasePackageValidator _packageValidator;
    private readonly IReleaseSymbolPackageValidator _symbolPackageValidator;

    internal ReleaseCandidateValidator()
        : this(
            new ReleasePackageValidator(),
            new ReleaseSymbolPackageValidator())
    {
    }

    internal ReleaseCandidateValidator(
        IReleasePackageValidator packageValidator,
        IReleaseSymbolPackageValidator symbolPackageValidator)
    {
        _packageValidator =
            packageValidator ??
            throw new ArgumentNullException(nameof(packageValidator));
        _symbolPackageValidator =
            symbolPackageValidator ??
            throw new ArgumentNullException(
                nameof(symbolPackageValidator));
    }

    public ReleaseCandidateValidationResult Validate(
        string candidateDirectory,
        string version,
        string tag,
        string repositoryCommit,
        string? expectedSdkPackageSha256 = null,
        string? expectedCliPackageSha256 = null)
    {
        string fullCandidateDirectory = Path.GetFullPath(candidateDirectory);
        ReleasePackageDescriptor[] packages =
        [
            new ReleasePackageDescriptor(
                "sdk",
                "fhir-pkg-lib",
                $"fhir-pkg-lib.{version}.nupkg",
                $"fhir-pkg-lib.{version}.snupkg",
                $"fhir-pkg-lib.{version}.sha512",
                expectedSdkPackageSha256),
            new ReleasePackageDescriptor(
                "cli",
                "fhir-pkg-cli",
                $"fhir-pkg-cli.{version}.nupkg",
                $"fhir-pkg-cli.{version}.snupkg",
                $"fhir-pkg-cli.{version}.sha512",
                expectedCliPackageSha256),
        ];

        if (!Directory.Exists(fullCandidateDirectory))
        {
            throw new ReleaseValidationException(
                $"Candidate directory '{fullCandidateDirectory}' does not exist.");
        }

        ValidateInventory(fullCandidateDirectory, packages);

        Dictionary<string, CandidatePackageValidationResult> results =
            new(StringComparer.Ordinal);
        foreach (ReleasePackageDescriptor package in packages)
        {
            CandidatePackageValidationResult result = ValidatePackage(
                fullCandidateDirectory,
                package,
                version,
                repositoryCommit);
            results.Add(package.PackageId, result);
        }

        ValidateReleaseMetadata(
            Path.Combine(fullCandidateDirectory, "release-metadata.json"),
            version,
            tag,
            repositoryCommit,
            packages,
            results);

        CandidatePackageValidationResult sdkResult = results["fhir-pkg-lib"];
        CandidatePackageValidationResult cliResult = results["fhir-pkg-cli"];
        ValidateSynchronizedAssemblies(sdkResult, cliResult);

        return new ReleaseCandidateValidationResult(
            sdkResult.PackagePath,
            sdkResult.SymbolsPath,
            sdkResult.ManifestPath,
            sdkResult.PackageSha256,
            cliResult.PackagePath,
            cliResult.SymbolsPath,
            cliResult.ManifestPath,
            cliResult.PackageSha256);
    }

    private static void ValidateInventory(
        string fullCandidateDirectory,
        IReadOnlyList<ReleasePackageDescriptor> packages)
    {
        List<string> actualFiles = [];
        foreach (string filePath in Directory.GetFiles(
                     fullCandidateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            actualFiles.Add(
                Path.GetRelativePath(fullCandidateDirectory, filePath)
                    .Replace('\\', '/'));
        }

        actualFiles.Sort(StringComparer.Ordinal);

        List<string> expectedFiles =
        [
            packages[0].PackageName,
            packages[0].SymbolsName,
            packages[0].ManifestName,
            packages[1].PackageName,
            packages[1].SymbolsName,
            packages[1].ManifestName,
            "release-metadata.json",
        ];
        expectedFiles.Sort(StringComparer.Ordinal);

        string actualInventory = string.Join('\n', actualFiles);
        string expectedInventory = string.Join('\n', expectedFiles);
        if (!string.Equals(
                actualInventory,
                expectedInventory,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                "Release candidate inventory must contain exactly: " +
                $"{string.Join(", ", expectedFiles)}. Found: " +
                $"{string.Join(", ", actualFiles)}.");
        }
    }

    private CandidatePackageValidationResult ValidatePackage(
        string fullCandidateDirectory,
        ReleasePackageDescriptor package,
        string version,
        string repositoryCommit)
    {
        string packagePath = Path.Combine(
            fullCandidateDirectory,
            package.PackageName);
        string symbolsPath = Path.Combine(
            fullCandidateDirectory,
            package.SymbolsName);
        string manifestPath = Path.Combine(
            fullCandidateDirectory,
            package.ManifestName);

        _packageValidator.Validate(
            package.PackageId,
            packagePath,
            version,
            repositoryCommit);
        _symbolPackageValidator.Validate(
            package.PackageId,
            symbolsPath,
            version,
            repositoryCommit);

        Dictionary<string, string> manifest = ReadSha512Manifest(
            manifestPath,
            [package.PackageName, package.SymbolsName]);

        ValidateSha512(packagePath, package.PackageName, manifest);
        ValidateSha512(symbolsPath, package.SymbolsName, manifest);

        string packageSha256 = GetFileSha256(packagePath);
        string symbolsSha256 = GetFileSha256(symbolsPath);
        if (!string.IsNullOrWhiteSpace(package.ExpectedSha256) &&
            !string.Equals(
                packageSha256,
                package.ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"{package.PackageId} SHA-256 '{packageSha256}' does not match '{package.ExpectedSha256}'.");
        }

        return new CandidatePackageValidationResult(
            package.Role,
            package.PackageId,
            packagePath,
            symbolsPath,
            manifestPath,
            packageSha256,
            symbolsSha256,
            manifest[package.PackageName],
            manifest[package.SymbolsName]);
    }

    private static Dictionary<string, string> ReadSha512Manifest(
        string manifestPath,
        IReadOnlyList<string> expectedNames)
    {
        List<string> manifestLines = [];
        foreach (string line in File.ReadAllLines(manifestPath))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                manifestLines.Add(line);
            }
        }

        if (manifestLines.Count != expectedNames.Count)
        {
            throw new ReleaseValidationException(
                $"Expected {expectedNames.Count} SHA-512 manifest entries in '{manifestPath}', found {manifestLines.Count}.");
        }

        Dictionary<string, string> manifest =
            new(StringComparer.Ordinal);
        foreach (string line in manifestLines)
        {
            Match match = Sha512ManifestEntryRegex.Match(line);
            if (!match.Success)
            {
                throw new ReleaseValidationException(
                    $"Invalid SHA-512 manifest entry '{line}'.");
            }

            string name = match.Groups["name"].Value;
            if (!manifest.TryAdd(name, match.Groups["hash"].Value))
            {
                throw new ReleaseValidationException(
                    $"Duplicate SHA-512 manifest entry '{name}'.");
            }
        }

        foreach (string expectedName in expectedNames)
        {
            if (!manifest.ContainsKey(expectedName))
            {
                throw new ReleaseValidationException(
                    $"SHA-512 manifest is missing '{expectedName}'.");
            }
        }

        return manifest;
    }

    private static void ValidateSha512(
        string path,
        string name,
        IReadOnlyDictionary<string, string> manifest)
    {
        string actualSha512 = GetFileSha512(path);
        if (!string.Equals(
                actualSha512,
                manifest[name],
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"SHA-512 mismatch for '{name}'.");
        }
    }

    private static void ValidateReleaseMetadata(
        string metadataPath,
        string version,
        string tag,
        string repositoryCommit,
        IReadOnlyList<ReleasePackageDescriptor> packages,
        IReadOnlyDictionary<string, CandidatePackageValidationResult> results)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(metadataPath));
        JsonElement root = document.RootElement;

        if (!TryGetString(root, "version", out string? metadataVersion) ||
            !TryGetString(root, "tag", out string? metadataTag) ||
            !TryGetString(
                root,
                "repositoryCommit",
                out string? metadataRepositoryCommit) ||
            !TryGetString(root, "feed", out string? metadataFeed) ||
            !string.Equals(metadataVersion, version, StringComparison.Ordinal) ||
            !string.Equals(metadataTag, tag, StringComparison.Ordinal) ||
            !string.Equals(
                metadataRepositoryCommit,
                repositoryCommit,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadataFeed,
                "https://api.nuget.org/v3/index.json",
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                "Release metadata identity does not match the candidate.");
        }

        int metadataPackageCount = 0;
        JsonElement metadataPackages = default;
        if (TryGetProperty(
                root,
                "packages",
                out JsonElement packagesElement) &&
            packagesElement.ValueKind == JsonValueKind.Array)
        {
            metadataPackages = packagesElement;
            metadataPackageCount = packagesElement.GetArrayLength();
        }

        if (metadataPackageCount != 2)
        {
            throw new ReleaseValidationException(
                "Release metadata must contain two package records, found " +
                $"{metadataPackageCount}.");
        }

        foreach (ReleasePackageDescriptor package in packages)
        {
            List<JsonElement> matches = [];
            foreach (JsonElement metadataPackage in
                     metadataPackages.EnumerateArray())
            {
                if (TryGetString(
                        metadataPackage,
                        "packageId",
                        out string? packageId) &&
                    string.Equals(
                        packageId,
                        package.PackageId,
                        StringComparison.Ordinal))
                {
                    matches.Add(metadataPackage);
                }
            }

            if (matches.Count != 1)
            {
                throw new ReleaseValidationException(
                    $"Release metadata must contain one '{package.PackageId}' record.");
            }

            CandidatePackageValidationResult result =
                results[package.PackageId];
            JsonElement record = matches[0];
            if (!TryGetString(
                    record,
                    "packageFile",
                    out string? packageFile) ||
                !TryGetString(
                    record,
                    "symbolsFile",
                    out string? symbolsFile) ||
                !TryGetString(
                    record,
                    "packageSha256",
                    out string? packageSha256) ||
                !TryGetString(
                    record,
                    "symbolsSha256",
                    out string? symbolsSha256) ||
                !TryGetString(
                    record,
                    "packageSha512",
                    out string? packageSha512) ||
                !TryGetString(
                    record,
                    "symbolsSha512",
                    out string? symbolsSha512) ||
                !string.Equals(
                    packageFile,
                    package.PackageName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    symbolsFile,
                    package.SymbolsName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    packageSha256,
                    result.PackageSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    symbolsSha256,
                    result.SymbolsSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    packageSha512,
                    result.PackageSha512,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    symbolsSha512,
                    result.SymbolsSha512,
                    StringComparison.Ordinal))
            {
                throw new ReleaseValidationException(
                    $"Release metadata hashes or filenames do not match '{package.PackageId}'.");
            }
        }
    }

    private static void ValidateSynchronizedAssemblies(
        CandidatePackageValidationResult sdkResult,
        CandidatePackageValidationResult cliResult)
    {
        string[] frameworks = ["net8.0", "net9.0", "net10.0"];
        foreach (string framework in frameworks)
        {
            string sdkHash = GetZipEntrySha256(
                sdkResult.PackagePath,
                $"lib/{framework}/FhirPkg.dll");
            string cliHash = GetZipEntrySha256(
                cliResult.PackagePath,
                $"tools/{framework}/any/FhirPkg.dll");
            if (!string.Equals(
                    sdkHash,
                    cliHash,
                    StringComparison.Ordinal))
            {
                throw new ReleaseValidationException(
                    "The CLI embedded SDK assembly for " +
                    $"'{framework}' does not match the SDK package.");
            }
        }
    }

    private static string GetZipEntrySha256(
        string packagePath,
        string entryName)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        List<ZipArchiveEntry> matches = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.Equals(
                    entry.FullName,
                    entryName,
                    StringComparison.Ordinal))
            {
                matches.Add(entry);
            }
        }

        if (matches.Count != 1)
        {
            throw new ReleaseValidationException(
                $"Expected one '{entryName}' entry in '{packagePath}'.");
        }

        using Stream stream = matches[0].Open();
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string GetFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string GetFileSha512(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream))
            .ToLowerInvariant();
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!TryGetProperty(
                element,
                propertyName,
                out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record ReleasePackageDescriptor(
        string Role,
        string PackageId,
        string PackageName,
        string SymbolsName,
        string ManifestName,
        string? ExpectedSha256);

    private sealed record CandidatePackageValidationResult(
        string Role,
        string PackageId,
        string PackagePath,
        string SymbolsPath,
        string ManifestPath,
        string PackageSha256,
        string SymbolsSha256,
        string PackageSha512,
        string SymbolsSha512);
}
