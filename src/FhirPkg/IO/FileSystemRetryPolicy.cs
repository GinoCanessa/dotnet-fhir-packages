// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using FhirPkg.Installation;

namespace FhirPkg.IO;

/// <summary>
/// Validated, immutable effective values. Equality compares settings, not their source.
/// </summary>
internal sealed record FileSystemRetryPolicy
{
    private FileSystemRetryPolicy(
        int retryCount,
        int minimumDelayMilliseconds,
        int maximumJitterMilliseconds)
    {
        RetryCount = retryCount;
        MinimumDelayMilliseconds = minimumDelayMilliseconds;
        MaximumJitterMilliseconds = maximumJitterMilliseconds;
    }

    internal int RetryCount { get; }

    internal int MinimumDelayMilliseconds { get; }

    internal int MaximumJitterMilliseconds { get; }

    internal long MaximumAttemptCount => (long)RetryCount + 1;

    internal int MaximumDelayMilliseconds =>
        (int)((long)MinimumDelayMilliseconds + MaximumJitterMilliseconds);

    internal static FileSystemRetryPolicy Resolve(FileSystemRetryOptions? configured) =>
        Resolve(configured, out _);

    // Keep this provenance alongside the policy when copying fully populated options.
    // Resolving a snapshot again would both reread configuration and lose its source.
    internal static FileSystemRetryPolicy Resolve(
        FileSystemRetryOptions? configured,
        out bool hasExplicitValues) =>
        Resolve(configured, Environment.GetEnvironmentVariable, out hasExplicitValues);

    internal static FileSystemRetryPolicy Resolve(
        FileSystemRetryOptions? configured,
        Func<string, string?> readEnvironment,
        out bool hasExplicitValues)
    {
        if (configured is null)
            throw InvalidPolicy("FileSystemRetry must not be null.");

        ArgumentNullException.ThrowIfNull(readEnvironment);
        hasExplicitValues = configured.HasExplicitValues;

        int retryCount = configured.ExplicitRetryCount
            ?? ReadInt(
                readEnvironment,
                FileSystemRetryOptions.RetryCountEnvironmentVariable,
                FileSystemRetryOptions.DefaultRetryCount);
        int minimumDelayMilliseconds = configured.ExplicitMinimumDelayMilliseconds
            ?? ReadInt(
                readEnvironment,
                FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
                FileSystemRetryOptions.DefaultMinimumDelayMilliseconds);
        int maximumJitterMilliseconds = configured.ExplicitMaximumJitterMilliseconds
            ?? ReadInt(
                readEnvironment,
                FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
                FileSystemRetryOptions.DefaultMaximumJitterMilliseconds);

        ValidateNonnegative(retryCount, nameof(FileSystemRetryOptions.RetryCount));
        ValidateNonnegative(
            minimumDelayMilliseconds,
            nameof(FileSystemRetryOptions.MinimumDelayMilliseconds));
        ValidateNonnegative(
            maximumJitterMilliseconds,
            nameof(FileSystemRetryOptions.MaximumJitterMilliseconds));

        if ((long)minimumDelayMilliseconds + maximumJitterMilliseconds > int.MaxValue)
        {
            throw InvalidPolicy(
                "MinimumDelayMilliseconds + MaximumJitterMilliseconds must not exceed Int32.MaxValue.");
        }

        return new FileSystemRetryPolicy(
            retryCount,
            minimumDelayMilliseconds,
            maximumJitterMilliseconds);
    }

    internal FileSystemRetryOptions ToOptionsSnapshot() =>
        new FileSystemRetryOptions
        {
            RetryCount = RetryCount,
            MinimumDelayMilliseconds = MinimumDelayMilliseconds,
            MaximumJitterMilliseconds = MaximumJitterMilliseconds,
        };

    private static int ReadInt(
        Func<string, string?> readEnvironment,
        string variableName,
        int defaultValue)
    {
        string? rawValue = readEnvironment(variableName);
        if (rawValue is null)
            return defaultValue;

        if (!int.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int value))
        {
            throw InvalidPolicy(
                $"Environment variable {variableName} must be a nonnegative invariant integer no greater than Int32.MaxValue.");
        }

        return value;
    }

    private static void ValidateNonnegative(int value, string propertyName)
    {
        if (value < 0)
            throw InvalidPolicy($"{propertyName} must not be negative.");
    }

    private static PackageInstallException InvalidPolicy(string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidPolicy,
            PackageInstallStage.PolicyValidation,
            message);
}
