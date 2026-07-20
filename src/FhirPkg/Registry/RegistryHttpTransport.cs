// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Registry;

/// <summary>
/// Describes the HTTP transport guarantees used by registry clients.
/// </summary>
/// <remarks>
/// A redirect-controlled transport requires an <see cref="HttpClient"/> whose
/// handler does not automatically follow redirects. Registry clients then
/// rebuild each redirected request so sensitive headers can be scoped to the
/// actual destination origin.
/// </remarks>
public sealed class RegistryHttpTransport
{
    private RegistryHttpTransport(
        HttpClient httpClient,
        TimeSpan timeout,
        int maxRedirects,
        bool redirectsControlled)
    {
        HttpClient = httpClient;
        Timeout = timeout;
        MaxRedirects = maxRedirects;
        RedirectsControlled = redirectsControlled;
    }

    /// <summary>Gets the HTTP client used for registry requests.</summary>
    public HttpClient HttpClient { get; }

    /// <summary>Gets the total deadline for a registry request and its response body.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the maximum number of redirects followed by registry GET and HEAD requests.</summary>
    public int MaxRedirects { get; }

    internal bool RedirectsControlled { get; }

    /// <summary>
    /// Creates a transport for an HTTP client whose handler has automatic redirects disabled.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client to use. Its handler must have automatic redirects disabled.
    /// </param>
    /// <param name="timeout">The total request and response-body deadline.</param>
    /// <param name="maxRedirects">The maximum number of redirects to follow.</param>
    /// <returns>A redirect-controlled transport capability.</returns>
    public static RegistryHttpTransport CreateRedirectControlled(
        HttpClient httpClient,
        TimeSpan timeout,
        int maxRedirects)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateTimeout(timeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRedirects, 1);

        return new RegistryHttpTransport(
            httpClient,
            timeout,
            maxRedirects,
            redirectsControlled: true);
    }

    internal static RegistryHttpTransport CreateUnverified(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        TimeSpan timeout = httpClient.Timeout;
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
            ValidateTimeout(timeout);

        return new RegistryHttpTransport(
            httpClient,
            timeout,
            maxRedirects: 0,
            redirectsControlled: false);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan
            && (timeout <= TimeSpan.Zero
                || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The transport timeout must be infinite or a finite positive value no greater than Int32.MaxValue milliseconds.");
        }
    }
}
