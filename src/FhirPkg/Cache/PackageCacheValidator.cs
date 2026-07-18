// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal sealed class PackageCacheValidator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheRoot;

    internal PackageCacheValidator(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    internal PackageCacheInspection Inspect(PackageReference reference)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        return Inspect(cacheKey);
    }

    internal PackageCacheInspection Inspect(PackageCacheKey cacheKey)
    {
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        PackageCacheInspection? ancestorFailure =
            InspectTargetAncestors(cacheKey, targetPath);
        if (ancestorFailure is not null)
            return ancestorFailure;

        PackageCacheInspection? shapeFailure = InspectShape(cacheKey, targetPath);
        if (shapeFailure is not null)
            return shapeFailure;

        string contentPath = Path.Combine(targetPath, "package");
        string manifestPath = Path.Combine(contentPath, "package.json");
        try
        {
            using FileStream stream =
                PackageCacheRegularFile.OpenRead(manifestPath);
            PackageManifest? manifest = JsonSerializer.Deserialize<PackageManifest>(
                stream,
                s_jsonOptions);
            if (manifest is null)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    contentPath,
                    manifestPath,
                    "Package manifest JSON did not contain an object.");
            }

            PackageIdentityExpectation expectation =
                PackageIdentityValidator.CreateExpectation(
                    cacheKey.DisplayReference);
            _ = PackageIdentityValidator.ValidateExpected(
                manifest,
                expectation,
                cacheKey.DisplayReference.ToString());
            return Valid(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                manifest);
        }
        catch (PackageInstallException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (JsonException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (PlatformNotSupportedException)
        {
            throw;
        }
        catch (NotSupportedException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (IOException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
    }

    internal async Task<PackageCacheInspection> InspectAsync(
        PackageReference reference,
        CancellationToken cancellationToken)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        return await InspectAsync(cacheKey, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<PackageCacheInspection> InspectAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        PackageCacheInspection? ancestorFailure =
            InspectTargetAncestors(cacheKey, targetPath);
        if (ancestorFailure is not null)
            return ancestorFailure;

        PackageCacheInspection? shapeFailure = InspectShape(cacheKey, targetPath);
        if (shapeFailure is not null)
            return shapeFailure;

        string contentPath = Path.Combine(targetPath, "package");
        string manifestPath = Path.Combine(contentPath, "package.json");
        try
        {
            await using FileStream stream =
                PackageCacheRegularFile.OpenRead(manifestPath);
            PackageManifest? manifest =
                await JsonSerializer.DeserializeAsync<PackageManifest>(
                        stream,
                        s_jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (manifest is null)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    contentPath,
                    manifestPath,
                    "Package manifest JSON did not contain an object.");
            }

            PackageIdentityExpectation expectation =
                PackageIdentityValidator.CreateExpectation(
                    cacheKey.DisplayReference);
            _ = PackageIdentityValidator.ValidateExpected(
                manifest,
                expectation,
                cacheKey.DisplayReference.ToString());
            return Valid(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageInstallException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (JsonException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (PlatformNotSupportedException)
        {
            throw;
        }
        catch (NotSupportedException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
        catch (IOException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                exception.Message);
        }
    }

    private static PackageCacheInspection? InspectShape(
        PackageCacheKey cacheKey,
        string targetPath)
    {
        FileAttributes targetAttributes;
        try
        {
            targetAttributes = File.GetAttributes(targetPath);
        }

        catch (FileNotFoundException)
        {
            return Missing(cacheKey, targetPath);
        }
        catch (DirectoryNotFoundException)
        {
            return Missing(cacheKey, targetPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                null,
                null,
                exception.Message);
        }
        catch (IOException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                null,
                null,
                exception.Message);
        }

        if (!targetAttributes.HasFlag(FileAttributes.Directory)
            || targetAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Corrupt(
                cacheKey,
                targetPath,
                null,
                null,
                "The cache target is not a regular directory.");
        }

        string contentPath = Path.Combine(targetPath, "package");
        PackageCacheInspection? contentFailure = InspectRequiredPath(
            cacheKey,
            targetPath,
            contentPath,
            expectDirectory: true);
        if (contentFailure is not null)
            return contentFailure;

        string manifestPath = Path.Combine(contentPath, "package.json");
        return InspectRequiredPath(
            cacheKey,
            targetPath,
            manifestPath,
            expectDirectory: false);
    }

    private PackageCacheInspection? InspectTargetAncestors(
        PackageCacheKey cacheKey,
        string targetPath)
    {
        string relativePath = Path.GetRelativePath(_cacheRoot, targetPath);
        string? relativeParent = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(relativeParent))
            return null;

        string currentPath = _cacheRoot;
        foreach (string segment in relativeParent.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                FileAttributes attributes = File.GetAttributes(currentPath);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return Corrupt(
                        cacheKey,
                        targetPath,
                        null,
                        null,
                        "A cache target ancestor is not a regular directory.",
                        isRepairable: false);
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    null,
                    null,
                    exception.Message,
                    isRepairable: false);
            }
            catch (IOException exception)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    null,
                    null,
                    exception.Message,
                    isRepairable: false);
            }
        }

        return null;
    }

    private static PackageCacheInspection? InspectRequiredPath(
        PackageCacheKey cacheKey,
        string targetPath,
        string path,
        bool expectDirectory)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (isDirectory != expectDirectory
                || attributes.HasFlag(FileAttributes.ReparsePoint)
                || (!expectDirectory && attributes.HasFlag(FileAttributes.Device)))
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    expectDirectory ? path : Path.GetDirectoryName(path),
                    expectDirectory ? null : path,
                    expectDirectory
                        ? "The package content path is not a regular directory."
                        : "The package manifest is not a regular file.");
            }
        }
        catch (FileNotFoundException)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                expectDirectory ? path : Path.GetDirectoryName(path),
                expectDirectory ? null : path,
                "A required cache path is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                expectDirectory ? path : Path.GetDirectoryName(path),
                expectDirectory ? null : path,
                "A required cache path is missing.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                expectDirectory ? path : Path.GetDirectoryName(path),
                expectDirectory ? null : path,
                exception.Message);
        }
        catch (IOException exception)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                expectDirectory ? path : Path.GetDirectoryName(path),
                expectDirectory ? null : path,
                exception.Message);
        }

        return null;
    }

    private static PackageCacheInspection Missing(
        PackageCacheKey cacheKey,
        string targetPath) =>
        new()
        {
            State = PackageCacheInspectionState.Missing,
            CacheKey = cacheKey,
            TargetPath = targetPath
        };

    private static PackageCacheInspection Valid(
        PackageCacheKey cacheKey,
        string targetPath,
        string contentPath,
        string manifestPath,
        PackageManifest manifest) =>
        new()
        {
            State = PackageCacheInspectionState.Valid,
            CacheKey = cacheKey,
            TargetPath = targetPath,
            ContentPath = contentPath,
            ManifestPath = manifestPath,
            Manifest = manifest
        };

    private static PackageCacheInspection Corrupt(
        PackageCacheKey cacheKey,
        string targetPath,
        string? contentPath,
        string? manifestPath,
        string reason,
        bool isRepairable = true) =>
        new()
        {
            State = PackageCacheInspectionState.Corrupt,
            CacheKey = cacheKey,
            TargetPath = targetPath,
            ContentPath = contentPath,
            ManifestPath = manifestPath,
            CorruptionReason = reason,
            IsRepairable = isRepairable
        };
}
