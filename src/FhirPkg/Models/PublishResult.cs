// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;

namespace FhirPkg.Models;

/// <summary>
/// Result of a publish operation to a package registry.
/// </summary>
public record PublishResult
{
    /// <summary>Whether the publish operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Descriptive message about the outcome.</summary>
    public string? Message { get; init; }

    /// <summary>The HTTP status code returned by the registry.</summary>
    public HttpStatusCode StatusCode { get; init; }
}
