// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;

namespace FhirPkg.Utilities;

/// <summary>
/// Provides checksum computation and verification for FHIR package integrity.
/// Supports SHA-1 (default, for NPM registry compatibility) and SHA-256.
/// </summary>
/// <remarks>
/// <para>
/// SHA-1 is used as the default algorithm because the NPM/FHIR package ecosystem
/// provides SHA-1 checksums (<c>shasum</c>) in registry metadata. While SHA-1 is
/// considered cryptographically weak for collision resistance, it remains adequate
/// for integrity verification of package tarballs.
/// </para>
/// <para>
/// SHA-256 methods are provided for registries or workflows that supply stronger
/// checksums. Prefer SHA-256 when the registry supports it.
/// </para>
/// </remarks>
public static class CheckSum
{
    /// <summary>
    /// Computes the SHA-1 hash of a stream and returns it as a lowercase hex string.
    /// The stream position is not reset after reading.
    /// </summary>
    /// <param name="stream">The stream to hash. Must be readable.</param>
    /// <returns>A 40-character lowercase hexadecimal SHA-1 hash string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <c>null</c>.</exception>
    public static string ComputeSha1(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] hash = SHA1.HashData(stream);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Computes the SHA-1 hash of a byte array and returns it as a lowercase hex string.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>A 40-character lowercase hexadecimal SHA-1 hash string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    public static string ComputeSha1(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] hash = SHA1.HashData(data);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Computes the SHA-256 hash of a stream and returns it as a lowercase hex string.
    /// The stream position is not reset after reading.
    /// </summary>
    /// <param name="stream">The stream to hash. Must be readable.</param>
    /// <returns>A 64-character lowercase hexadecimal SHA-256 hash string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <c>null</c>.</exception>
    public static string ComputeSha256(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] hash = SHA256.HashData(stream);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Computes the SHA-256 hash of a byte array and returns it as a lowercase hex string.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>A 64-character lowercase hexadecimal SHA-256 hash string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    public static string ComputeSha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] hash = SHA256.HashData(data);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Verifies that the SHA-1 hash of a stream matches the expected hash.
    /// If <paramref name="expectedHash"/> is <c>null</c> or empty, verification is skipped and <c>true</c> is returned.
    /// The comparison is case-insensitive.
    /// </summary>
    /// <param name="stream">The stream to verify. Must be readable.</param>
    /// <param name="expectedHash">The expected SHA-1 hash (hex string), or <c>null</c> to skip verification.</param>
    /// <param name="resetPosition">
    /// When <c>true</c> (default) and the stream supports seeking, the stream position is reset
    /// to its original position after computing the hash.
    /// </param>
    /// <returns><c>true</c> if the hash matches or verification was skipped; <c>false</c> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <c>null</c>.</exception>
    public static bool Verify(Stream stream, string? expectedHash, bool resetPosition = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrWhiteSpace(expectedHash))
            return true;

        long originalPosition = stream.CanSeek ? stream.Position : -1;
        string actualHash = ComputeSha1(stream);

        if (resetPosition && stream.CanSeek)
            stream.Position = originalPosition;

        return string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the SHA-256 hash of a stream matches the expected hash.
    /// If <paramref name="expectedHash"/> is <c>null</c> or empty, verification is skipped and <c>true</c> is returned.
    /// The comparison is case-insensitive.
    /// </summary>
    /// <param name="stream">The stream to verify. Must be readable.</param>
    /// <param name="expectedHash">The expected SHA-256 hash (hex string), or <c>null</c> to skip verification.</param>
    /// <param name="resetPosition">
    /// When <c>true</c> (default) and the stream supports seeking, the stream position is reset
    /// to its original position after computing the hash.
    /// </param>
    /// <returns><c>true</c> if the hash matches or verification was skipped; <c>false</c> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <c>null</c>.</exception>
    public static bool VerifySha256(Stream stream, string? expectedHash, bool resetPosition = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrWhiteSpace(expectedHash))
            return true;

        long originalPosition = stream.CanSeek ? stream.Position : -1;
        string actualHash = ComputeSha256(stream);

        if (resetPosition && stream.CanSeek)
            stream.Position = originalPosition;

        return string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
