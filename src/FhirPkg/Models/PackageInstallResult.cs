// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;

namespace FhirPkg.Models;

/// <summary>
/// Result of installing a single package (part of a batch operation).
/// </summary>
public record PackageInstallResult
{
    /// <summary>The original directive that was requested.</summary>
    public required string Directive { get; init; }

    /// <summary>The installed package record, if successful.</summary>
    public PackageRecord? Package { get; init; }

    /// <summary>The result status of the installation.</summary>
    public PackageInstallStatus Status { get; init; }

    /// <summary>Error message if the installation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Typed failure category when installation did not succeed.</summary>
    public PackageInstallErrorCode? ErrorCode { get; init; }

    /// <summary>Installation stage associated with <see cref="ErrorCode"/>.</summary>
    public PackageInstallStage? ErrorStage { get; init; }
}
