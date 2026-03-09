// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;

namespace FhirPkg.Utilities;

/// <summary>
/// Provides SHA-1 checksum computation and verification for FHIR package integrity.
/// Used to validate downloaded tarballs against registry-provided checksums.
/// </summary>
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
    /// Verifies that the SHA-1 hash of a stream matches the expected hash.
    /// If <paramref name="expectedHash"/> is <c>null</c> or empty, verification is skipped and <c>true</c> is returned.
    /// The comparison is case-insensitive.
    /// </summary>
    /// <param name="stream">The stream to verify. Must be readable.</param>
    /// <param name="expectedHash">The expected SHA-1 hash (hex string), or <c>null</c> to skip verification.</param>
    /// <returns><c>true</c> if the hash matches or verification was skipped; <c>false</c> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <c>null</c>.</exception>
    public static bool Verify(Stream stream, string? expectedHash)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrWhiteSpace(expectedHash))
            return true;

        var actualHash = ComputeSha1(stream);
        return string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
