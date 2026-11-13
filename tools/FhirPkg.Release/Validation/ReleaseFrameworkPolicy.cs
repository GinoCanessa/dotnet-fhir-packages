// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Reflection;

namespace FhirPkg.Release.Validation;

internal static class ReleaseFrameworkPolicy
{
    internal const string MetadataKey = "FhirPkgSupportedTargetFrameworks";

    internal static IReadOnlyList<string> Frameworks =>
        ReadMetadata(
            typeof(ReleaseFrameworkPolicy).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>());

    internal static IReadOnlyList<string> ReadMetadata(
        IEnumerable<AssemblyMetadataAttribute> metadata)
    {
        int count = 0;
        string? value = null;
        foreach (AssemblyMetadataAttribute attribute in metadata)
        {
            if (string.Equals(
                    attribute.Key,
                    MetadataKey,
                    StringComparison.Ordinal))
            {
                count++;
                value = attribute.Value;
            }
        }

        if (count != 1)
        {
            throw InvalidPolicy(
                $"Expected exactly one assembly metadata attribute, found {count}.");
        }

        return Parse(value);
    }

    internal static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidPolicy("The supported-framework value is missing or empty.");
        }

        string[] frameworks = value.Split(';');
        HashSet<string> uniqueFrameworks = new(StringComparer.Ordinal);
        for (int index = 0; index < frameworks.Length; index++)
        {
            string framework = frameworks[index];
            if (framework.Length == 0)
            {
                throw InvalidPolicy(
                    $"Framework segment {index + 1} is empty.");
            }

            if (!framework.StartsWith("net", StringComparison.Ordinal) ||
                !framework.EndsWith(".0", StringComparison.Ordinal) ||
                framework.Length <= 5 ||
                !int.TryParse(
                    framework.AsSpan(3, framework.Length - 5),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int major) ||
                major < 10 ||
                !string.Equals(
                    framework,
                    $"net{major.ToString(CultureInfo.InvariantCulture)}.0",
                    StringComparison.Ordinal))
            {
                throw InvalidPolicy(
                    $"Framework '{framework}' must be a canonical 'net<major>.0' identifier with major >= 10.");
            }

            if (!uniqueFrameworks.Add(framework))
            {
                throw InvalidPolicy($"Framework '{framework}' is duplicated.");
            }
        }

        return Array.AsReadOnly(frameworks);
    }

    private static ReleaseValidationException InvalidPolicy(string message) =>
        new(
            $"Invalid release build policy '{MetadataKey}': {message} " +
            "Rebuild the release tool with the repository framework policy.");
}
