// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;

namespace FhirPkg.Cache;

internal static class PackageStructureNormalizer
{
    private const string PackageDirectoryName = "package";
    private const string ManifestFileName = "package.json";

    internal static PackageArchiveLayoutResult ValidateAndNormalize(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        ArgumentNullException.ThrowIfNull(inventory);

        List<PackageArchiveInventoryNode> manifestNodes = inventory.Nodes
            .Where(node =>
                node.Kind == PackageArchiveEntryKind.RegularFile
                && string.Equals(
                    node.CanonicalSegment,
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool hasStandardManifest = manifestNodes.Any(IsStandardManifest);
        bool hasLegacyManifest = manifestNodes.Any(node =>
            node.Depth == 1
            && string.Equals(
                node.CanonicalSegment,
                ManifestFileName,
                StringComparison.Ordinal));

        if (hasStandardManifest && hasLegacyManifest)
        {
            throw InvalidLayout(
                directive,
                "Package archive contains more than one candidate package manifest.");
        }

        if (hasStandardManifest)
        {
            List<PackageArchiveInventoryNode> rootNodes = inventory.Nodes
                .Where(node => node.Depth == 1)
                .ToList();
            bool allRootsAreSupported = rootNodes.All(node =>
                node.Kind == PackageArchiveEntryKind.Directory
                && (string.Equals(
                        node.CanonicalSegment,
                        PackageDirectoryName,
                        StringComparison.Ordinal)
                    || string.Equals(
                        node.CanonicalSegment,
                        "openapi",
                        StringComparison.Ordinal)
                    || string.Equals(
                        node.CanonicalSegment,
                        "other",
                        StringComparison.Ordinal)));
            if (!allRootsAreSupported || manifestNodes.Count != 1)
            {
                throw InvalidLayout(
                    directive,
                    "Standard package archives must contain one package/ tree and manifest, with only optional openapi/ and other/ directory trees.");
            }

            List<PackageArchiveInventoryNode> ancillaryRoots = rootNodes
                .Where(node => !string.Equals(
                    node.CanonicalSegment,
                    PackageDirectoryName,
                    StringComparison.Ordinal))
                .ToList();
            PackageArchiveInventoryNode packageNode = manifestNodes[0].Parent!;
            foreach (PackageArchiveInventoryNode ancillaryRoot in ancillaryRoots)
            {
                if (packageNode.TryGetChild(ancillaryRoot.CanonicalSegment, out _))
                {
                    throw InvalidLayout(
                        directive,
                        $"Package archive ancillary normalization destination 'package/{ancillaryRoot.CanonicalSegment}' is already occupied.");
                }
            }

            string contentPath = ancillaryRoots.Count > 0
                ? NormalizeRootEntries(extractedPath)
                : Path.Combine(extractedPath, PackageDirectoryName);
            return new PackageArchiveLayoutResult(
                PackageArchiveLayout.Standard,
                contentPath,
                Path.Combine(contentPath, ManifestFileName));
        }

        if (hasLegacyManifest)
        {
            if (inventory.HasTopLevelPath(PackageDirectoryName)
                || manifestNodes.Count != 1)
            {
                throw InvalidLayout(
                    directive,
                    "Legacy package archives must not contain a package/ subtree or second manifest.");
            }

            string contentPath = NormalizeRootEntries(extractedPath);
            return new PackageArchiveLayoutResult(
                PackageArchiveLayout.Legacy,
                contentPath,
                Path.Combine(contentPath, ManifestFileName));
        }

        throw InvalidLayout(
            directive,
            "Package archive must contain exactly one regular package.json in an accepted layout.");
    }

    private static bool IsStandardManifest(
        PackageArchiveInventoryNode node) =>
        node.Depth == 2
        && node.Parent is not null
        && string.Equals(
            node.Parent.CanonicalSegment,
            PackageDirectoryName,
            StringComparison.Ordinal)
        && string.Equals(
            node.CanonicalSegment,
            ManifestFileName,
            StringComparison.Ordinal);

    private static string NormalizeRootEntries(string extractedPath)
    {
        string contentPath = Path.Combine(
            extractedPath,
            PackageDirectoryName);
        Directory.CreateDirectory(contentPath);

        foreach (string entryPath in Directory.EnumerateFileSystemEntries(extractedPath)
            .Where(path => !string.Equals(
                path,
                contentPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            string destinationPath = Path.Combine(
                contentPath,
                Path.GetFileName(entryPath));
            FileAttributes attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Move(entryPath, destinationPath);
            else
                File.Move(entryPath, destinationPath);
        }

        return contentPath;
    }

    private static PackageInstallException InvalidLayout(
        string? directive,
        string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            directive);
}
