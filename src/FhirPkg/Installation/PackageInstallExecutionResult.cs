// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Installation;

internal sealed record PackageInstallExecutionResult(
    PackageRecord Package,
    PackageInstallDisposition? Disposition,
    string? PreviousManifestDate);
