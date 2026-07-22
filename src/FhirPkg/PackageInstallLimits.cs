// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using FhirPkg.Installation;

namespace FhirPkg;

/// <summary>
/// Finite resource limits applied to package acquisition and archive processing.
/// </summary>
public sealed class PackageInstallLimits
{
    /// <summary>The default maximum compressed package size (100 MiB).</summary>
    public const long DefaultMaxCompressedBytes = 100L * 1024 * 1024;

    /// <summary>The default maximum expanded package size (1 GiB).</summary>
    public const long DefaultMaxExpandedBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>The default maximum expanded size of one archive entry (128 MiB).</summary>
    public const long DefaultMaxEntryBytes = 128L * 1024 * 1024;

    /// <summary>The default maximum number of entries in an archive.</summary>
    public const int DefaultMaxArchiveEntries = 50_000;

    /// <summary>The default maximum length of a normalized archive path.</summary>
    public const int DefaultMaxArchivePathLength = 1_024;

    /// <summary>The default maximum normalized archive nesting depth.</summary>
    public const int DefaultMaxArchiveDepth = 32;

    /// <summary>Environment variable that configures <see cref="MaxCompressedBytes"/>.</summary>
    public const string MaxCompressedBytesEnvironmentVariable = "FHIRPKG_MAX_COMPRESSED_BYTES";

    /// <summary>Environment variable that configures <see cref="MaxExpandedBytes"/>.</summary>
    public const string MaxExpandedBytesEnvironmentVariable = "FHIRPKG_MAX_EXPANDED_BYTES";

    /// <summary>Environment variable that configures <see cref="MaxEntryBytes"/>.</summary>
    public const string MaxEntryBytesEnvironmentVariable = "FHIRPKG_MAX_ENTRY_BYTES";

    /// <summary>Environment variable that configures <see cref="MaxArchiveEntries"/>.</summary>
    public const string MaxArchiveEntriesEnvironmentVariable = "FHIRPKG_MAX_ARCHIVE_ENTRIES";

    /// <summary>Environment variable that configures <see cref="MaxArchivePathLength"/>.</summary>
    public const string MaxArchivePathLengthEnvironmentVariable = "FHIRPKG_MAX_ARCHIVE_PATH_LENGTH";

    /// <summary>Environment variable that configures <see cref="MaxArchiveDepth"/>.</summary>
    public const string MaxArchiveDepthEnvironmentVariable = "FHIRPKG_MAX_ARCHIVE_DEPTH";

    private long? _maxCompressedBytes;
    private long? _maxExpandedBytes;
    private long? _maxEntryBytes;
    private int? _maxArchiveEntries;
    private int? _maxArchivePathLength;
    private int? _maxArchiveDepth;

    /// <summary>Maximum compressed bytes accepted from a package source.</summary>
    public long MaxCompressedBytes
    {
        get => _maxCompressedBytes ?? DefaultMaxCompressedBytes;
        set => _maxCompressedBytes = value;
    }

    /// <summary>Maximum aggregate bytes produced by archive extraction.</summary>
    public long MaxExpandedBytes
    {
        get => _maxExpandedBytes ?? DefaultMaxExpandedBytes;
        set => _maxExpandedBytes = value;
    }

    /// <summary>Maximum expanded bytes accepted for one archive entry.</summary>
    public long MaxEntryBytes
    {
        get => _maxEntryBytes ?? DefaultMaxEntryBytes;
        set => _maxEntryBytes = value;
    }

    /// <summary>Maximum number of archive entries accepted.</summary>
    public int MaxArchiveEntries
    {
        get => _maxArchiveEntries ?? DefaultMaxArchiveEntries;
        set => _maxArchiveEntries = value;
    }

    /// <summary>Maximum length of one normalized archive path.</summary>
    public int MaxArchivePathLength
    {
        get => _maxArchivePathLength ?? DefaultMaxArchivePathLength;
        set => _maxArchivePathLength = value;
    }

    /// <summary>Maximum normalized archive nesting depth.</summary>
    public int MaxArchiveDepth
    {
        get => _maxArchiveDepth ?? DefaultMaxArchiveDepth;
        set => _maxArchiveDepth = value;
    }

    /// <summary>
    /// Reads and validates installation limits from the current process environment.
    /// Unset values use the documented defaults.
    /// </summary>
    public static PackageInstallLimits FromEnvironment() =>
        ResolveManager(new PackageInstallLimits());

    /// <summary>Validates this limit set.</summary>
    public void Validate() => ValidateResolved(this);

    internal static PackageInstallLimits ResolveManager(PackageInstallLimits? configured)
    {
        if (configured is null)
        {
            throw InvalidPolicy("Manager installation limits must not be null.");
        }

        PackageInstallLimits resolved = new PackageInstallLimits
        {
            MaxCompressedBytes = configured._maxCompressedBytes
                ?? ReadLong(MaxCompressedBytesEnvironmentVariable, DefaultMaxCompressedBytes),
            MaxExpandedBytes = configured._maxExpandedBytes
                ?? ReadLong(MaxExpandedBytesEnvironmentVariable, DefaultMaxExpandedBytes),
            MaxEntryBytes = configured._maxEntryBytes
                ?? ReadLong(MaxEntryBytesEnvironmentVariable, DefaultMaxEntryBytes),
            MaxArchiveEntries = configured._maxArchiveEntries
                ?? ReadInt(MaxArchiveEntriesEnvironmentVariable, DefaultMaxArchiveEntries),
            MaxArchivePathLength = configured._maxArchivePathLength
                ?? ReadInt(MaxArchivePathLengthEnvironmentVariable, DefaultMaxArchivePathLength),
            MaxArchiveDepth = configured._maxArchiveDepth
                ?? ReadInt(MaxArchiveDepthEnvironmentVariable, DefaultMaxArchiveDepth)
        };

        ValidateResolved(resolved);
        return resolved;
    }

    internal static PackageInstallLimits ResolvePerCall(
        PackageInstallLimits managerLimits,
        PackageInstallLimits? requestedLimits)
    {
        ArgumentNullException.ThrowIfNull(managerLimits);
        ValidateResolved(managerLimits);

        PackageInstallLimits resolved = Copy(managerLimits);
        if (requestedLimits is null)
            return resolved;

        ApplyTighteningOverride(
            requestedLimits._maxCompressedBytes,
            managerLimits.MaxCompressedBytes,
            nameof(MaxCompressedBytes),
            value => resolved.MaxCompressedBytes = value);
        ApplyTighteningOverride(
            requestedLimits._maxExpandedBytes,
            managerLimits.MaxExpandedBytes,
            nameof(MaxExpandedBytes),
            value => resolved.MaxExpandedBytes = value);
        ApplyTighteningOverride(
            requestedLimits._maxEntryBytes,
            managerLimits.MaxEntryBytes,
            nameof(MaxEntryBytes),
            value => resolved.MaxEntryBytes = value);
        ApplyTighteningOverride(
            requestedLimits._maxArchiveEntries,
            managerLimits.MaxArchiveEntries,
            nameof(MaxArchiveEntries),
            value => resolved.MaxArchiveEntries = value);
        ApplyTighteningOverride(
            requestedLimits._maxArchivePathLength,
            managerLimits.MaxArchivePathLength,
            nameof(MaxArchivePathLength),
            value => resolved.MaxArchivePathLength = value);
        ApplyTighteningOverride(
            requestedLimits._maxArchiveDepth,
            managerLimits.MaxArchiveDepth,
            nameof(MaxArchiveDepth),
            value => resolved.MaxArchiveDepth = value);

        ValidateResolved(resolved);
        return resolved;
    }

    private static void ApplyTighteningOverride(
        long? requestedValue,
        long managerValue,
        string propertyName,
        Action<long> apply)
    {
        if (!requestedValue.HasValue)
            return;

        if (requestedValue.Value > managerValue)
        {
            throw InvalidPolicy(
                $"Per-call {propertyName} must not exceed the manager limit.");
        }

        apply(requestedValue.Value);
    }

    private static void ApplyTighteningOverride(
        int? requestedValue,
        int managerValue,
        string propertyName,
        Action<int> apply)
    {
        if (!requestedValue.HasValue)
            return;

        if (requestedValue.Value > managerValue)
        {
            throw InvalidPolicy(
                $"Per-call {propertyName} must not exceed the manager limit.");
        }

        apply(requestedValue.Value);
    }

    private static long ReadLong(string variableName, long defaultValue)
    {
        string? rawValue = Environment.GetEnvironmentVariable(variableName);
        if (rawValue is null)
            return defaultValue;

        if (!long.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long value)
            || value <= 0)
        {
            throw InvalidPolicy(
                $"Environment variable {variableName} must be a positive invariant integer.");
        }

        return value;
    }

    private static int ReadInt(string variableName, int defaultValue)
    {
        string? rawValue = Environment.GetEnvironmentVariable(variableName);
        if (rawValue is null)
            return defaultValue;

        if (!int.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int value)
            || value <= 0)
        {
            throw InvalidPolicy(
                $"Environment variable {variableName} must be a positive invariant integer.");
        }

        return value;
    }

    private static void ValidateResolved(PackageInstallLimits limits)
    {
        ValidatePositive(limits.MaxCompressedBytes, nameof(MaxCompressedBytes));
        ValidatePositive(limits.MaxExpandedBytes, nameof(MaxExpandedBytes));
        ValidatePositive(limits.MaxEntryBytes, nameof(MaxEntryBytes));
        ValidatePositive(limits.MaxArchiveEntries, nameof(MaxArchiveEntries));
        ValidatePositive(limits.MaxArchivePathLength, nameof(MaxArchivePathLength));
        ValidatePositive(limits.MaxArchiveDepth, nameof(MaxArchiveDepth));

        if (limits.MaxEntryBytes > limits.MaxExpandedBytes)
        {
            throw InvalidPolicy(
                $"{nameof(MaxEntryBytes)} must not exceed {nameof(MaxExpandedBytes)}.");
        }

        try
        {
            _ = checked(limits.MaxCompressedBytes + limits.MaxExpandedBytes);
            _ = checked(limits.MaxEntryBytes * limits.MaxArchiveEntries);
        }
        catch (OverflowException exception)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPolicy,
                PackageInstallStage.PolicyValidation,
                "Installation limits are too large for safe accounting.",
                innerException: exception);
        }
    }

    private static void ValidatePositive(long value, string propertyName)
    {
        if (value <= 0)
            throw InvalidPolicy($"{propertyName} must be greater than zero.");
    }

    private static PackageInstallLimits Copy(PackageInstallLimits source) =>
        new PackageInstallLimits
        {
            MaxCompressedBytes = source.MaxCompressedBytes,
            MaxExpandedBytes = source.MaxExpandedBytes,
            MaxEntryBytes = source.MaxEntryBytes,
            MaxArchiveEntries = source.MaxArchiveEntries,
            MaxArchivePathLength = source.MaxArchivePathLength,
            MaxArchiveDepth = source.MaxArchiveDepth
        };

    private static PackageInstallException InvalidPolicy(string message) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidPolicy,
            PackageInstallStage.PolicyValidation,
            message);
}

/// <summary>
/// Controls how installation handles an existing cache entry that is invalid.
/// </summary>
public enum CorruptCacheBehavior
{
    /// <summary>Quarantine and replace the invalid entry after validation succeeds.</summary>
    Repair,

    /// <summary>Reject installation with a typed corruption error.</summary>
    Strict,

    /// <summary>Alias for <see cref="Strict"/>.</summary>
    Throw = Strict
}
