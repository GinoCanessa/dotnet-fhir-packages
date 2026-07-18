// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Models;

namespace FhirPkg.Installation;

internal sealed record PackageIdentityValidationResult(
    PackageManifest Manifest,
    PackageReference ManifestReference,
    PackageCacheKey CacheKey);

internal static class PackageIdentityValidator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static PackageIdentityExpectation CreateExpectation(
        PackageReference reference,
        string? directive = null)
    {
        VersionType versionType = PackageDirective.ClassifyVersion(
            reference.Version);
        PackageIdentityExpectationKind kind = versionType switch
        {
            VersionType.Exact => PackageIdentityExpectationKind.Exact,
            VersionType.CiBuild
                or VersionType.CiBuildBranch
                or VersionType.LocalBuild =>
                PackageIdentityExpectationKind.Alias,
            _ => throw InvalidIdentity(
                directive,
                "Direct-source package identity must be exact, current, current$branch, or dev.")
        };

        if (versionType == VersionType.Exact
            && !IsConcreteExactVersion(reference.Version!))
        {
            throw InvalidIdentity(
                directive,
                "Direct-source package identity must not use a version range.");
        }

        _ = PackageCacheKey.Create(reference);
        return new PackageIdentityExpectation
        {
            Kind = kind,
            Reference = reference
        };
    }

    internal static PackageIdentityExpectation ValidateExpectation(
        PackageIdentityExpectation expectation,
        string? directive = null)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        PackageIdentityExpectation normalizedExpectation =
            CreateExpectation(expectation.Reference, directive);
        if (normalizedExpectation.Kind != expectation.Kind)
        {
            throw InvalidIdentity(
                directive,
                "Package identity expectation kind does not match its reference.");
        }

        return normalizedExpectation;
    }

    internal static async Task<PackageIdentityValidationResult> ValidateExpectedAsync(
        string manifestPath,
        PackageIdentityExpectation expectation,
        string? directive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(expectation);

        PackageManifest manifest = await ReadManifestAsync(
                manifestPath,
                directive,
                cancellationToken)
            .ConfigureAwait(false);
        return ValidateExpected(manifest, expectation, directive);
    }

    internal static PackageIdentityValidationResult ValidateExpected(
        PackageManifest manifest,
        PackageIdentityExpectation expectation,
        string? directive)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expectation);

        PackageIdentityExpectation normalizedExpectation =
            ValidateExpectation(expectation, directive);
        PackageReference manifestReference = ValidateManifestIdentity(
            manifest,
            directive);
        PackageCacheKey manifestKey = PackageCacheKey.Create(manifestReference);
        PackageCacheKey expectedKey = PackageCacheKey.Create(
            normalizedExpectation.Reference);

        if (!string.Equals(
                manifestKey.CanonicalReference.Name,
                expectedKey.CanonicalReference.Name,
                StringComparison.Ordinal))
        {
            throw InvalidIdentity(
                directive,
                $"Manifest package name '{manifestReference.Name}' does not match " +
                $"expected name '{expectation.Reference.Name}'.");
        }

        if (normalizedExpectation.Kind == PackageIdentityExpectationKind.Exact
            && !string.Equals(
                manifestReference.Version,
                expectation.Reference.Version,
                StringComparison.Ordinal))
        {
            throw InvalidIdentity(
                directive,
                $"Manifest package version '{manifestReference.Version}' does not match " +
                $"expected version '{expectation.Reference.Version}'.");
        }

        return new PackageIdentityValidationResult(
            manifest,
            manifestReference,
            expectedKey);
    }

    internal static PackageIdentityValidationResult Discover(
        PackageManifest manifest,
        string? directive)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        PackageReference manifestReference = ValidateManifestIdentity(
            manifest,
            directive);
        PackageCacheKey cacheKey = PackageCacheKey.Create(manifestReference);
        return new PackageIdentityValidationResult(
            manifest,
            manifestReference,
            cacheKey);
    }

    internal static async Task<PackageIdentityValidationResult> DiscoverAsync(
        string manifestPath,
        string? directive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        PackageManifest manifest = await ReadManifestAsync(
                manifestPath,
                directive,
                cancellationToken)
            .ConfigureAwait(false);
        return Discover(manifest, directive);
    }

    private static async Task<PackageManifest> ReadManifestAsync(
        string manifestPath,
        string? directive,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            PackageManifest? manifest =
                await JsonSerializer.DeserializeAsync<PackageManifest>(
                        stream,
                        s_jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            return manifest
                ?? throw InvalidManifest(
                    directive,
                    "Package manifest JSON did not contain an object.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidManifest(
                directive,
                "Package manifest is not readable JSON.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidManifest(
                directive,
                "Package manifest JSON uses an unsupported shape.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw InvalidManifest(
                directive,
                "Package manifest could not be read.",
                exception);
        }
        catch (IOException exception)
        {
            throw InvalidManifest(
                directive,
                "Package manifest could not be read.",
                exception);
        }
    }

    private static PackageReference ValidateManifestIdentity(
        PackageManifest manifest,
        string? directive)
    {
        string? manifestName = manifest.Name;
        string? manifestVersion = manifest.Version;
        if (string.IsNullOrWhiteSpace(manifestName)
            || string.IsNullOrWhiteSpace(manifestVersion))
        {
            throw InvalidIdentity(
                directive,
                "Package manifest name and version must not be empty.");
        }

        string trimmedName = manifestName.Trim();
        string trimmedVersion = manifestVersion.Trim();
        if (!string.Equals(manifestName, trimmedName, StringComparison.Ordinal)
            || !string.Equals(
                manifestVersion,
                trimmedVersion,
                StringComparison.Ordinal))
        {
            throw InvalidIdentity(
                directive,
                "Package manifest name and version must not contain surrounding whitespace.");
        }

        if (!IsConcreteExactVersion(trimmedVersion))
        {
            throw InvalidIdentity(
                directive,
                "Package manifest version must be a concrete exact value.");
        }

        string? scope = null;
        if (trimmedName.StartsWith('@'))
        {
            int separatorIndex = trimmedName.IndexOf('/');
            if (separatorIndex > 0)
                scope = trimmedName[..separatorIndex];
        }

        PackageReference manifestReference = new PackageReference(
            trimmedName,
            trimmedVersion,
            scope);
        _ = PackageCacheKey.Create(manifestReference);
        return manifestReference;
    }

    private static bool IsConcreteExactVersion(string version)
    {
        if (PackageDirective.ClassifyVersion(version) != VersionType.Exact)
            return false;

        if (version[0] is '<' or '>' or '='
            || version.Contains(" - ", StringComparison.Ordinal)
            || version.Contains(',')
            || version.Contains('*')
            || version.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (FhirSemVer.TryParse(version, out FhirSemVer? parsed)
            && parsed.IsWildcard)
        {
            return false;
        }

        return true;
    }

    private static PackageInstallException InvalidManifest(
        string? directive,
        string message,
        Exception? innerException = null) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            directive,
            innerException);

    private static PackageInstallException InvalidIdentity(
        string? directive,
        string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidPackageIdentity,
            PackageInstallStage.IdentityValidation,
            message,
            directive);
}
