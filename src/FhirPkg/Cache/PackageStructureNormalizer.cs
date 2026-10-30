// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Cache;

internal static class PackageStructureNormalizer
{
    private const string PackageDirectoryName = "package";
    private const string ManifestFileName = "package.json";

    internal static void WrapLegacyRoot(
        string extractedPath,
        IPackageCacheFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        ArgumentNullException.ThrowIfNull(fileOperations);

        // Only a relative path needs OS-backed resolution; a context-bound adapter
        // resolves it under its own policy, other adapters keep pure resolution.
        string stagingPath = !Path.IsPathFullyQualified(extractedPath)
            && fileOperations is SystemPackageCacheFileOperations systemOperations
                ? systemOperations.Context.FileSystem.GetFullPath(extractedPath, cancellationToken)
                : Path.GetFullPath(extractedPath);
        string siblingPath = Path.Combine(
            Path.GetDirectoryName(stagingPath)!,
            $"legacy-{Guid.NewGuid():N}");

        // Keep the complete tree together, including any incidental package entry.
        // The final staging path must remain expanded for transaction recovery.
        // Each step is its own protected operation; a later failure never repeats
        // an earlier completed move.
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.MoveDirectory(stagingPath, siblingPath);
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.CreateDirectory(stagingPath);
        cancellationToken.ThrowIfCancellationRequested();
        fileOperations.MoveDirectory(
            siblingPath,
            Path.Combine(stagingPath, PackageDirectoryName));
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async ValueTask WrapLegacyRootAsync(
        string extractedPath,
        IPackageCacheFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        ArgumentNullException.ThrowIfNull(fileOperations);
        string stagingPath = !Path.IsPathFullyQualified(extractedPath)
            && fileOperations is SystemPackageCacheFileOperations systemOperations
                ? await systemOperations.Context.FileSystem.GetFullPathAsync(extractedPath, cancellationToken)
                    .ConfigureAwait(false)
                : Path.GetFullPath(extractedPath);
        string siblingPath = Path.Combine(
            Path.GetDirectoryName(stagingPath)!,
            $"legacy-{Guid.NewGuid():N}");

        // Match the synchronous boundary: finish each mutation's required
        // barriers before observing cancellation between the three operations.
        cancellationToken.ThrowIfCancellationRequested();
        await fileOperations.MoveDirectoryAsync(stagingPath, siblingPath, CancellationToken.None)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await fileOperations.CreateDirectoryAsync(stagingPath, CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await fileOperations.MoveDirectoryAsync(
                siblingPath, Path.Combine(stagingPath, PackageDirectoryName), CancellationToken.None)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static PackageArchiveLayoutResult ValidateAndNormalize(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive) =>
        ValidateAndNormalize(
            extractedPath,
            inventory,
            directive,
            FileSystemContext.CreateDefault());

    internal static PackageArchiveLayoutResult ValidateAndNormalize(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive,
        FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PackageArchiveLayoutResult layout = ValidateLayout(extractedPath, inventory, directive, out bool normalizeRoots);
        if (normalizeRoots)
            _ = NormalizeRootEntries(extractedPath, context);
        return layout;
    }

    internal static async ValueTask<PackageArchiveLayoutResult> ValidateAndNormalizeAsync(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        PackageArchiveLayoutResult layout = ValidateLayout(extractedPath, inventory, directive, out bool normalizeRoots);
        if (normalizeRoots)
            await NormalizeRootEntriesAsync(extractedPath, context, cancellationToken).ConfigureAwait(false);
        return layout;
    }

    private static PackageArchiveLayoutResult ValidateLayout(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive,
        out bool normalizeRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        ArgumentNullException.ThrowIfNull(inventory);
        normalizeRoots = false;

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

            normalizeRoots = ancillaryRoots.Count > 0;
            string contentPath = Path.Combine(extractedPath, PackageDirectoryName);
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

            normalizeRoots = true;
            string contentPath = Path.Combine(extractedPath, PackageDirectoryName);
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

    private static string NormalizeRootEntries(
        string extractedPath,
        FileSystemContext context)
    {
        ResilientFileSystem fileSystem = context.FileSystem;
        string contentPath = Path.Combine(
            extractedPath,
            PackageDirectoryName);
        fileSystem.CreateDirectory(contentPath);

        // The snapshot is complete before the first move. Each move is then its own
        // operation: a completed child is never revisited when a later one fails.
        string[] entryPaths = fileSystem.SnapshotDirectory(extractedPath);
        foreach (string entryPath in entryPaths)
        {
            string entryName = Path.GetFileName(entryPath);
            if (string.Equals(
                entryName,
                PackageDirectoryName,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destinationPath = Path.Combine(contentPath, entryName);
            // Directories and files share the one move operation, which rejects an
            // occupied destination instead of merging.
            fileSystem.MovePath(entryPath, destinationPath);
        }

        return contentPath;
    }

    private static async ValueTask NormalizeRootEntriesAsync(
        string extractedPath,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ResilientFileSystem fileSystem = context.FileSystem;
        string contentPath = Path.Combine(extractedPath, PackageDirectoryName);
        cancellationToken.ThrowIfCancellationRequested();
        await fileSystem.CreateDirectoryAsync(contentPath, CancellationToken.None).ConfigureAwait(false);
        string[] entryPaths = await fileSystem.SnapshotDirectoryAsync(extractedPath, cancellationToken)
            .ConfigureAwait(false);
        foreach (string entryPath in entryPaths)
        {
            string entryName = Path.GetFileName(entryPath);
            if (string.Equals(entryName, PackageDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            await fileSystem.MovePathAsync(
                    entryPath, Path.Combine(contentPath, entryName), CancellationToken.None)
                .ConfigureAwait(false);
        }
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
