// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;

namespace FhirPkg.Qualification;

internal sealed record QualificationArguments
{
    private static readonly HashSet<string> s_supportedOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cache",
            "corpus",
            "http-timeout-seconds",
            "output",
            "process-host",
            "validate-only"
        };

    internal required string CacheRoot { get; init; }

    internal required string OutputPath { get; init; }

    internal required string CorpusPath { get; init; }

    internal string? ProcessHostPath { get; init; }

    internal required TimeSpan HttpTimeout { get; init; }

    internal required bool ValidateOnly { get; init; }

    internal static QualificationArguments Parse(string[] args)
    {
        Dictionary<string, string> values =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unexpected argument '{argument}'.");
            }

            string name = argument[2..];
            if (!s_supportedOptions.Contains(name))
            {
                throw new ArgumentException(
                    $"Unsupported option '{argument}'.");
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException(
                    $"Option '{argument}' requires a value.");
            }

            if (!values.TryAdd(name, args[index]))
            {
                throw new ArgumentException(
                    $"Option '{argument}' was specified more than once.");
            }
        }

        string output = GetRequired(values, "output");
        bool validateOnly = GetBoolean(
            values,
            "validate-only",
            defaultValue: false);
        string cache = values.TryGetValue(
            "cache",
            out string? cacheValue)
            && !string.IsNullOrWhiteSpace(cacheValue)
            ? cacheValue
            : validateOnly
                ? Path.Combine(
                    Path.GetDirectoryName(
                        Path.GetFullPath(output))
                        ?? Environment.CurrentDirectory,
                    ".qualification-validation")
                : GetRequired(values, "cache");
        string corpus = values.TryGetValue(
            "corpus",
            out string? corpusValue)
            ? corpusValue
            : Path.Combine(
                AppContext.BaseDirectory,
                "qualification-corpus.json");
        int timeoutSeconds = values.TryGetValue(
            "http-timeout-seconds",
            out string? timeoutValue)
            ? ParsePositiveInteger(
                timeoutValue,
                "http-timeout-seconds")
            : 600;

        return new QualificationArguments
        {
            CacheRoot = Path.GetFullPath(cache),
            OutputPath = Path.GetFullPath(output),
            CorpusPath = Path.GetFullPath(corpus),
            ProcessHostPath = values.TryGetValue(
                "process-host",
                out string? processHost)
                ? Path.GetFullPath(processHost)
                : null,
            HttpTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            ValidateOnly = validateOnly
        };
    }

    private static bool GetBoolean(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool defaultValue)
    {
        if (!values.TryGetValue(name, out string? value))
            return defaultValue;

        if (bool.TryParse(value, out bool parsed))
            return parsed;

        throw new ArgumentException(
            $"Option '--{name}' must be true or false.");
    }

    private static int ParsePositiveInteger(
        string value,
        string name)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed)
            || parsed <= 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Option '--{name}' must be a positive integer.");
        }

        return parsed;
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out string? value)
            && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                $"Option '--{name}' is required.");
}
