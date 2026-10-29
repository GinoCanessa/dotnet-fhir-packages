// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal sealed class PackageCacheValidator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheRoot;
    private readonly FileSystemContext _context;

    // Compatibility entry point for standalone callers; cache graphs pass their own context.
    internal PackageCacheValidator(string cacheRoot)
        : this(cacheRoot, FileSystemContext.CreateDefault())
    {
    }

    internal PackageCacheValidator(string cacheRoot, FileSystemContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _cacheRoot = context.FileSystem.GetFullPath(cacheRoot);
    }

    internal PackageCacheInspection Inspect(PackageReference reference)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        return Inspect(cacheKey);
    }

    internal PackageCacheInspection Inspect(PackageCacheKey cacheKey)
    {
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot, _context);
        PackageCacheInspection? ancestorFailure =
            InspectTargetAncestors(cacheKey, targetPath);
        if (ancestorFailure is not null)
            return ancestorFailure;

        PackageCacheInspection? shapeFailure = InspectShape(
            cacheKey,
            targetPath,
            out string contentPath,
            out string manifestPath);
        if (shapeFailure is not null)
            return shapeFailure;

        try
        {
            using Stream stream =
                _context.FileSystem.OpenProtectedRegularRead(manifestPath);
            PackageManifest? manifest = JsonSerializer.Deserialize<PackageManifest>(
                stream,
                s_jsonOptions);
            return Complete(cacheKey, targetPath, contentPath, manifestPath, manifest);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
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
        cancellationToken.ThrowIfCancellationRequested();
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot, _context);
        PackageCacheInspection? ancestorFailure =
            await InspectTargetAncestorsAsync(cacheKey, targetPath, cancellationToken)
                .ConfigureAwait(false);
        if (ancestorFailure is not null)
            return ancestorFailure;

        (PackageCacheInspection? shapeFailure, string contentPath, string manifestPath) =
            await InspectShapeAsync(cacheKey, targetPath, cancellationToken)
                .ConfigureAwait(false);
        if (shapeFailure is not null)
            return shapeFailure;

        try
        {
            await using Stream stream =
                await _context.FileSystem.OpenProtectedRegularReadAsync(
                        manifestPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            PackageManifest? manifest =
                await JsonSerializer.DeserializeAsync<PackageManifest>(
                        stream,
                        s_jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            return Complete(cacheKey, targetPath, contentPath, manifestPath, manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
        }
    }

    // Exhausted physical read faults stay Corrupt; cancellation and platform errors escape.
    private static bool IsInspectionFailure(Exception exception) =>
        exception is PackageInstallException
            or JsonException
            or UnauthorizedAccessException
            or IOException
        || (exception is NotSupportedException and not PlatformNotSupportedException);

    private static PackageCacheInspection Complete(
        PackageCacheKey cacheKey,
        string targetPath,
        string contentPath,
        string manifestPath,
        PackageManifest? manifest)
    {
        if (manifest is null)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                contentPath,
                manifestPath,
                "Package manifest JSON did not contain an object.");
        }

        try
        {
            PackageIdentityExpectation expectation =
                PackageIdentityValidator.CreateExpectation(
                    cacheKey.DisplayReference);
            _ = PackageIdentityValidator.ValidateExpected(
                manifest,
                expectation,
                cacheKey.DisplayReference.ToString());
        }
        catch (PackageInstallException exception)
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
        }

        return Valid(cacheKey, targetPath, contentPath, manifestPath, manifest);
    }

    private PackageCacheInspection? InspectShape(
        PackageCacheKey cacheKey,
        string targetPath,
        out string contentPath,
        out string manifestPath,
        CancellationToken cancellationToken = default)
    {
        contentPath = Path.Combine(targetPath, "package");
        manifestPath = Path.Combine(contentPath, "package.json");
        FileSystemProbeResult target;
        try
        {
            target = _context.FileSystem.Probe(targetPath, cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return CorruptFrom(exception, cacheKey, targetPath, null, null);
        }

        // A genuinely absent target is an explicit Missing result, never a retried fault.
        if (target.Kind == FileSystemEntryKind.Missing)
            return Missing(cacheKey, targetPath);

        if (target.Kind != FileSystemEntryKind.Directory)
        {
            return Corrupt(
                cacheKey,
                targetPath,
                null,
                null,
                "The cache target is not a regular directory.");
        }

        try
        {
            // Without an exact root candidate, retain standard-only cache
            // compatibility, including the host filesystem's case behavior.
            // With one, incidental package entries must not redirect root content.
            if (HasExactManifestCandidate(targetPath, cancellationToken)
                && !HasExactStandardManifestCandidate(targetPath, cancellationToken))
            {
                contentPath = targetPath;
                manifestPath = Path.Combine(targetPath, "package.json");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
        }

        PackageCacheInspection? contentFailure = InspectRequiredPath(
            cacheKey,
            targetPath,
            contentPath,
            expectDirectory: true,
            cancellationToken);
        if (contentFailure is not null)
            return contentFailure;

        return InspectRequiredPath(
            cacheKey,
            targetPath,
            manifestPath,
            expectDirectory: false,
            cancellationToken);
    }

    private bool HasExactStandardManifestCandidate(
        string targetPath,
        CancellationToken cancellationToken) =>
        TryGetExactChildKind(targetPath, "package", cancellationToken, out FileSystemEntryKind kind)
        && kind == FileSystemEntryKind.Directory
        && HasExactManifestCandidate(Path.Combine(targetPath, "package"), cancellationToken);

    private async ValueTask<(PackageCacheInspection? Failure, string ContentPath, string ManifestPath)>
        InspectShapeAsync(
            PackageCacheKey cacheKey,
            string targetPath,
            CancellationToken cancellationToken)
    {
        string contentPath = Path.Combine(targetPath, "package");
        string manifestPath = Path.Combine(contentPath, "package.json");
        FileSystemProbeResult target;
        try
        {
            target = await _context.FileSystem.ProbeAsync(targetPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return (CorruptFrom(exception, cacheKey, targetPath, null, null), contentPath, manifestPath);
        }

        if (target.Kind == FileSystemEntryKind.Missing)
            return (Missing(cacheKey, targetPath), contentPath, manifestPath);

        if (target.Kind != FileSystemEntryKind.Directory)
        {
            return (Corrupt(
                cacheKey, targetPath, null, null,
                "The cache target is not a regular directory."), contentPath, manifestPath);
        }

        try
        {
            if (await HasExactManifestCandidateAsync(targetPath, cancellationToken).ConfigureAwait(false)
                && !await HasExactStandardManifestCandidateAsync(targetPath, cancellationToken).ConfigureAwait(false))
            {
                contentPath = targetPath;
                manifestPath = Path.Combine(targetPath, "package.json");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return (CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath), contentPath, manifestPath);
        }

        PackageCacheInspection? failure = await InspectRequiredPathAsync(
                cacheKey, targetPath, contentPath, expectDirectory: true, cancellationToken)
            .ConfigureAwait(false)
            ?? await InspectRequiredPathAsync(
                    cacheKey, targetPath, manifestPath, expectDirectory: false, cancellationToken)
                .ConfigureAwait(false);
        return (failure, contentPath, manifestPath);
    }

    private async ValueTask<bool> HasExactStandardManifestCandidateAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        await GetExactChildKindAsync(targetPath, "package", cancellationToken).ConfigureAwait(false)
            == FileSystemEntryKind.Directory
        && await HasExactManifestCandidateAsync(Path.Combine(targetPath, "package"), cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<bool> HasExactManifestCandidateAsync(
        string contentPath,
        CancellationToken cancellationToken) =>
        await GetExactChildKindAsync(contentPath, "package.json", cancellationToken).ConfigureAwait(false)
            == FileSystemEntryKind.File;

    private async ValueTask<FileSystemEntryKind> GetExactChildKindAsync(
        string parentPath,
        string name,
        CancellationToken cancellationToken)
    {
        string[] paths = await _context.FileSystem.SnapshotDirectoryAsync(parentPath, cancellationToken)
            .ConfigureAwait(false);
        foreach (string path in paths)
        {
            if (string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
            {
                return (await _context.FileSystem.ProbeAsync(path, cancellationToken)
                    .ConfigureAwait(false)).Kind;
            }
        }

        return FileSystemEntryKind.Missing;
    }

    private bool HasExactManifestCandidate(
        string contentPath,
        CancellationToken cancellationToken) =>
        TryGetExactChildKind(contentPath, "package.json", cancellationToken, out FileSystemEntryKind kind)
        && kind == FileSystemEntryKind.File;

    private bool TryGetExactChildKind(
        string parentPath,
        string name,
        CancellationToken cancellationToken,
        out FileSystemEntryKind kind)
    {
        foreach (string path in _context.FileSystem.SnapshotDirectory(parentPath, cancellationToken))
        {
            if (string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
            {
                kind = _context.FileSystem.Probe(path, cancellationToken).Kind;
                return kind != FileSystemEntryKind.Missing;
            }
        }

        kind = FileSystemEntryKind.Missing;
        return false;
    }

    private PackageCacheInspection? InspectTargetAncestors(
        PackageCacheKey cacheKey,
        string targetPath,
        CancellationToken cancellationToken = default)
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
                FileSystemProbeResult probe =
                    _context.FileSystem.Probe(currentPath, cancellationToken);
                if (probe.Kind == FileSystemEntryKind.Missing)
                    return null;

                if (probe.Kind != FileSystemEntryKind.Directory)
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
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return CorruptFrom(
                    exception,
                    cacheKey,
                    targetPath,
                    null,
                    null,
                    isRepairable: false);
            }
        }

        return null;
    }

    private PackageCacheInspection? InspectRequiredPath(
        PackageCacheKey cacheKey,
        string targetPath,
        string path,
        bool expectDirectory,
        CancellationToken cancellationToken)
    {
        string? contentPath = expectDirectory ? path : Path.GetDirectoryName(path);
        string? manifestPath = expectDirectory ? null : path;
        try
        {
            FileSystemProbeResult probe = _context.FileSystem.Probe(path, cancellationToken);
            if (probe.Kind == FileSystemEntryKind.Missing)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    contentPath,
                    manifestPath,
                    "A required cache path is missing.");
            }

            FileSystemEntryKind expected = expectDirectory
                ? FileSystemEntryKind.Directory
                : FileSystemEntryKind.File;
            if (probe.Kind != expected)
            {
                return Corrupt(
                    cacheKey,
                    targetPath,
                    contentPath,
                    manifestPath,
                    expectDirectory
                        ? "The package content path is not a regular directory."
                        : "The package manifest is not a regular file.");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
        }

        return null;
    }

    private async ValueTask<PackageCacheInspection?> InspectTargetAncestorsAsync(
        PackageCacheKey cacheKey,
        string targetPath,
        CancellationToken cancellationToken)
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
                FileSystemProbeResult probe = await _context.FileSystem.ProbeAsync(currentPath, cancellationToken)
                    .ConfigureAwait(false);
                if (probe.Kind == FileSystemEntryKind.Missing)
                    return null;

                if (probe.Kind != FileSystemEntryKind.Directory)
                {
                    return Corrupt(
                        cacheKey, targetPath, null, null,
                        "A cache target ancestor is not a regular directory.",
                        isRepairable: false);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return CorruptFrom(
                    exception, cacheKey, targetPath, null, null, isRepairable: false);
            }
        }

        return null;
    }

    private async ValueTask<PackageCacheInspection?> InspectRequiredPathAsync(
        PackageCacheKey cacheKey,
        string targetPath,
        string path,
        bool expectDirectory,
        CancellationToken cancellationToken)
    {
        string? contentPath = expectDirectory ? path : Path.GetDirectoryName(path);
        string? manifestPath = expectDirectory ? null : path;
        try
        {
            FileSystemProbeResult probe = await _context.FileSystem.ProbeAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Kind == FileSystemEntryKind.Missing)
            {
                return Corrupt(
                    cacheKey, targetPath, contentPath, manifestPath,
                    "A required cache path is missing.");
            }

            FileSystemEntryKind expected = expectDirectory
                ? FileSystemEntryKind.Directory
                : FileSystemEntryKind.File;
            if (probe.Kind != expected)
            {
                return Corrupt(
                    cacheKey, targetPath, contentPath, manifestPath,
                    expectDirectory
                        ? "The package content path is not a regular directory."
                        : "The package manifest is not a regular file.");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return CorruptFrom(exception, cacheKey, targetPath, contentPath, manifestPath);
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

    // Embeds the final retry evidence so an exhausted physical failure is diagnosable.
    private static PackageCacheInspection CorruptFrom(
        Exception exception,
        PackageCacheKey cacheKey,
        string targetPath,
        string? contentPath,
        string? manifestPath,
        bool isRepairable = true)
    {
        PackageCacheInspection inspection = Corrupt(
            cacheKey,
            targetPath,
            contentPath,
            manifestPath,
            FileSystemFailureContext.FormatMessage(exception),
            isRepairable);
        return FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? context)
            ? inspection with { FailureContext = context }
            : inspection;
    }
}
