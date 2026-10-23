// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg;

/// <summary>
/// Configures bounded retries of safely replayable filesystem operations.
/// </summary>
/// <remarks>
/// Each explicitly assigned value overrides its environment variable. Unspecified
/// values use the environment and then the documented default. Assigning a default
/// value or zero still counts as explicit configuration. Components snapshot the
/// effective values; later option or environment changes do not reconfigure them.
/// </remarks>
public sealed class FileSystemRetryOptions
{
    /// <summary>The default number of retries after the initial attempt.</summary>
    public const int DefaultRetryCount = 5;

    /// <summary>The default minimum delay between attempts, in milliseconds.</summary>
    public const int DefaultMinimumDelayMilliseconds = 10;

    /// <summary>The default inclusive maximum additive jitter, in milliseconds.</summary>
    public const int DefaultMaximumJitterMilliseconds = 10;

    /// <summary>Environment variable that configures <see cref="RetryCount"/>.</summary>
    public const string RetryCountEnvironmentVariable = "FHIRPKG_FS_RETRY_COUNT";

    /// <summary>Environment variable that configures <see cref="MinimumDelayMilliseconds"/>.</summary>
    public const string MinimumDelayMillisecondsEnvironmentVariable = "FHIRPKG_FS_MIN_DELAY_MS";

    /// <summary>Environment variable that configures <see cref="MaximumJitterMilliseconds"/>.</summary>
    public const string MaximumJitterMillisecondsEnvironmentVariable = "FHIRPKG_FS_JITTER_MAX_MS";

    private int? _retryCount;
    private int? _minimumDelayMilliseconds;
    private int? _maximumJitterMilliseconds;

    /// <summary>
    /// Number of retries after the first attempt. Must be nonnegative; zero
    /// disables retries. Default: 5.
    /// </summary>
    public int RetryCount
    {
        get => _retryCount ?? DefaultRetryCount;
        set => _retryCount = value;
    }

    /// <summary>
    /// Minimum retry delay in milliseconds. Must be nonnegative. Default: 10.
    /// </summary>
    public int MinimumDelayMilliseconds
    {
        get => _minimumDelayMilliseconds ?? DefaultMinimumDelayMilliseconds;
        set => _minimumDelayMilliseconds = value;
    }

    /// <summary>
    /// Inclusive maximum of fresh, uniformly sampled additive jitter in milliseconds.
    /// Must be nonnegative. Its sum with <see cref="MinimumDelayMilliseconds"/>
    /// must not exceed <see cref="int.MaxValue"/>. Default: 10.
    /// </summary>
    public int MaximumJitterMilliseconds
    {
        get => _maximumJitterMilliseconds ?? DefaultMaximumJitterMilliseconds;
        set => _maximumJitterMilliseconds = value;
    }

    internal int? ExplicitRetryCount => _retryCount;

    internal int? ExplicitMinimumDelayMilliseconds => _minimumDelayMilliseconds;

    internal int? ExplicitMaximumJitterMilliseconds => _maximumJitterMilliseconds;

    internal bool HasExplicitValues =>
        _retryCount.HasValue
        || _minimumDelayMilliseconds.HasValue
        || _maximumJitterMilliseconds.HasValue;
}
