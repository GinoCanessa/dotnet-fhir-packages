// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Xml;

namespace FhirPkg.Release.Validation;

internal interface IReleaseSymbolPackageValidator
{
    ReleaseArtifactValidationResult Validate(
        string packageId,
        string path,
        string version,
        string repositoryCommit);
}

internal sealed class ReleaseSymbolPackageValidator
    : IReleaseSymbolPackageValidator
{
    public ReleaseArtifactValidationResult Validate(
        string packageId,
        string path,
        string version,
        string repositoryCommit)
    {
        ReleasePackageValidationCommon.EnsureSupportedPackageId(
            packageId);

        string fullPackagePath = Path.GetFullPath(path);
        if (!File.Exists(fullPackagePath))
        {
            throw new ReleaseValidationException(
                $"Symbols package '{fullPackagePath}' does not exist.");
        }

        string expectedFileName = $"{packageId}.{version}.snupkg";
        string actualFileName = Path.GetFileName(fullPackagePath);
        if (!string.Equals(
                actualFileName,
                expectedFileName,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"Expected '{expectedFileName}', found '{actualFileName}'.");
        }

        using ZipArchive archive = ZipFile.OpenRead(fullPackagePath);
        ReleasePackageNuspecContext nuspec =
            ReleasePackageValidationCommon.LoadNuspec(
                archive,
                "The symbols package nuspec has no metadata element.");

        ValidateMetadata(
            packageId,
            version,
            repositoryCommit,
            nuspec.Metadata,
            nuspec.Namespaces);

        HashSet<string> entryNames =
            ReleasePackageValidationCommon.CreateEntryNameSet(
                archive);
        ValidateEntries(packageId, entryNames);

        return new ReleaseArtifactValidationResult(
            fullPackagePath,
            ReleasePackageValidationCommon.ComputeSha256(
                fullPackagePath));
    }

    private static void ValidateEntries(
        string packageId,
        HashSet<string> entryNames)
    {
        foreach (string framework in
                 ReleasePackageValidationCommon.Frameworks)
        {
            string[] requiredEntries =
                string.Equals(
                    packageId,
                    ReleasePackageValidationCommon.SdkPackageId,
                    StringComparison.Ordinal)
                    ? [$"lib/{framework}/FhirPkg.pdb"]
                    :
                    [
                        $"tools/{framework}/any/FhirPkg.Cli.pdb",
                        $"tools/{framework}/any/FhirPkg.pdb",
                    ];

            foreach (string requiredEntry in requiredEntries)
            {
                if (!entryNames.Contains(requiredEntry))
                {
                    throw new ReleaseValidationException(
                        $"The symbols package is missing '{requiredEntry}'.");
                }
            }
        }
    }

    private static void ValidateMetadata(
        string packageId,
        string version,
        string repositoryCommit,
        XmlElement metadata,
        XmlNamespaceManager namespaces)
    {
        XmlElement? id = ReleasePackageValidationCommon.SelectElement(
            metadata,
            "n:id",
            namespaces);
        if (id is null ||
            !string.Equals(
                id.InnerText,
                packageId,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"The symbols package id is not '{packageId}'.");
        }

        XmlElement? packageVersion =
            ReleasePackageValidationCommon.SelectElement(
                metadata,
                "n:version",
                namespaces);
        if (packageVersion is null ||
            !string.Equals(
                packageVersion.InnerText,
                version,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"The symbols package version does not match '{version}'.");
        }

        XmlElement? repository =
            ReleasePackageValidationCommon.SelectElement(
                metadata,
                "n:repository",
                namespaces);
        if (repository is null ||
            !string.Equals(
                repository.GetAttribute("type"),
                "git",
                StringComparison.Ordinal) ||
            !string.Equals(
                repository.GetAttribute("url"),
                ReleasePackageValidationCommon.RepositoryUrl,
                StringComparison.Ordinal) ||
            !string.Equals(
                repository.GetAttribute("commit"),
                repositoryCommit,
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                "The symbols package repository metadata does not match the release commit.");
        }

        XmlNodeList? packageTypes =
            ReleasePackageValidationCommon.SelectNodes(
                metadata,
                "n:packageTypes/n:packageType",
                namespaces);
        string packageTypeName = string.Empty;
        if (packageTypes is not null &&
            packageTypes.Count == 1)
        {
            XmlNode? packageType = packageTypes[0];
            if (packageType is not null)
            {
                packageTypeName =
                    ReleasePackageValidationCommon.GetAttribute(
                        packageType,
                        "name");
            }
        }

        if (packageTypes is null ||
            packageTypes.Count != 1 ||
            !string.Equals(
                packageTypeName,
                "SymbolsPackage",
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                "The symbols package type must be SymbolsPackage.");
        }
    }
}
