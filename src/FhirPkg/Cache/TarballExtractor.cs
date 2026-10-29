// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Cache;

/// <summary>
/// Extracts .tgz (gzip-compressed tar) package archives and provides an explicit
/// compatibility transformation for extracted package structures.
/// </summary>
/// <remarks>
/// FHIR packages are distributed as npm-style .tgz tarballs. The expected
/// structure within the archive is a <c>package/</c> subdirectory containing
/// all resource files and the package.json manifest.
/// <see cref="NormalizePackageStructure"/> accepts that standard layout or one
/// unambiguous legacy root layout and rejects all other shapes.
/// Cache installation uses a separate content-location and layout-preservation
/// policy rather than this explicitly requested compatibility transformation.
/// </remarks>
public static class TarballExtractor
{
    /// <summary>
    /// Extracts a .tgz tarball stream to the specified destination directory.
    /// </summary>
    /// <param name="tarballStream">
    /// A readable stream containing the gzip-compressed tar archive. The stream
    /// is consumed from its current position and is left open.
    /// </param>
    /// <param name="destinationPath">The directory to extract files into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tarballStream"/> or <paramref name="destinationPath"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the archive contains invalid or unsafe entry paths.</exception>
    public static async Task ExtractAsync(Stream tarballStream, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        ArgumentNullException.ThrowIfNull(destinationPath);

        PackageInstallLimits limits = new PackageInstallLimits();
        await ExtractAsync(
                tarballStream,
                destinationPath,
                limits,
                directive: null,
                FileSystemContext.CreateDefault(),
                ct)
            .ConfigureAwait(false);
    }

    internal static async Task<ArchiveExtractionMetrics> ExtractAsync(
        Stream tarballStream,
        string destinationPath,
        PackageInstallLimits limits,
        string? directive,
        CancellationToken ct) =>
        await ExtractAsync(
                tarballStream,
                destinationPath,
                limits,
                directive,
                FileSystemContext.CreateDefault(),
                ct)
            .ConfigureAwait(false);

    internal static async Task<ArchiveExtractionMetrics> ExtractAsync(
        Stream tarballStream,
        string destinationPath,
        PackageInstallLimits limits,
        string? directive,
        FileSystemContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(context);
        limits.Validate();

        ResilientFileSystem fileSystem = context.FileSystem;
        await fileSystem.CreateDirectoryAsync(destinationPath, ct).ConfigureAwait(false);
        string fullDestination = await fileSystem.GetFullPathAsync(destinationPath, ct)
            .ConfigureAwait(false);

        // An ordinary caller file is borrowed below gzip/tar, so a failed file
        // read never replays parsing. Other streams are consumed once, as before.
        Stream source = FileSystemContext.ProtectRead(tarballStream, context);
        bool ownsSource = !ReferenceEquals(source, tarballStream);
        try
        {
            return await ExtractCoreAsync(
                    source,
                    fullDestination,
                    limits,
                    directive,
                    fileSystem,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // A borrowed adapter never closes the caller's stream.
            if (ownsSource)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ArchiveExtractionMetrics> ExtractCoreAsync(
        Stream tarballStream,
        string fullDestination,
        PackageInstallLimits limits,
        string? directive,
        ResilientFileSystem fileSystem,
        CancellationToken ct)
    {
        await using GZipStream gzipStream = new GZipStream(tarballStream, CompressionMode.Decompress, leaveOpen: true);
        using TarMetadataPreflightStream preflightStream =
            new TarMetadataPreflightStream(gzipStream, limits, directive);
        await using TarReader tarReader = new TarReader(
            preflightStream,
            leaveOpen: true);

        long expandedBytes = 0;
        long largestEntryBytes = 0;
        int entryCount = 0;
        int maximumPathLength = 0;
        int maximumDepth = 0;
        byte[] buffer = new byte[81_920];
        PackageArchiveInventory inventory = new PackageArchiveInventory(limits);

        while (await ReadNextEntryAsync(
                tarReader,
                directive,
                ct)
            .ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            if (entryCount >= limits.MaxArchiveEntries)
                throw ArchiveEntryCountLimitExceeded(directive, limits.MaxArchiveEntries);

            entryCount++;

            bool isDirectory = entry.EntryType == TarEntryType.Directory;
            if (!isDirectory && !IsRegularFile(entry.EntryType))
                throw UnsupportedArchiveEntry(entry.EntryType, directive);

            PortableArchivePath portablePath = PortableArchivePath.Create(
                entry.Name,
                isDirectory,
                directive);
            portablePath.ValidateExtractionSpelling(directive);

            int pathLength = Math.Max(
                portablePath.CanonicalPath.Length,
                portablePath.ExactSpelling.Length);
            if (pathLength > limits.MaxArchivePathLength)
            {
                throw ArchivePathLengthLimitExceeded(
                    directive,
                    limits.MaxArchivePathLength);
            }

            if (portablePath.Depth > limits.MaxArchiveDepth)
                throw ArchiveDepthLimitExceeded(directive, limits.MaxArchiveDepth);

            maximumPathLength = Math.Max(
                maximumPathLength,
                pathLength);
            maximumDepth = Math.Max(maximumDepth, portablePath.Depth);

            inventory.Add(
                portablePath,
                isDirectory
                    ? PackageArchiveEntryKind.Directory
                    : PackageArchiveEntryKind.RegularFile,
                directive);

            string targetPath = Path.GetFullPath(
                Path.Combine(
                [
                    fullDestination,
                    .. portablePath.ExactSegments
                ]));
            string relativePath = Path.GetRelativePath(fullDestination, targetPath);

            if (Path.IsPathRooted(relativePath)
                || string.Equals(relativePath, "..", StringComparison.Ordinal)
                || relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || relativePath.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                throw UnsafeArchivePath(directive);
            }

            if (isDirectory)
            {
                await fileSystem.CreateDirectoryAsync(targetPath, ct).ConfigureAwait(false);
                continue;
            }

            if (entry.Length < 0)
                throw InvalidArchiveEntryLength(directive);

            if (entry.Length > limits.MaxEntryBytes)
                throw EntrySizeLimitExceeded(directive, limits.MaxEntryBytes);

            if (entry.Length > limits.MaxExpandedBytes - expandedBytes)
                throw ExpandedSizeLimitExceeded(directive, limits.MaxExpandedBytes);

            string? parentDir = Path.GetDirectoryName(targetPath);
            if (parentDir is not null)
                await fileSystem.CreateDirectoryAsync(parentDir, ct).ConfigureAwait(false);

            long entryBytes = 0;
            await using (PositionedFileStream destination =
                await fileSystem.OpenPositionedFileAsync(
                        targetPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        FileOptions.Asynchronous | FileOptions.SequentialScan,
                        ct)
                    .ConfigureAwait(false))
            {
                Stream? dataStream = entry.DataStream;
                if (dataStream is null)
                {
                    if (entry.Length != 0)
                        throw InvalidArchiveEntryLength(directive);
                }
                else
                {
                    while (true)
                    {
                        int bytesRead = await ReadArchiveDataAsync(
                                dataStream,
                                buffer.AsMemory(),
                                directive,
                                ct)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        if (bytesRead > limits.MaxEntryBytes - entryBytes)
                            throw EntrySizeLimitExceeded(directive, limits.MaxEntryBytes);

                        if (bytesRead > limits.MaxExpandedBytes - expandedBytes)
                        {
                            throw ExpandedSizeLimitExceeded(
                                directive,
                                limits.MaxExpandedBytes);
                        }

                        // The destination retains its own copy of the block until the
                        // write succeeds; counters advance once, after it.
                        await destination.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                ct)
                            .ConfigureAwait(false);
                        entryBytes += bytesRead;
                        expandedBytes += bytesRead;
                    }
                }
            }

            largestEntryBytes = Math.Max(largestEntryBytes, entryBytes);
        }

        return new ArchiveExtractionMetrics(
            expandedBytes,
            largestEntryBytes,
            entryCount,
            maximumPathLength,
            maximumDepth)
        {
            MetadataBytes = preflightStream.MetadataBytes,
            MetadataEntryCount = preflightStream.MetadataEntryCount,
            Inventory = inventory
        };
    }

    private static async ValueTask<TarEntry?> ReadNextEntryAsync(
        TarReader tarReader,
        string? directive,
        CancellationToken cancellationToken)
    {
        try
        {
            return await tarReader.GetNextEntryAsync(
                    copyData: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (InvalidDataException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (FormatException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (OverflowException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
    }

    private static async ValueTask<int> ReadArchiveDataAsync(
        Stream dataStream,
        Memory<byte> buffer,
        string? directive,
        CancellationToken cancellationToken)
    {
        try
        {
            return await dataStream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (InvalidDataException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (FormatException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (OverflowException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidArchiveParserFailure(directive, exception);
        }
    }

    /// <summary>
    /// Validates an extracted standard or legacy package layout and normalizes
    /// accepted root content into the standard <c>package/</c> directory.
    /// </summary>
    /// <remarks>
    /// Standard layouts require one <c>package/package.json</c> manifest and may
    /// include only the optional lowercase <c>openapi/</c> and <c>other/</c> sibling
    /// directories. All ancillary destinations beneath <c>package/</c> are checked
    /// before any moves; occupied destinations are rejected without merging.
    /// Unambiguous legacy root layouts are also normalized beneath <c>package/</c>.
    /// This is an in-place, layout-only operation: it does not validate package
    /// identity or provide transactional rollback if a filesystem operation fails.
    /// </remarks>
    /// <param name="extractedPath">The root directory of the extracted package.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="extractedPath"/> is <c>null</c>.</exception>
    public static void NormalizePackageStructure(string extractedPath)
    {
        ArgumentNullException.ThrowIfNull(extractedPath);
        FileSystemContext context = FileSystemContext.CreateDefault();
        PackageArchiveInventory inventory =
            PackageArchiveInventory.FromFileSystem(
                extractedPath,
                directive: null,
                context);
        _ = PackageStructureNormalizer.ValidateAndNormalize(
            extractedPath,
            inventory,
            directive: null,
            context);
    }

    /// <summary>
    /// Retains the compatibility transformation and original-layout result used
    /// by publish identity discovery and standard-only NPM publication validation.
    /// Cache installation and import use the read-only content locator instead.
    /// </summary>
    internal static PackageArchiveLayoutResult ValidateAndNormalizePackageStructure(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive) =>
        PackageStructureNormalizer.ValidateAndNormalize(
            extractedPath,
            inventory,
            directive);

    internal static PackageArchiveLayoutResult ValidateAndNormalizePackageStructure(
        string extractedPath,
        PackageArchiveInventory inventory,
        string? directive,
        FileSystemContext context) =>
        PackageStructureNormalizer.ValidateAndNormalize(
            extractedPath,
            inventory,
            directive,
            context);

    private static bool IsRegularFile(TarEntryType entryType) =>
        entryType is TarEntryType.RegularFile
            or TarEntryType.V7RegularFile
            or TarEntryType.ContiguousFile;

    private static PackageInstallException UnsafeArchivePath(string? directive) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            "Package archive contains an unsafe entry path.",
            directive);

    private static PackageInstallException UnsupportedArchiveEntry(
        TarEntryType entryType,
        string? directive) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            $"Package archive contains unsupported entry type '{entryType}'.",
            directive);

    private static PackageInstallException InvalidArchiveEntryLength(
        string? directive) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            "Package archive contains an invalid entry length.",
            directive);

    private static PackageInstallException InvalidArchiveParserFailure(
        string? directive,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            "Package archive is malformed or uses an unsupported tar format.",
            directive,
            exception);

    private static PackageInstallException ExpandedSizeLimitExceeded(
        string? directive,
        long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ExpandedSizeLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive exceeds the expanded size limit of {limit} bytes.",
            directive);

    private static PackageInstallException EntrySizeLimitExceeded(
        string? directive,
        long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.EntrySizeLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive entry exceeds the size limit of {limit} bytes.",
            directive);

    private static PackageInstallException ArchiveEntryCountLimitExceeded(
        string? directive,
        int limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive exceeds the entry-count limit of {limit}.",
            directive);

    private static PackageInstallException ArchivePathLengthLimitExceeded(
        string? directive,
        int limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive path exceeds the length limit of {limit} in canonical or exact spelling.",
            directive);

    private static PackageInstallException ArchiveDepthLimitExceeded(
        string? directive,
        int limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchiveDepthLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive path exceeds the nesting-depth limit of {limit}.",
            directive);
}

/// <summary>Resource metrics measured while extracting one archive.</summary>
internal sealed record ArchiveExtractionMetrics(
    long ExpandedBytes,
    long LargestEntryBytes,
    int EntryCount,
    int MaximumPathLength,
    int MaximumDepth)
{
    internal long MetadataBytes { get; init; }

    internal int MetadataEntryCount { get; init; }

    internal PackageArchiveInventory? Inventory { get; init; }
}
