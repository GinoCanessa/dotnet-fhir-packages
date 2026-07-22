// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal enum PackageCacheInspectionState
{
    Missing = 0,
    Valid = 1,
    Corrupt = 2
}

internal sealed record PackageCacheInspection
{
    internal required PackageCacheInspectionState State { get; init; }

    internal required PackageCacheKey CacheKey { get; init; }

    internal required string TargetPath { get; init; }

    internal string? ContentPath { get; init; }

    internal string? ManifestPath { get; init; }

    internal PackageManifest? Manifest { get; init; }

    internal string? CorruptionReason { get; init; }

    internal bool IsRepairable { get; init; } = true;
}
