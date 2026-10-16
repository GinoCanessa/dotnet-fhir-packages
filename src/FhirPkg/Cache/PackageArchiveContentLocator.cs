// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;

namespace FhirPkg.Cache;

internal enum PackageArchiveLayout
{
    Standard,
    Legacy
}

internal sealed record PackageArchiveLayoutResult(
    PackageArchiveLayout Layout,
    string ContentPath,
    string ManifestPath);

internal static class PackageArchiveContentLocator
{
    private const string PackageDirectoryName = "package";
    private const string ManifestFileName = "package.json";

    internal static PackageArchiveLayoutResult Locate(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        ArgumentNullException.ThrowIfNull(inventory);

        bool hasStandardManifest =
            inventory.TryGetRoot(
                PackageDirectoryName,
                out PackageArchiveInventoryNode? packageNode)
            && packageNode is not null
            && packageNode.Kind == PackageArchiveEntryKind.Directory
            && string.Equals(
                packageNode.ExactSegment,
                PackageDirectoryName,
                StringComparison.Ordinal)
            && packageNode.TryGetChild(
                ManifestFileName,
                out PackageArchiveInventoryNode? manifestNode)
            && IsExactRegularManifest(manifestNode);
        bool hasLegacyManifest =
            inventory.TryGetRoot(
                ManifestFileName,
                out PackageArchiveInventoryNode? rootManifestNode)
            && IsExactRegularManifest(rootManifestNode);

        if (hasStandardManifest && hasLegacyManifest)
        {
            throw InvalidLayout(
                directive,
                "Package archive contains more than one candidate package manifest.");
        }

        if (hasStandardManifest)
        {
            string contentPath = Path.Combine(extractedPath, PackageDirectoryName);
            return new PackageArchiveLayoutResult(
                PackageArchiveLayout.Standard,
                contentPath,
                Path.Combine(contentPath, ManifestFileName));
        }

        if (hasLegacyManifest)
        {
            return new PackageArchiveLayoutResult(
                PackageArchiveLayout.Legacy,
                extractedPath,
                Path.Combine(extractedPath, ManifestFileName));
        }

        throw InvalidLayout(
            directive,
            "Package archive must contain exactly one regular package/package.json or root package.json.");
    }

    private static bool IsExactRegularManifest(
        PackageArchiveInventoryNode? node) =>
        node is not null
        && node.Kind == PackageArchiveEntryKind.RegularFile
        && string.Equals(
            node.ExactSegment,
            ManifestFileName,
            StringComparison.Ordinal);

    private static PackageInstallException InvalidLayout(
        string? directive,
        string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            directive);
}
