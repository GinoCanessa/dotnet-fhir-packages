// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace FhirPkg.Release.Validation;

internal static class ReleasePackageValidationCommon
{
    internal const string CliPackageId = "fhir-pkg-cli";
    internal const string RepositoryUrl =
        "https://github.com/GinoCanessa/dotnet-fhir-packages";
    internal const string SdkPackageId = "fhir-pkg-lib";

    internal static readonly string[] Frameworks =
        ["net8.0", "net9.0", "net10.0"];

    internal static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static HashSet<string> CreateEntryNameSet(
        ZipArchive archive)
    {
        HashSet<string> entryNames =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            entryNames.Add(entry.FullName);
        }

        return entryNames;
    }

    internal static void EnsureSupportedPackageId(string packageId)
    {
        if (string.Equals(
                packageId,
                SdkPackageId,
                StringComparison.Ordinal) ||
            string.Equals(
                packageId,
                CliPackageId,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentException(
            "Package id must be 'fhir-pkg-lib' or 'fhir-pkg-cli'.",
            nameof(packageId));
    }

    internal static string GetAttribute(
        XmlNode node,
        string attributeName)
    {
        XmlAttributeCollection? attributes = node.Attributes;
        if (attributes is null)
        {
            return string.Empty;
        }

        XmlAttribute? attribute = attributes[attributeName];
        if (attribute is null)
        {
            return string.Empty;
        }

        return attribute.Value;
    }

    internal static ReleasePackageNuspecContext LoadNuspec(
        ZipArchive archive,
        string missingMetadataMessage)
    {
        List<ZipArchiveEntry> nuspecEntries = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(
                    ".nuspec",
                    StringComparison.OrdinalIgnoreCase))
            {
                nuspecEntries.Add(entry);
            }
        }

        if (nuspecEntries.Count != 1)
        {
            throw new ReleaseValidationException(
                $"Expected one nuspec, found {nuspecEntries.Count}.");
        }

        XmlDocument nuspec = LoadXml(nuspecEntries[0]);
        XmlNamespaceManager namespaces =
            new(nuspec.NameTable);
        string namespaceUri = nuspec.DocumentElement is null
            ? string.Empty
            : nuspec.DocumentElement.NamespaceURI;
        namespaces.AddNamespace("n", namespaceUri);

        XmlElement? metadata = SelectElement(
            nuspec,
            "/n:package/n:metadata",
            namespaces);
        if (metadata is null)
        {
            throw new ReleaseValidationException(
                missingMetadataMessage);
        }

        return new ReleasePackageNuspecContext(
            namespaces,
            metadata);
    }

    internal static XmlDocument LoadXml(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream);
        XmlDocument document = new();
        document.LoadXml(reader.ReadToEnd());
        return document;
    }

    internal static XmlElement? SelectElement(
        XmlNode node,
        string xpath,
        XmlNamespaceManager namespaces) =>
        node.SelectSingleNode(xpath, namespaces) as XmlElement;

    internal static XmlNodeList? SelectNodes(
        XmlNode node,
        string xpath,
        XmlNamespaceManager namespaces) =>
        node.SelectNodes(xpath, namespaces);
}

internal sealed record ReleasePackageNuspecContext(
    XmlNamespaceManager Namespaces,
    XmlElement Metadata);
