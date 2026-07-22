// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FhirPkg.Models;

namespace FhirPkg.Installation;

/// <summary>
/// Canonical, portable cache identity used for package paths, metadata, and coordination.
/// </summary>
public sealed class PackageCacheKey : IEquatable<PackageCacheKey>
{
    private static readonly HashSet<string> s_reservedNames = new HashSet<string>(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding s_strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string[] _relativePathSegments;

    /// <summary>Creates and validates a canonical key for a package reference.</summary>
    public PackageCacheKey(PackageReference reference)
    {
        DisplayReference = reference;

        (string? scope, string packageName) = ParsePackageName(reference);
        string canonicalPackageName = packageName.ToLowerInvariant();
        string? canonicalScope = scope?.ToLowerInvariant();
        string normalizedVersion = NormalizeDisplayVersion(reference.Version);
        ValidateVersion(normalizedVersion);

        string encodedPackageName = EncodeComponent(
            canonicalPackageName,
            encodeUppercase: false);
        string encodedVersion = EncodeComponent(
            normalizedVersion,
            encodeUppercase: true);

        _relativePathSegments = canonicalScope is null
            ? [$"{encodedPackageName}#{encodedVersion}"]
            :
            [
                $"@{EncodeComponent(canonicalScope, encodeUppercase: false)}",
                $"{encodedPackageName}#{encodedVersion}"
            ];

        string canonicalName = canonicalScope is null
            ? canonicalPackageName
            : $"@{canonicalScope}/{canonicalPackageName}";

        CanonicalReference = new PackageReference(
            canonicalName,
            normalizedVersion,
            canonicalScope is null ? null : $"@{canonicalScope}");
        CanonicalIdentity = string.Join('/', _relativePathSegments);
        RelativePath = Path.Combine(_relativePathSegments);
        MetadataKey = CanonicalIdentity;
        TransactionKey = CanonicalIdentity;
        LockHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalIdentity)))
            .ToLowerInvariant();

        if (CanonicalIdentity.Length > PackageInstallLimits.DefaultMaxArchivePathLength)
        {
            throw InvalidIdentity(
                "The canonical package cache path exceeds the portable path limit.");
        }
    }

    /// <summary>The caller-supplied reference retained for display and results.</summary>
    public PackageReference DisplayReference { get; }

    /// <summary>The normalized package reference used for identity comparisons.</summary>
    public PackageReference CanonicalReference { get; }

    /// <summary>
    /// The single reversible canonical identity from which all storage and lock keys derive.
    /// </summary>
    public string CanonicalIdentity { get; }

    /// <summary>The canonical relative path beneath a cache root.</summary>
    public string RelativePath { get; }

    /// <summary>The canonical packages.ini key.</summary>
    public string MetadataKey { get; }

    /// <summary>The canonical transaction identity.</summary>
    public string TransactionKey { get; }

    /// <summary>A stable SHA-256 hash suitable for lock and journal file names.</summary>
    public string LockHash { get; }

    /// <summary>Creates and validates a canonical key.</summary>
    public static PackageCacheKey Create(PackageReference reference) => new PackageCacheKey(reference);

    internal static void ValidatePackageName(PackageReference reference)
    {
        _ = ParsePackageName(reference);
    }

    /// <summary>
    /// Returns the canonical package directory beneath <paramref name="cacheRoot"/>
    /// after verifying containment with <see cref="Path.GetRelativePath(string, string)"/>.
    /// </summary>
    public string GetPackageDirectoryPath(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        string fullRoot = Path.GetFullPath(cacheRoot);
        string candidate = Path.GetFullPath(Path.Combine([fullRoot, .. _relativePathSegments]));
        string relative = Path.GetRelativePath(fullRoot, candidate);

        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw InvalidIdentity(
                "The canonical package cache path is not contained by the cache root.");
        }

        return candidate;
    }

    /// <summary>
    /// Attempts to parse a canonical relative cache path back into a package key.
    /// </summary>
    public static bool TryParseRelativePath(string relativePath, out PackageCacheKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        string normalized = relativePath.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length is < 1 or > 2 || segments.Any(string.IsNullOrEmpty))
            return false;

        string? encodedScope = null;
        string leaf;
        if (segments.Length == 2)
        {
            if (!segments[0].StartsWith('@') || segments[0].Length == 1)
                return false;

            encodedScope = segments[0][1..];
            leaf = segments[1];
        }
        else
        {
            leaf = segments[0];
        }

        int separatorIndex = leaf.LastIndexOf('#');
        if (separatorIndex <= 0 || separatorIndex == leaf.Length - 1)
            return false;

        try
        {
            string? scope = encodedScope is null
                ? null
                : DecodeComponent(encodedScope);
            string packageName = DecodeComponent(leaf[..separatorIndex]);
            string version = DecodeComponent(leaf[(separatorIndex + 1)..]);
            string fullName = scope is null ? packageName : $"@{scope}/{packageName}";
            PackageReference reference = new PackageReference(
                fullName,
                version,
                scope is null ? null : $"@{scope}");
            PackageCacheKey candidate = new PackageCacheKey(reference);
            if (!string.Equals(
                    candidate.CanonicalIdentity,
                    normalized,
                    StringComparison.Ordinal))
            {
                return false;
            }

            key = candidate;
            return true;
        }
        catch (PackageInstallException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool Equals(PackageCacheKey? other) =>
        other is not null
        && string.Equals(
            CanonicalIdentity,
            other.CanonicalIdentity,
            StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PackageCacheKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(CanonicalIdentity);

    /// <inheritdoc />
    public override string ToString() => CanonicalIdentity;

    private static (string? Scope, string PackageName) ParsePackageName(
        PackageReference reference)
    {
        string name = reference.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw InvalidIdentity("The package name must not be empty.");

        RejectRootedValue(name);

        if (!name.StartsWith('@'))
        {
            if (reference.Scope is not null || name.Contains('/') || name.Contains('\\'))
                throw InvalidIdentity("An unscoped package name must be one logical component.");

            ValidateLogicalComponent(name, "package name");
            return (null, name);
        }

        int slashIndex = name.IndexOf('/');
        if (slashIndex <= 1
            || slashIndex == name.Length - 1
            || name.IndexOf('/', slashIndex + 1) >= 0
            || name.Contains('\\'))
        {
            throw InvalidIdentity("A scoped package name must have the form @scope/name.");
        }

        string scopeWithMarker = name[..slashIndex];
        string scope = scopeWithMarker[1..];
        string packageName = name[(slashIndex + 1)..];

        if (reference.Scope is not null
            && !string.Equals(
                reference.Scope,
                scopeWithMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidIdentity("The package scope does not match the scoped package name.");
        }

        ValidateLogicalComponent(scope, "package scope");
        ValidateLogicalComponent(packageName, "package name");
        return (scope, packageName);
    }

    private static string NormalizeDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw InvalidIdentity("A cache package reference must include a version.");

        VersionType versionType = PackageDirective.ClassifyVersion(version);
        return versionType switch
        {
            VersionType.Exact => version,
            VersionType.CiBuild => "current",
            VersionType.LocalBuild => "dev",
            VersionType.CiBuildBranch => $"current${version["current$".Length..]}",
            _ => throw InvalidIdentity(
                "Only exact, current, current$branch, and dev references can be cache keys.")
        };
    }

    private static void ValidateVersion(string version)
    {
        RejectRootedValue(version);

        VersionType versionType = PackageDirective.ClassifyVersion(version);
        if (versionType == VersionType.CiBuildBranch)
        {
            string branch = version["current$".Length..];
            if (branch.Length == 0)
                throw InvalidIdentity("A current$branch reference must include a branch name.");

            ValidateLogicalPath(branch, "branch name");
            return;
        }

        ValidateLogicalPath(version, "package version");
    }

    private static void ValidateLogicalPath(string value, string description)
    {
        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.None);
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw InvalidIdentity(
                    $"The {description} contains an empty or traversal component.");
            }

            ValidateLogicalComponent(segment, description);
        }
    }

    private static void ValidateLogicalComponent(string value, string description)
    {
        if (value.Length == 0 || value is "." or "..")
            throw InvalidIdentity($"The {description} is empty or is a traversal component.");

        foreach (char character in value)
        {
            if (char.IsControl(character))
                throw InvalidIdentity($"The {description} contains a control character.");
        }

        string reservedCandidate = value.Split('.')[0];
        if (s_reservedNames.Contains(reservedCandidate))
            throw InvalidIdentity($"The {description} uses a reserved file name.");
    }

    private static void RejectRootedValue(string value)
    {
        if (value.StartsWith('/')
            || value.StartsWith('\\')
            || (value.Length >= 2
                && char.IsAsciiLetter(value[0])
                && value[1] == ':'))
        {
            throw InvalidIdentity("A package identity component must not be rooted.");
        }
    }

    private static string EncodeComponent(string value, bool encodeUppercase)
    {
        byte[] utf8;
        try
        {
            utf8 = s_strictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw InvalidIdentity(
                "A package identity component contains malformed UTF-16.",
                exception);
        }

        StringBuilder encoded = new StringBuilder(utf8.Length);

        for (int index = 0; index < utf8.Length; index++)
        {
            byte valueByte = utf8[index];
            bool isTrailingDot = valueByte == (byte)'.' && index == utf8.Length - 1;
            bool isAllowed = valueByte is >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-'
                or (byte)'_'
                or (byte)'.';
            bool isUppercase = valueByte is >= (byte)'A' and <= (byte)'Z';

            if (isAllowed && !isTrailingDot)
            {
                encoded.Append((char)valueByte);
            }
            else if (isUppercase && !encodeUppercase)
            {
                encoded.Append(char.ToLowerInvariant((char)valueByte));
            }
            else
            {
                encoded.Append('%');
                encoded.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    private static string DecodeComponent(string encoded)
    {
        List<byte> bytes = [];
        for (int index = 0; index < encoded.Length; index++)
        {
            char character = encoded[index];
            if (character == '%')
            {
                if (index + 2 >= encoded.Length)
                    throw new FormatException("Incomplete percent escape.");

                int high = ParseHex(encoded[index + 1]);
                int low = ParseHex(encoded[index + 2]);
                bytes.Add((byte)((high << 4) | low));
                index += 2;
                continue;
            }

            if (character > 0x7f
                || !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_'
                    or '.'))
            {
                throw new FormatException("Non-canonical character.");
            }

            bytes.Add((byte)character);
        }

        return s_strictUtf8.GetString(bytes.ToArray());
    }

    private static int ParseHex(char character) =>
        character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            _ => throw new FormatException("Invalid percent escape.")
        };

    private static PackageInstallException InvalidIdentity(
        string message,
        Exception? innerException = null) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidPackageIdentity,
            PackageInstallStage.IdentityValidation,
            message,
            innerException: innerException);
}
