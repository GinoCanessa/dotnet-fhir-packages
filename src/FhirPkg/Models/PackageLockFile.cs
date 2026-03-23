// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirPkg.Models;

/// <summary>
/// Represents a <c>fhirpkg.lock.json</c> lock file that records the exact resolved versions
/// of all dependencies for reproducible package installations.
/// </summary>
public record PackageLockFile
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The timestamp of the last lock file update.</summary>
    [JsonPropertyName("updated")]
    public required DateTime Updated { get; init; }

    /// <summary>The resolved dependency map: package name → exact version.</summary>
    [JsonPropertyName("dependencies")]
    public required IReadOnlyDictionary<string, string> Dependencies { get; init; }

    /// <summary>
    /// Packages that could not be resolved during the last install,
    /// keyed by package name with the version constraint as value.
    /// <c>null</c> when all dependencies were resolved.
    /// </summary>
    [JsonPropertyName("missing")]
    public IReadOnlyDictionary<string, string>? Missing { get; init; }

    /// <summary>
    /// Loads a <see cref="PackageLockFile"/> from the specified file path.
    /// </summary>
    /// <param name="path">The absolute or relative path to a <c>fhirpkg.lock.json</c> file.</param>
    /// <returns>The deserialized lock file.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="JsonException">Thrown when the file contains invalid JSON.</exception>
    public static PackageLockFile Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Lock file not found: {path}", path);

        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<PackageLockFile>(stream, s_serializerOptions)
            ?? throw new JsonException("Deserialization of lock file returned null.");
    }

    /// <summary>
    /// Saves this lock file to the specified file path as formatted JSON.
    /// </summary>
    /// <param name="path">The absolute or relative path where the lock file will be written.</param>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(this, s_serializerOptions);
        File.WriteAllText(path, json);
    }
}
