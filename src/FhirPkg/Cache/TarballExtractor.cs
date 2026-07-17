// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using FhirPkg.Installation;

namespace FhirPkg.Cache;

/// <summary>
/// Extracts .tgz (gzip-compressed tar) package archives and normalizes
/// the resulting directory structure for FHIR package cache storage.
/// </summary>
/// <remarks>
/// FHIR packages are distributed as npm-style .tgz tarballs. The expected
/// structure within the archive is a <c>package/</c> subdirectory containing
/// all resource files and the package.json manifest. Some packages may not
/// follow this convention, so <see cref="NormalizePackageStructure"/> is
/// provided to ensure a consistent layout.
/// </remarks>
public static class TarballExtractor
{
    private const string PackageSubdirectory = "package";

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
                ct)
            .ConfigureAwait(false);
    }

    internal static async Task<ArchiveExtractionMetrics> ExtractAsync(
        Stream tarballStream,
        string destinationPath,
        PackageInstallLimits limits,
        string? directive,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        Directory.CreateDirectory(destinationPath);
        string fullDestination = Path.GetFullPath(destinationPath);

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

            string entryName = NormalizeEntryName(
                entry.Name,
                entry.EntryType == TarEntryType.Directory);
            if (string.IsNullOrEmpty(entryName))
                continue;

            if (entryName.Length > limits.MaxArchivePathLength)
            {
                throw ArchivePathLengthLimitExceeded(
                    directive,
                    limits.MaxArchivePathLength);
            }

            int depth = CountPathDepth(entryName);
            if (depth > limits.MaxArchiveDepth)
                throw ArchiveDepthLimitExceeded(directive, limits.MaxArchiveDepth);

            maximumPathLength = Math.Max(maximumPathLength, entryName.Length);
            maximumDepth = Math.Max(maximumDepth, depth);

            string localEntryName = entryName.Replace(
                '/',
                Path.DirectorySeparatorChar);
            string targetPath = Path.GetFullPath(
                Path.Combine(fullDestination, localEntryName));
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
                throw UnsafeArchivePath();
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (!IsRegularFile(entry.EntryType))
                throw UnsupportedArchiveEntry(entry.EntryType, directive);

            if (entry.Length < 0)
                throw InvalidArchiveEntryLength(directive);

            if (entry.Length > limits.MaxEntryBytes)
                throw EntrySizeLimitExceeded(directive, limits.MaxEntryBytes);

            if (entry.Length > limits.MaxExpandedBytes - expandedBytes)
                throw ExpandedSizeLimitExceeded(directive, limits.MaxExpandedBytes);

            string? parentDir = Path.GetDirectoryName(targetPath);
            if (parentDir is not null)
                Directory.CreateDirectory(parentDir);

            long entryBytes = 0;
            await using (FileStream destination = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
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

                        entryBytes += bytesRead;
                        expandedBytes += bytesRead;
                        await destination.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                ct)
                            .ConfigureAwait(false);
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
            MetadataEntryCount = preflightStream.MetadataEntryCount
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
    /// Normalizes the package structure by ensuring a <c>package/</c> subdirectory exists.
    /// If the extracted contents do not contain a <c>package/</c> subdirectory,
    /// all top-level files are moved into a newly created one.
    /// </summary>
    /// <param name="extractedPath">The root directory of the extracted package.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="extractedPath"/> is <c>null</c>.</exception>
    public static void NormalizePackageStructure(string extractedPath)
    {
        ArgumentNullException.ThrowIfNull(extractedPath);

        string packageDir = Path.Combine(extractedPath, PackageSubdirectory);

        // If the package/ subdirectory already exists, nothing to do
        if (Directory.Exists(packageDir))
            return;

        // Create the package/ subdirectory
        Directory.CreateDirectory(packageDir);

        // Move all top-level files into package/
        foreach (string file in Directory.GetFiles(extractedPath))
        {
            string fileName = Path.GetFileName(file);
            string destination = Path.Combine(packageDir, fileName);
            File.Move(file, destination);
        }

        // Move all top-level subdirectories (except "package" itself) into package/
        foreach (string dir in Directory.GetDirectories(extractedPath))
        {
            string dirName = Path.GetFileName(dir);
            if (string.Equals(dirName, PackageSubdirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(packageDir, dirName);
            Directory.Move(dir, destination);
        }
    }

    /// <summary>
    /// Normalizes a portable tar entry path while rejecting roots and traversal.
    /// </summary>
    private static string NormalizeEntryName(string entryName, bool isDirectory)
    {
        string normalized = entryName.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (normalized.StartsWith('/')
            || (normalized.Length >= 2
                && char.IsAsciiLetter(normalized[0])
                && normalized[1] == ':'))
        {
            throw UnsafeArchivePath();
        }

        if (isDirectory)
            normalized = normalized.TrimEnd('/');

        if (normalized.Length == 0)
            return string.Empty;

        string[] segments = normalized.Split('/', StringSplitOptions.None);
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw UnsafeArchivePath();
        }

        return string.Join('/', segments);
    }

    private static int CountPathDepth(string normalizedEntryName)
    {
        int depth = 1;
        foreach (char character in normalizedEntryName)
        {
            if (character == '/')
                depth++;
        }

        return depth;
    }

    private static bool IsRegularFile(TarEntryType entryType) =>
        entryType is TarEntryType.RegularFile
            or TarEntryType.V7RegularFile
            or TarEntryType.ContiguousFile;

    private static PackageInstallException UnsafeArchivePath() =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            "Package archive contains an unsafe entry path.");

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
            $"Package archive path exceeds the normalized length limit of {limit}.",
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
}
