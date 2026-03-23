// Copyright (c) Gino Canessa. Licensed under the MIT License.

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
    public static string Create(string prefix, string? fallbackRoot = null)
    {
        if (TryCreateInSystemTemp(prefix, out string? path))
        {
            return path;
        }

        if (fallbackRoot is not null)
        {
            string dir = Path.Combine(fallbackRoot, FallbackSubdirectory, $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // No fallback available — force the system temp path so the original exception propagates.
        string forcedDir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(forcedDir);
        return forcedDir;
    }

    /// <summary>
    /// Attempts to create a temporary directory under the system temp path.
    /// Returns <c>false</c> if the system temp path is empty or the directory cannot be created.
    /// </summary>
    private static bool TryCreateInSystemTemp(string prefix, out string path)
    {
        path = string.Empty;
        try
        {
            string tempRoot = Path.GetTempPath();
            if (string.IsNullOrEmpty(tempRoot))
            {
                return false;
            }

            path = Path.Combine(tempRoot, $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }
}
