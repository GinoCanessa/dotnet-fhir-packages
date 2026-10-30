// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;

namespace FhirPkg.Qualification;

internal sealed record QualificationArguments
{
    internal const string DefaultSummaryTitle = "qualification";

    private static readonly HashSet<string> s_supportedOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "approval",
            "approval-commit",
            "bundle",
            "bundle-sha256",
            "cache",
            "corpus",
            "derive-bundle",
            "freeze-bundle",
            "http-timeout-seconds",
            "output",
            "process-host",
            "release-tag",
            "summary",
            "summary-title",
            "validate-only"
        };

    internal required string CacheRoot { get; init; }

    internal required string OutputPath { get; init; }

    internal required string CorpusPath { get; init; }

    internal string? ProcessHostPath { get; init; }

    internal string? FreezeBundlePath { get; init; }

    internal string? BundlePath { get; init; }

    internal string? BundleSha256 { get; init; }

    internal string? DeriveBundlePath { get; init; }

    internal string? ApprovalPath { get; init; }

    internal string? ApprovalCommit { get; init; }

    internal string? ReleaseTag { get; init; }

    internal string? SummaryPath { get; init; }

    internal required string SummaryTitle { get; init; }

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
        string? freezeBundle = values.GetValueOrDefault("freeze-bundle");
        if (freezeBundle is not null)
        {
            if (string.IsNullOrWhiteSpace(freezeBundle))
            {
                throw new ArgumentException(
                    "Option '--freeze-bundle' requires a value.");
            }

            if (validateOnly)
            {
                throw new ArgumentException(
                    "Option '--freeze-bundle' cannot be combined with '--validate-only true'.");
            }
        }

        string? bundle = values.GetValueOrDefault("bundle");
        string? bundleSha256 = values.GetValueOrDefault("bundle-sha256");
        string? deriveBundle = values.GetValueOrDefault("derive-bundle");
        string? approval = values.GetValueOrDefault("approval");
        string? approvalCommit = values.GetValueOrDefault("approval-commit");
        string? releaseTag = values.GetValueOrDefault("release-tag");
        if (deriveBundle is not null)
        {
            if (string.IsNullOrWhiteSpace(deriveBundle))
            {
                throw new ArgumentException(
                    "Option '--derive-bundle' requires a value.");
            }

            if (validateOnly)
            {
                throw new ArgumentException(
                    "Option '--derive-bundle' cannot be combined with '--validate-only true'.");
            }

            if (freezeBundle is not null)
            {
                throw new ArgumentException(
                    "Option '--derive-bundle' cannot be combined with '--freeze-bundle'.");
            }

            if (bundle is null)
            {
                throw new ArgumentException(
                    "Option '--bundle' is required with '--derive-bundle'.");
            }

            if (bundleSha256 is null)
            {
                throw new ArgumentException(
                    "Option '--bundle-sha256' is required with '--derive-bundle'.");
            }

            if (approval is null)
            {
                throw new ArgumentException(
                    "Option '--approval' is required with '--derive-bundle'.");
            }

            if (approvalCommit is null)
            {
                throw new ArgumentException(
                    "Option '--approval-commit' is required with '--derive-bundle'.");
            }

            if (releaseTag is null)
            {
                throw new ArgumentException(
                    "Option '--release-tag' is required with '--derive-bundle'.");
            }

            if (string.IsNullOrWhiteSpace(approval))
            {
                throw new ArgumentException(
                    "Option '--approval' requires a value.");
            }

            if (!FixtureBundle.IsCommit(approvalCommit))
            {
                throw new ArgumentException(
                    "Option '--approval-commit' must be exactly 40 lowercase hexadecimal characters.");
            }
        }
        else if (approval is not null)
        {
            throw new ArgumentException(
                "Option '--approval' requires '--derive-bundle'.");
        }
        else if (approvalCommit is not null)
        {
            throw new ArgumentException(
                "Option '--approval-commit' requires '--derive-bundle'.");
        }

        if (bundle is not null)
        {
            if (string.IsNullOrWhiteSpace(bundle))
            {
                throw new ArgumentException(
                    "Option '--bundle' requires a value.");
            }

            if (validateOnly)
            {
                throw new ArgumentException(
                    "Option '--bundle' cannot be combined with '--validate-only true'.");
            }

            if (freezeBundle is not null)
            {
                throw new ArgumentException(
                    "Option '--bundle' cannot be combined with '--freeze-bundle'.");
            }

            if (bundleSha256 is null)
            {
                throw new ArgumentException(
                    "Option '--bundle-sha256' is required with '--bundle'.");
            }

            if (!FixtureBundle.IsSha256(bundleSha256))
            {
                throw new ArgumentException(
                    "Option '--bundle-sha256' must be exactly 64 lowercase hexadecimal characters.");
            }
        }
        else if (bundleSha256 is not null)
        {
            throw new ArgumentException(
                "Option '--bundle-sha256' requires '--bundle'.");
        }

        if (releaseTag is not null)
        {
            if (string.IsNullOrWhiteSpace(releaseTag))
            {
                throw new ArgumentException(
                    "Option '--release-tag' requires a value.");
            }

            if (bundle is null)
            {
                throw new ArgumentException(
                    "Option '--release-tag' requires '--bundle'.");
            }
        }

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
            FreezeBundlePath = freezeBundle is null
                ? null
                : Path.GetFullPath(freezeBundle),
            BundlePath = bundle is null
                ? null
                : Path.GetFullPath(bundle),
            BundleSha256 = bundleSha256,
            DeriveBundlePath = deriveBundle is null
                ? null
                : Path.GetFullPath(deriveBundle),
            ApprovalPath = approval is null
                ? null
                : Path.GetFullPath(approval),
            ApprovalCommit = approvalCommit,
            ReleaseTag = releaseTag,
            SummaryPath = values.TryGetValue(
                "summary",
                out string? summary)
                ? Path.GetFullPath(summary)
                : null,
            SummaryTitle = NormalizeSummaryTitle(
                values.GetValueOrDefault("summary-title")),
            HttpTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            ValidateOnly = validateOnly
        };
    }

    internal static string NormalizeSummaryTitle(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DefaultSummaryTitle
            : value;

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
