// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Models;

/// <summary>
/// Classifies the structure of a FHIR package name.
/// </summary>
public enum PackageNameType
{
    /// <summary>A fully-qualified core package name such as "hl7.fhir.r4.core".</summary>
    CoreFull,

    /// <summary>A partial core package name such as "hl7.fhir.r4" (no type suffix).</summary>
    CorePartial,

    /// <summary>An HL7 implementation guide package whose name ends with a FHIR version suffix (e.g. ".r4").</summary>
    GuideWithFhirSuffix,

    /// <summary>An HL7 implementation guide package without a FHIR version suffix.</summary>
    GuideWithoutSuffix,

    /// <summary>A non-HL7 package (third-party or community).</summary>
    NonHl7Guide
}

/// <summary>
/// Classifies the type of version constraint in a package directive.
/// </summary>
public enum VersionType
{
    /// <summary>An exact semantic version such as "4.0.1".</summary>
    Exact,

    /// <summary>A version pattern containing wildcards such as "4.0.x".</summary>
    Wildcard,

    /// <summary>Requests the latest published version.</summary>
    Latest,

    /// <summary>A semantic version range expression such as "^4.0.0" or "~3.0.0".</summary>
    Range,

    /// <summary>The latest continuous integration build ("current").</summary>
    CiBuild,

    /// <summary>A CI build from a specific branch ("current$branchname").</summary>
    CiBuildBranch,

    /// <summary>A local development build ("dev").</summary>
    LocalBuild
}

/// <summary>
/// Indicates the pre-release maturity stage of a FHIR version.
/// </summary>
public enum FhirPreReleaseType
{
    /// <summary>A stable, published release.</summary>
    Release = 0,

    /// <summary>A ballot (standard for trial use) release.</summary>
    Ballot = 1,

    /// <summary>A draft specification.</summary>
    Draft = 2,

    /// <summary>A snapshot release.</summary>
    Snapshot = 3,

    /// <summary>A continuous integration build.</summary>
    CiBuild = 4,

    /// <summary>An unrecognized pre-release type.</summary>
    Other = 5
}

/// <summary>
/// Enumerates the major FHIR specification releases.
/// </summary>
public enum FhirRelease
{
    /// <summary>FHIR DSTU2 (1.0.2).</summary>
    DSTU2,

    /// <summary>FHIR STU3 (3.0.2).</summary>
    STU3,

    /// <summary>FHIR R4 (4.0.1).</summary>
    R4,

    /// <summary>FHIR R4B (4.3.0).</summary>
    R4B,

    /// <summary>FHIR R5 (5.0.0).</summary>
    R5,

    /// <summary>FHIR R6 (6.0.0).</summary>
    R6
}

/// <summary>
/// Identifies the type of a FHIR core package.
/// </summary>
public enum CorePackageType
{
    /// <summary>The main specification definitions.</summary>
    Core,

    /// <summary>ValueSet expansions.</summary>
    Expansions,

    /// <summary>Example resources.</summary>
    Examples,

    /// <summary>Search parameter definitions.</summary>
    Search,

    /// <summary>XML-only core definitions.</summary>
    CoreXml,

    /// <summary>Element definitions.</summary>
    Elements
}

/// <summary>
/// Describes the result of a package install operation.
/// </summary>
public enum PackageInstallStatus
{
    /// <summary>The package was successfully installed.</summary>
    Installed,

    /// <summary>The package was already present in the cache.</summary>
    AlreadyCached,

    /// <summary>The install operation failed.</summary>
    Failed,

    /// <summary>The package was not found in any registry.</summary>
    NotFound
}

/// <summary>
/// Identifies the type of package registry.
/// </summary>
public enum RegistryType
{
    /// <summary>The FHIR NPM package registry (packages.fhir.org).</summary>
    FhirNpm,

    /// <summary>The FHIR CI build registry (build.fhir.org).</summary>
    FhirCiBuild,

    /// <summary>A FHIR HTTP endpoint that serves packages.</summary>
    FhirHttp,

    /// <summary>A standard NPM registry.</summary>
    Npm
}

/// <summary>
/// Strategy for resolving version conflicts during dependency resolution.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>The highest compatible version wins.</summary>
    HighestWins,

    /// <summary>The first-encountered version wins.</summary>
    FirstWins,

    /// <summary>Any conflict produces an error.</summary>
    Error
}

/// <summary>
/// Describes the current phase of a package installation progress report.
/// </summary>
public enum PackageProgressPhase
{
    /// <summary>Resolving the package version from registries.</summary>
    Resolving,

    /// <summary>Downloading the package tarball.</summary>
    Downloading,

    /// <summary>Extracting the package contents.</summary>
    Extracting,

    /// <summary>Indexing the package resources.</summary>
    Indexing,

    /// <summary>Installation completed successfully.</summary>
    Complete,

    /// <summary>Installation failed.</summary>
    Failed
}

/// <summary>
/// Controls how the package cache protects installed packages from mutation.
/// </summary>
public enum SafeMode
{
    /// <summary>No protection; callers may mutate cached data.</summary>
    Off,

    /// <summary>Returns cloned copies of cached data.</summary>
    Clone,

    /// <summary>Returns frozen (read-only) instances of cached data.</summary>
    Freeze
}