// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Xml;

namespace FhirPkg.Release.Validation;

internal interface IReleasePackageValidator
{
    ReleaseArtifactValidationResult Validate(
        string packageId,
        string path,
        string version,
        string repositoryCommit);
}

internal sealed class ReleasePackageValidator
    : IReleasePackageValidator
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
                $"Package '{fullPackagePath}' does not exist.");
        }

        string expectedFileName = $"{packageId}.{version}.nupkg";
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
                "The package nuspec has no metadata element.");

        ValidateMetadata(
            packageId,
            version,
            repositoryCommit,
            nuspec.Metadata,
            nuspec.Namespaces);

        HashSet<string> entryNames =
            ReleasePackageValidationCommon.CreateEntryNameSet(
                archive);
        if (string.Equals(
                packageId,
                ReleasePackageValidationCommon.SdkPackageId,
                StringComparison.Ordinal))
        {
            ValidateSdkPackageEntries(entryNames);
        }
        else
        {
            ValidateCliPackage(
                archive,
                nuspec.Metadata,
                nuspec.Namespaces,
                entryNames);
        }

        return new ReleaseArtifactValidationResult(
            fullPackagePath,
            ReleasePackageValidationCommon.ComputeSha256(
                fullPackagePath));
    }

    private static void ValidateCliPackage(
        ZipArchive archive,
        XmlElement metadata,
        XmlNamespaceManager namespaces,
        HashSet<string> entryNames)
    {
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
                "DotnetTool",
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                "The CLI package type must be DotnetTool.");
        }

        XmlNodeList? dependencies =
            ReleasePackageValidationCommon.SelectNodes(
                metadata,
                "n:dependencies//n:dependency",
                namespaces);
        if (dependencies is not null &&
            dependencies.Count != 0)
        {
            throw new ReleaseValidationException(
                "The CLI package must not declare package dependencies.");
        }

        string[] requiredToolFiles =
        [
            "DotnetToolSettings.xml",
            "FhirPkg.Cli.dll",
            "FhirPkg.Cli.deps.json",
            "FhirPkg.Cli.runtimeconfig.json",
            "FhirPkg.dll",
        ];
        foreach (string framework in
                 ReleasePackageValidationCommon.Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            foreach (string toolFile in requiredToolFiles)
            {
                string requiredEntry = $"{toolRoot}/{toolFile}";
                if (!entryNames.Contains(requiredEntry))
                {
                    throw new ReleaseValidationException(
                        $"The package is missing '{requiredEntry}'.");
                }
            }

            ValidateToolSettings(archive, framework, toolRoot);
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
                $"The package id is not '{packageId}'.");
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
                $"The nuspec version does not match '{version}'.");
        }

        XmlElement? repository =
            ReleasePackageValidationCommon.SelectElement(
                metadata,
                "n:repository",
                namespaces);
        if (repository is null)
        {
            throw new ReleaseValidationException(
                "The package nuspec has no repository metadata.");
        }

        if (!string.Equals(
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
                "The package repository metadata does not match the release commit.");
        }
    }

    private static void ValidateSdkPackageEntries(
        HashSet<string> entryNames)
    {
        foreach (string framework in
                 ReleasePackageValidationCommon.Frameworks)
        {
            string requiredEntry = $"lib/{framework}/FhirPkg.dll";
            if (!entryNames.Contains(requiredEntry))
            {
                throw new ReleaseValidationException(
                    $"The package is missing '{requiredEntry}'.");
            }
        }
    }

    private static void ValidateToolSettings(
        ZipArchive archive,
        string framework,
        string toolRoot)
    {
        string settingsPath =
            $"{toolRoot}/DotnetToolSettings.xml";
        List<ZipArchiveEntry> settingsEntries = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.Equals(
                    entry.FullName,
                    settingsPath,
                    StringComparison.Ordinal))
            {
                settingsEntries.Add(entry);
            }
        }

        if (settingsEntries.Count != 1)
        {
            throw new ReleaseValidationException(
                $"Expected one tool settings file for '{framework}'.");
        }

        XmlDocument settings =
            ReleasePackageValidationCommon.LoadXml(
                settingsEntries[0]);
        XmlNodeList? commands =
            settings.SelectNodes(
                "/DotNetCliTool/Commands/Command");
        string commandName = string.Empty;
        string entryPoint = string.Empty;
        string runner = string.Empty;
        if (commands is not null &&
            commands.Count == 1)
        {
            XmlNode? command = commands[0];
            if (command is not null)
            {
                commandName =
                    ReleasePackageValidationCommon.GetAttribute(
                        command,
                        "Name");
                entryPoint =
                    ReleasePackageValidationCommon.GetAttribute(
                        command,
                        "EntryPoint");
                runner =
                    ReleasePackageValidationCommon.GetAttribute(
                        command,
                        "Runner");
            }
        }

        if (commands is null ||
            commands.Count != 1 ||
            !string.Equals(
                commandName,
                "fhir-pkg",
                StringComparison.Ordinal) ||
            !string.Equals(
                entryPoint,
                "FhirPkg.Cli.dll",
                StringComparison.Ordinal) ||
            !string.Equals(
                runner,
                "dotnet",
                StringComparison.Ordinal))
        {
            throw new ReleaseValidationException(
                $"The tool settings for '{framework}' are invalid.");
        }
    }
}
