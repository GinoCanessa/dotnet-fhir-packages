// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Registry;

/// <summary>
/// Indicates that a registry request or response body exceeded its configured total deadline.
/// </summary>
public sealed class RegistryResponseTimeoutException : TimeoutException
{
    /// <summary>Initializes a new timeout exception with the specified message.</summary>
    public RegistryResponseTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new timeout exception with the specified message and inner exception.</summary>
    public RegistryResponseTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
