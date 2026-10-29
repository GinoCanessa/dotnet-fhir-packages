// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;
using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Cache;

internal enum PackageArchiveEntryKind
{
    RegularFile,
    Directory
}

internal sealed class PackageArchiveInventoryNode
{
    private Dictionary<string, PackageArchiveInventoryNode>? _children;

    internal PackageArchiveInventoryNode(
        PackageArchiveInventoryNode? parent,
        string canonicalSegment,
        string exactSegment,
        PackageArchiveEntryKind kind,
        bool isExplicit,
        int depth)
    {
        Parent = parent;
        CanonicalSegment = canonicalSegment;
        ExactSegment = exactSegment;
        Kind = kind;
        IsExplicit = isExplicit;
        Depth = depth;
        TopLevelCanonicalSegment = parent is null
            ? canonicalSegment
            : parent.TopLevelCanonicalSegment;
    }

    internal PackageArchiveInventoryNode? Parent { get; }

    internal string CanonicalSegment { get; }

    internal string ExactSegment { get; }

    internal string TopLevelCanonicalSegment { get; }

    internal PackageArchiveEntryKind Kind { get; }

    internal bool IsExplicit { get; set; }

    internal int Depth { get; }

    internal IEnumerable<PackageArchiveInventoryNode> Children
    {
        get
        {
            if (_children is null)
                return [];

            return _children.Values;
        }
    }

    internal bool TryGetChild(
        string canonicalSegment,
        out PackageArchiveInventoryNode? child)
    {
        if (_children is null)
        {
            child = null;
            return false;
        }

        return _children.TryGetValue(canonicalSegment, out child);
    }

    internal void AddChild(PackageArchiveInventoryNode child)
    {
        _children ??= new Dictionary<string, PackageArchiveInventoryNode>(
            StringComparer.OrdinalIgnoreCase);
        _children.Add(child.CanonicalSegment, child);
    }
}

internal sealed class PackageArchiveInventory
{
    private const long MaximumNodesPerExplicitEntry = 2;
    private const long AbsoluteNodeBudget = 1_000_000;
    private const long AbsolutePathByteBudget = 64L * 1024 * 1024;

    private static readonly UTF8Encoding s_strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Dictionary<string, PackageArchiveInventoryNode> _roots =
        new Dictionary<string, PackageArchiveInventoryNode>(
            StringComparer.OrdinalIgnoreCase);
    private readonly long _nodeBudget;
    private readonly long _pathByteBudget;
    private long _nodeCount;
    private long _pathBytes;

    internal PackageArchiveInventory(PackageInstallLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        long theoreticalNodeMaximum = SaturatingMultiply(
            limits.MaxArchiveEntries,
            limits.MaxArchiveDepth);
        long compactNodeMaximum = SaturatingAdd(
            SaturatingMultiply(
                limits.MaxArchiveEntries,
                MaximumNodesPerExplicitEntry),
            limits.MaxArchiveDepth);
        // One deep entry remains possible, while many entries cannot each
        // retain a unique chain of implicit parent nodes.
        _nodeBudget = Math.Min(
            Math.Min(
                theoreticalNodeMaximum,
                compactNodeMaximum),
            AbsoluteNodeBudget);

        long maximumBytesForOnePath = SaturatingMultiply(
            limits.MaxArchivePathLength,
            8);
        long allEntryPathBytes = SaturatingMultiply(
            limits.MaxArchiveEntries,
            maximumBytesForOnePath);
        long compressedOrSinglePathBudget = Math.Max(
            limits.MaxCompressedBytes,
            maximumBytesForOnePath);
        // Eight bytes per UTF-16 code unit covers strict UTF-8 storage for
        // both canonical and exact segment spellings.
        _pathByteBudget = Math.Min(
            Math.Min(
                allEntryPathBytes,
                compressedOrSinglePathBudget),
            AbsolutePathByteBudget);
    }

    internal IEnumerable<PackageArchiveInventoryNode> Nodes
    {
        get
        {
            Stack<PackageArchiveInventoryNode> pending =
                new Stack<PackageArchiveInventoryNode>(
                    _roots.Values.Reverse());
            while (pending.Count > 0)
            {
                PackageArchiveInventoryNode node = pending.Pop();
                yield return node;

                foreach (PackageArchiveInventoryNode child in node.Children.Reverse())
                    pending.Push(child);
            }
        }
    }

    internal long NodeCount => _nodeCount;

    internal long NodeBudget => _nodeBudget;

    internal long PathBytes => _pathBytes;

    internal long PathByteBudget => _pathByteBudget;

    internal void Add(
        PortableArchivePath path,
        PackageArchiveEntryKind kind,
        string? directive)
    {
        PackageArchiveInventoryNode? parent = null;
        for (int index = 0; index < path.Depth; index++)
        {
            bool isLeaf = index == path.Depth - 1;
            PackageArchiveEntryKind nodeKind = isLeaf
                ? kind
                : PackageArchiveEntryKind.Directory;
            bool isExplicit = isLeaf;
            string canonicalSegment = path.Segments[index];
            string exactSegment = path.ExactSegments[index];

            PackageArchiveInventoryNode? existing;
            bool found = parent is null
                ? _roots.TryGetValue(canonicalSegment, out existing)
                : parent.TryGetChild(canonicalSegment, out existing);
            if (!found)
            {
                PackageArchiveInventoryNode created = CreateNode(
                    parent,
                    canonicalSegment,
                    exactSegment,
                    nodeKind,
                    isExplicit,
                    index + 1,
                    directive);
                if (parent is null)
                    _roots.Add(canonicalSegment, created);
                else
                    parent.AddChild(created);

                parent = created;
                continue;
            }

            if (!string.Equals(
                    existing!.ExactSegment,
                    exactSegment,
                    StringComparison.Ordinal))
            {
                throw InvalidArchive(
                    directive,
                    "Package archive paths collide by case or Unicode normalization.");
            }

            if (!isLeaf)
            {
                if (existing.Kind != PackageArchiveEntryKind.Directory)
                {
                    throw InvalidArchive(
                        directive,
                        "Package archive contains a file-directory or ancestor path conflict.");
                }

                parent = existing;
                continue;
            }

            if (existing.Kind != nodeKind)
            {
                throw InvalidArchive(
                    directive,
                    "Package archive contains a file-directory or ancestor path conflict.");
            }

            if (nodeKind == PackageArchiveEntryKind.Directory
                && !existing.IsExplicit)
            {
                existing.IsExplicit = true;
                parent = existing;
                continue;
            }

            throw InvalidArchive(
                directive,
                "Package archive contains a duplicate normalized path.");
        }
    }

    internal bool TryGetRoot(
        string canonicalSegment,
        out PackageArchiveInventoryNode? root) =>
        _roots.TryGetValue(canonicalSegment, out root);

    internal bool HasTopLevelPath(string canonicalSegment) =>
        _roots.ContainsKey(canonicalSegment);

    internal static PackageArchiveInventory FromFileSystem(
        string extractedPath,
        string? directive) =>
        FromFileSystem(extractedPath, directive, FileSystemContext.CreateDefault());

    internal static PackageArchiveInventory FromFileSystem(
        string extractedPath,
        string? directive,
        FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResilientFileSystem fileSystem = context.FileSystem;
        PackageInstallLimits limits = new PackageInstallLimits();
        PackageArchiveInventory inventory = new PackageArchiveInventory(limits);
        Stack<string> pendingDirectories = new Stack<string>();
        pendingDirectories.Push(extractedPath);

        while (pendingDirectories.Count > 0)
        {
            string directory = pendingDirectories.Pop();
            // One directory is materialized at a time; a failed attempt's private
            // list is discarded, so the inventory only sees a successful snapshot.
            string[] entryPaths = fileSystem.SnapshotDirectory(directory);
            foreach (string entryPath in entryPaths)
            {
                FileAttributes attributes = fileSystem.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidArchive(
                        directive,
                        "Extracted package content contains a symbolic link or reparse point.");
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                string relativePath = Path.GetRelativePath(extractedPath, entryPath);
                PortableArchivePath portablePath = PortableArchivePath.Create(
                    relativePath,
                    isDirectory,
                    directive);
                inventory.Add(
                    portablePath,
                    isDirectory
                        ? PackageArchiveEntryKind.Directory
                        : PackageArchiveEntryKind.RegularFile,
                    directive);

                if (isDirectory)
                    pendingDirectories.Push(entryPath);
            }
        }

        return inventory;
    }

    private PackageArchiveInventoryNode CreateNode(
        PackageArchiveInventoryNode? parent,
        string canonicalSegment,
        string exactSegment,
        PackageArchiveEntryKind kind,
        bool isExplicit,
        int depth,
        string? directive)
    {
        if (_nodeCount >= _nodeBudget)
        {
            throw InventoryEntryLimitExceeded(
                directive,
                _nodeBudget);
        }

        int canonicalBytes = GetStrictUtf8ByteCount(
            canonicalSegment,
            directive);
        int exactBytes = GetStrictUtf8ByteCount(
            exactSegment,
            directive);
        long addedBytes = (long)canonicalBytes + exactBytes;
        if (addedBytes > _pathByteBudget - _pathBytes)
        {
            throw InventoryPathLimitExceeded(
                directive,
                _pathByteBudget);
        }

        _nodeCount++;
        _pathBytes += addedBytes;
        return new PackageArchiveInventoryNode(
            parent,
            canonicalSegment,
            exactSegment,
            kind,
            isExplicit,
            depth);
    }

    private static int GetStrictUtf8ByteCount(
        string value,
        string? directive)
    {
        try
        {
            return s_strictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidArchive,
                PackageInstallStage.ArchiveValidation,
                "Package archive path contains malformed Unicode.",
                directive,
                exception);
        }
    }

    private static long SaturatingMultiply(long left, long right)
    {
        if (left == 0 || right == 0)
            return 0;

        return left > long.MaxValue / right
            ? long.MaxValue
            : left * right;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private static PackageInstallException InventoryEntryLimitExceeded(
        string? directive,
        long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive inventory exceeds its node limit of {limit}.",
            directive);

    private static PackageInstallException InventoryPathLimitExceeded(
        string? directive,
        long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive inventory exceeds its path-byte limit of {limit}.",
            directive);

    private static PackageInstallException InvalidArchive(
        string? directive,
        string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            directive);
}
