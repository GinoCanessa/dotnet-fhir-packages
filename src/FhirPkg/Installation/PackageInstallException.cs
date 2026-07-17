// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Installation;

/// <summary>
/// Represents a typed package installation failure.
/// Cancellation is never wrapped in this exception.
/// </summary>
public class PackageInstallException : InvalidOperationException
{
    /// <summary>Creates a typed package installation failure.</summary>
    public PackageInstallException(
        PackageInstallErrorCode errorCode,
        PackageInstallStage stage,
        string message,
        string? directive = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Stage = stage;
        Directive = directive;
    }

    /// <summary>The stable failure category.</summary>
    public PackageInstallErrorCode ErrorCode { get; }

    /// <summary>The installation stage that failed.</summary>
    public PackageInstallStage Stage { get; }

    /// <summary>The requested package directive when it is safe to report.</summary>
    public string? Directive { get; }
}
