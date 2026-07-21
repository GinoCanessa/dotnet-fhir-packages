// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;

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
        out string? failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageContentPath);

        if (!TryValidateStructure(index, out failureReason))
            return false;

        PackageIndex validatedIndex = index!;
        string fullContentPath = Path.GetFullPath(packageContentPath);
        foreach (ResourceIndexEntry entry in validatedIndex.Files)
        {
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

            string relativePath = portablePath.ExactSpelling.Replace(
                '/',
                Path.DirectorySeparatorChar);
            string filePath;
            try
            {
                filePath = Path.GetFullPath(
                    Path.Combine(fullContentPath, relativePath));
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

            string containment = Path.GetRelativePath(
                fullContentPath,
                filePath);
            if (Path.IsPathRooted(containment)
                || string.Equals(containment, "..", StringComparison.Ordinal)
                || containment.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || containment.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                failureReason =
                    $"Package index filename '{entry.Filename}' is not contained by the package directory.";
                return false;
            }

            try
            {
                using FileStream stream =
                    PackageCacheRegularFile.OpenRead(filePath);
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
                failureReason = exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                failureReason = exception.Message;
                return false;
            }
        }

        failureReason = null;
        return true;
    }
}
