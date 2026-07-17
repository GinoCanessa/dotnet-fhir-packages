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
    /// <param name="tarballStream">A readable stream containing the gzip-compressed tar archive.</param>
    /// <param name="destinationPath">The directory to extract files into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tarballStream"/> or <paramref name="destinationPath"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the archive contains invalid or unsafe entry paths.</exception>
    public static async Task ExtractAsync(Stream tarballStream, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        ArgumentNullException.ThrowIfNull(destinationPath);

        Directory.CreateDirectory(destinationPath);

        string fullDestination = Path.GetFullPath(destinationPath);

        await ExtractUsingTarReaderAsync(tarballStream, fullDestination, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the tarball using <see cref="TarReader"/> for async entry enumeration
    /// and path traversal validation.
    /// </summary>
    private static async Task ExtractUsingTarReaderAsync(
        Stream tarballStream,
        string destinationPath,
        CancellationToken ct)
    {
        // Reset stream to beginning if possible
        if (tarballStream.CanSeek)
            tarballStream.Position = 0;

        await using GZipStream gzipStream = new GZipStream(tarballStream, CompressionMode.Decompress, leaveOpen: true);
        await using TarReader tarReader = new TarReader(gzipStream, leaveOpen: true);

        while (await tarReader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            string entryName = NormalizeEntryName(
                entry.Name,
                entry.EntryType == TarEntryType.Directory);
            if (string.IsNullOrEmpty(entryName))
                continue;

            string targetPath = Path.GetFullPath(Path.Combine(destinationPath, entryName));
            string relativePath = Path.GetRelativePath(destinationPath, targetPath);

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

            // Ensure parent directory exists
            string? parentDir = Path.GetDirectoryName(targetPath);
            if (parentDir is not null)
                Directory.CreateDirectory(parentDir);

            await entry.ExtractToFileAsync(targetPath, overwrite: true, ct).ConfigureAwait(false);
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

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static PackageInstallException UnsafeArchivePath() =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            "Package archive contains an unsafe entry path.");
}
