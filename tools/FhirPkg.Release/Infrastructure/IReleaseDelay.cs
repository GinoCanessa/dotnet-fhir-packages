// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Release.Infrastructure;

internal interface IReleaseDelay
{
    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken);
}
