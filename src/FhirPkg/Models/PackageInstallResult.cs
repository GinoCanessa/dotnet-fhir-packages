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

    /// <summary>
    /// The installed package record. For dependency-stage failures, this is the
    /// committed root package retained as partial state.
    /// </summary>
    public PackageRecord? Package { get; init; }

    /// <summary>The result status of the installation.</summary>
    public PackageInstallStatus Status { get; init; }

    /// <summary>Error message if the installation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Typed failure category when installation did not succeed.</summary>
    public PackageInstallErrorCode? ErrorCode { get; init; }

    /// <summary>Installation stage associated with <see cref="ErrorCode"/>.</summary>
    public PackageInstallStage? ErrorStage { get; init; }

    /// <summary>
    /// Failed child operations when the root package committed but dependency
    /// installation did not complete.
    /// </summary>
    public IReadOnlyList<PackageInstallResult> DependencyFailures { get; init; } = [];

    /// <summary>
    /// Returns a stable typed failure description suitable for human-readable
    /// diagnostics, or <c>null</c> when no failure information is present.
    /// </summary>
    public string? GetFailureDescription()
    {
        if (ErrorCode is null)
            return ErrorMessage;

        string category = ErrorStage is PackageInstallStage stage
            ? $"{ErrorCode}/{stage}"
            : ErrorCode.ToString()!;
        return string.IsNullOrWhiteSpace(ErrorMessage)
            ? category
            : $"[{category}] {ErrorMessage}";
    }
}
