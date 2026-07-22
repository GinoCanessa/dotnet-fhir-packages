// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Installation;

/// <summary>Identifies the installation stage at which a failure occurred.</summary>
public enum PackageInstallStage
{
    /// <summary>Manager and per-call policy validation.</summary>
    PolicyValidation,

    /// <summary>Directive resolution.</summary>
    Resolution,

    /// <summary>Package source acquisition.</summary>
    Acquisition,

    /// <summary>Checksum verification.</summary>
    ChecksumValidation,

    /// <summary>Archive shape and content validation.</summary>
    ArchiveValidation,

    /// <summary>Manifest identity validation.</summary>
    IdentityValidation,

    /// <summary>Existing cache inspection.</summary>
    CacheInspection,

    /// <summary>Cross-instance or cross-process coordination.</summary>
    Coordination,

    /// <summary>Cache promotion and metadata commit.</summary>
    Commit,

    /// <summary>Dependency installation.</summary>
    DependencyInstallation
}
