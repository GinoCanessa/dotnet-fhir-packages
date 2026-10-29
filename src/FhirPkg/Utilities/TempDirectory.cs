// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Runtime.ExceptionServices;
using FhirPkg.IO;

namespace FhirPkg.Utilities;

/// <summary>
/// Provides helpers for creating temporary directories with fallback behaviour.
/// The primary location is the system temporary directory (<see cref="Path.GetTempPath"/>).
/// When the system path is unavailable or not writable, the caller may supply a fallback
/// root (typically the FHIR package cache directory) under which a <c>.temp</c> subdirectory
/// is created instead.
/// </summary>
public static class TempDirectory
{
    private const string FallbackSubdirectory = ".temp";

    /// <summary>
    /// Creates a uniquely-named temporary directory.
    /// </summary>
    /// <param name="prefix">Name prefix for the directory (e.g. <c>fhir-pkg</c>).</param>
    /// <param name="fallbackRoot">
    /// Optional fallback root. When provided and the system temp path is unusable, a
    /// <c>.temp/{prefix}-{guid}</c> subdirectory is created under this path instead.
    /// </param>
    /// <returns>The full path to the newly created temporary directory.</returns>
    /// <exception cref="IOException">
    /// Thrown when the directory could not be created in any location.
    /// </exception>
    public static string Create(string prefix, string? fallbackRoot = null) =>
        Create(prefix, fallbackRoot, FileSystemContext.CreateDefault());

    internal static string Create(string prefix, string? fallbackRoot, FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One primary attempt sequence: discovery and creation share no renewed budget.
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            string tempRoot = context.FileSystem.GetTempPath();
            if (!string.IsNullOrEmpty(tempRoot))
            {
                string path = Path.Combine(tempRoot, $"{prefix}-{Guid.NewGuid():N}");
                context.FileSystem.CreateDirectory(path);
                return path;
            }
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (fallbackRoot is not null)
        {
            // A distinct destination: an independent operation, not a retry of the primary.
            string dir = Path.Combine(fallbackRoot, FallbackSubdirectory, $"{prefix}-{Guid.NewGuid():N}");
            context.FileSystem.CreateDirectory(dir);
            return dir;
        }

        // No fallback available: surface the captured primary failure rather than
        // starting a second system-temp sequence.
        primaryFailure?.Throw();
        throw new IOException("The system temporary directory path is empty, so no temporary directory could be created.");
    }

    // Invalid input and cancellation never become fallback signals.
    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
        || FileSystemFailureContext.TryGetOrigin(exception, out _);
}
