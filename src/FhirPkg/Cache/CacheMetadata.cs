// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Cache;

/// <summary>
/// Metadata about the local FHIR package cache, corresponding to the packages.ini file.
/// Tracks which packages have been installed, when they were downloaded, and their sizes.
/// </summary>
public record CacheMetadata
{
    /// <summary>
    /// Cache format version. Used for forward-compatibility detection.
    /// Current version is 3, matching the FHIR community standard.
    /// </summary>
    public int CacheVersion { get; init; } = 3;

    /// <summary>
    /// Map of package directives (e.g., "hl7.fhir.r4.core#4.0.1") to their metadata entries.
    /// Each entry records when the package was downloaded and its size.
    /// </summary>
    public IReadOnlyDictionary<string, CacheMetadataEntry> Packages { get; init; }
        = new Dictionary<string, CacheMetadataEntry>();
}

/// <summary>
/// Metadata for a single cached package entry, tracking download time and disk usage.
/// </summary>
public record CacheMetadataEntry
{
    /// <summary>
    /// UTC date and time when the package was downloaded and installed into the cache.
    /// </summary>
    public required DateTime DownloadDateTime { get; init; }

    /// <summary>
    /// Expanded size of the package content on disk, in bytes.
    /// May be <c>null</c> if the size was not recorded (e.g., for legacy cache entries).
    /// </summary>
    public long? SizeBytes { get; init; }
}
