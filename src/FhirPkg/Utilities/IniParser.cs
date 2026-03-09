// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;

namespace FhirPkg.Utilities;

/// <summary>
/// Parses and writes INI-format configuration files.
/// Used for reading and writing the FHIR package cache metadata (packages.ini)
/// and version information files (version.info).
/// </summary>
/// <remarks>
/// The INI format supported is:
/// <code>
/// [section]
/// key = value
/// ; comment
/// # comment
/// </code>
/// Sections are case-preserving. Keys and values are trimmed of whitespace.
/// </remarks>
public static class IniParser
{
    /// <summary>
    /// Parses INI-format content from a string into a nested dictionary structure.
    /// </summary>
    /// <param name="content">The INI file content to parse.</param>
    /// <returns>
    /// A dictionary of section names to dictionaries of key-value pairs.
    /// Keys without a section header are placed under an empty-string section.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <c>null</c>.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentSectionName = string.Empty;

        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
                continue;

            // Section header
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                // Save the current section before starting a new one
                if (currentSection.Count > 0 || sections.ContainsKey(currentSectionName) is false)
                    sections[currentSectionName] = currentSection;

                currentSectionName = trimmed[1..^1].Trim();
                currentSection = sections.TryGetValue(currentSectionName, out var existing)
                    ? new Dictionary<string, string>((Dictionary<string, string>)existing, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            // Key-value pair
            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex > 0)
            {
                var key = trimmed[..equalsIndex].Trim();
                var value = trimmed[(equalsIndex + 1)..].Trim();
                currentSection[key] = value;
            }
            else
            {
                // Bare key with no value
                currentSection[trimmed] = string.Empty;
            }
        }

        // Save the final section
        sections[currentSectionName] = currentSection;

        return sections;
    }

    /// <summary>
    /// Parses an INI file from disk into a nested dictionary structure.
    /// Returns an empty structure if the file does not exist.
    /// </summary>
    /// <param name="filePath">Full path to the INI file.</param>
    /// <returns>
    /// A dictionary of section names to dictionaries of key-value pairs.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filePath"/> is <c>null</c>.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var content = File.ReadAllText(filePath);
        return Parse(content);
    }

    /// <summary>
    /// Serializes a nested dictionary structure into INI-format text.
    /// </summary>
    /// <param name="sections">The section/key-value structure to serialize.</param>
    /// <returns>The INI-format string representation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sections"/> is <c>null</c>.</exception>
    public static string Serialize(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var sb = new StringBuilder();

        foreach (var (sectionName, entries) in sections)
        {
            if (sectionName.Length > 0)
            {
                sb.AppendLine($"[{sectionName}]");
            }

            foreach (var (key, value) in entries)
            {
                sb.AppendLine(value.Length > 0 ? $"{key} = {value}" : key);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes a nested dictionary structure to an INI file on disk.
    /// Creates parent directories if they do not exist.
    /// </summary>
    /// <param name="filePath">Full path to the INI file to write.</param>
    /// <param name="sections">The section/key-value structure to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filePath"/> or <paramref name="sections"/> is <c>null</c>.</exception>
    public static void WriteFile(
        string filePath,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sections);

        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        File.WriteAllText(filePath, Serialize(sections));
    }
}
