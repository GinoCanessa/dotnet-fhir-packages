// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Indexing;

internal static class PackageIndexValidation
{
    internal static bool TryValidateStructure(
        PackageIndex? index,
        out string? failureReason)
    {
        if (index is null)
        {
            failureReason = "The package index JSON did not contain an object.";
            return false;
        }

        if (index.IndexVersion != 2)
        {
            failureReason = "The package index version must be 2.";
            return false;
        }

        if (index.Files is null)
        {
            failureReason = "The package index files collection must not be null.";
            return false;
        }

        HashSet<string> filenames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResourceIndexEntry entry in index.Files)
        {
            if (entry is null)
            {
                failureReason = "The package index must not contain null file entries.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.ResourceType))
            {
                failureReason = "Package index resource types must not be blank.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.Filename))
            {
                failureReason = "Package index filenames must not be blank.";
                return false;
            }

            PortableArchivePath portablePath;
            try
            {
                portablePath = PortableArchivePath.Create(
                    entry.Filename,
                    isDirectory: false);
            }
            catch (PackageInstallException exception)
            {
                failureReason = exception.Message;
                return false;
            }

            if (!filenames.Add(portablePath.CanonicalPath))
            {
                failureReason =
                    $"The package index contains duplicate filename '{entry.Filename}'.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    internal static bool TryValidateReferencedFiles(
        PackageIndex? index,
        string packageContentPath,
        out string? failureReason) =>
        TryValidateReferencedFiles(
            index,
            packageContentPath,
            FileSystemContext.CreateDefault(),
            out failureReason);

    internal static bool TryValidateReferencedFiles(
        PackageIndex? index,
        string packageContentPath,
        FileSystemContext context,
        out string? failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageContentPath);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryValidateStructure(index, out failureReason))
            return false;

        PackageIndex validatedIndex = index!;
        string fullContentPath = context.FileSystem.GetFullPath(packageContentPath);
        foreach (ResourceIndexEntry entry in validatedIndex.Files)
        {
            if (!TryGetReferencedPath(entry, fullContentPath, out string filePath, out failureReason))
                return false;

            try
            {
                // A genuinely missing file is explicit and must not consume retry budget.
                if (context.FileSystem.Probe(filePath).Kind == FileSystemEntryKind.Missing)
                {
                    failureReason =
                        $"Package index filename '{entry.Filename}' does not exist.";
                    return false;
                }

                using FileStream stream =
                    context.FileSystem.OpenRegularRead(filePath);
            }
            catch (FileNotFoundException)
            {
                failureReason =
                    $"Package index filename '{entry.Filename}' does not exist.";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                failureReason =
                    $"Package index filename '{entry.Filename}' does not exist.";
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                failureReason = FileSystemFailureContext.FormatMessage(exception);
                return false;
            }
            catch (IOException exception)
            {
                failureReason = FileSystemFailureContext.FormatMessage(exception);
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    internal static async ValueTask<(bool IsValid, string? FailureReason)> TryValidateReferencedFilesAsync(
        PackageIndex? index,
        string packageContentPath,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageContentPath);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateStructure(index, out string? failureReason))
            return (false, failureReason);

        string fullContentPath = await context.FileSystem.GetFullPathAsync(packageContentPath, cancellationToken)
            .ConfigureAwait(false);
        foreach (ResourceIndexEntry entry in index!.Files)
        {
            if (!TryGetReferencedPath(entry, fullContentPath, out string filePath, out failureReason))
                return (false, failureReason);

            try
            {
                if ((await context.FileSystem.ProbeAsync(filePath, cancellationToken).ConfigureAwait(false)).Kind
                    == FileSystemEntryKind.Missing)
                {
                    return (false, $"Package index filename '{entry.Filename}' does not exist.");
                }

                await using FileStream stream = await context.FileSystem.OpenRegularReadAsync(filePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return (false, $"Package index filename '{entry.Filename}' does not exist.");
            }
            catch (DirectoryNotFoundException)
            {
                return (false, $"Package index filename '{entry.Filename}' does not exist.");
            }
            catch (UnauthorizedAccessException exception)
            {
                return (false, FileSystemFailureContext.FormatMessage(exception));
            }
            catch (IOException exception)
            {
                return (false, FileSystemFailureContext.FormatMessage(exception));
            }
        }

        return (true, null);
    }

    private static bool TryGetReferencedPath(
        ResourceIndexEntry entry,
        string fullContentPath,
        out string filePath,
        out string? failureReason)
    {
        filePath = string.Empty;
        PortableArchivePath portablePath;
        try
        {
            portablePath = PortableArchivePath.Create(entry.Filename, isDirectory: false);
        }
        catch (PackageInstallException exception)
        {
            failureReason = exception.Message;
            return false;
        }

        string relativePath = portablePath.ExactSpelling.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            filePath = Path.GetFullPath(Path.Combine(fullContentPath, relativePath));
        }
        catch (ArgumentException exception)
        {
            failureReason = exception.Message;
            return false;
        }
        catch (NotSupportedException exception)
        {
            failureReason = exception.Message;
            return false;
        }

        string containment = Path.GetRelativePath(fullContentPath, filePath);
        if (Path.IsPathRooted(containment)
            || string.Equals(containment, "..", StringComparison.Ordinal)
            || containment.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || containment.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            failureReason =
                $"Package index filename '{entry.Filename}' is not contained by the package directory.";
            return false;
        }

        failureReason = null;
        return true;
    }
}
