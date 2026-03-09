// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Models;

/// <summary>
/// Result of downloading a package tarball from a registry.
/// </summary>
public record PackageDownloadResult : IAsyncDisposable
{
    /// <summary>The downloaded tarball content stream.</summary>
    public required Stream Content { get; init; }

    /// <summary>The MIME content type (typically "application/gzip").</summary>
    public required string ContentType { get; init; }

    /// <summary>The content length in bytes, if known.</summary>
    public long? ContentLength { get; init; }

    /// <summary>The SHA-1 checksum of the content, if provided by the registry.</summary>
    public string? ShaSum { get; init; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
