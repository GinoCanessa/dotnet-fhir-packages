// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Installation;

/// <summary>Stable categories for package installation failures.</summary>
public enum PackageInstallErrorCode
{
    /// <summary>The effective installation policy is invalid.</summary>
    InvalidPolicy,

    /// <summary>The package directive could not be resolved.</summary>
    ResolutionFailed,

    /// <summary>The package source could not be acquired.</summary>
    DownloadFailed,

    /// <summary>The compressed package size exceeded policy.</summary>
    CompressedSizeLimitExceeded,

    /// <summary>The expanded package size exceeded policy.</summary>
    ExpandedSizeLimitExceeded,

    /// <summary>One archive entry exceeded policy.</summary>
    EntrySizeLimitExceeded,

    /// <summary>The archive entry count exceeded policy.</summary>
    ArchiveEntryCountLimitExceeded,

    /// <summary>A normalized archive path exceeded policy.</summary>
    ArchivePathLengthLimitExceeded,

    /// <summary>An archive path nesting depth exceeded policy.</summary>
    ArchiveDepthLimitExceeded,

    /// <summary>A package checksum did not match its expected value.</summary>
    ChecksumMismatch,

    /// <summary>The package archive is structurally invalid.</summary>
    InvalidArchive,

    /// <summary>The requested or discovered package identity is invalid.</summary>
    InvalidPackageIdentity,

    /// <summary>An existing cache target is corrupt.</summary>
    CorruptCache,

    /// <summary>Package operation coordination failed.</summary>
    CoordinationFailed,

    /// <summary>The validated package could not be committed to the cache.</summary>
    CommitFailed,

    /// <summary>The manager implementation does not support the requested capability.</summary>
    UnsupportedManagerCapability
}
