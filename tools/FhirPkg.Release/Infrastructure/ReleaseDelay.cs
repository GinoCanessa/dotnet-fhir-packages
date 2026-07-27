// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Release.Infrastructure;

internal sealed class ReleaseDelay : IReleaseDelay
{
    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
