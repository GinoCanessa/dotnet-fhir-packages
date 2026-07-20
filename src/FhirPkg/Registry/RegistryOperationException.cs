// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;

namespace FhirPkg.Registry;

/// <summary>
/// Identifies a safe, public category for a failed registry attempt.
/// </summary>
public enum RegistryFailureCategory
{
    /// <summary>The registry could not be reached.</summary>
    Network,

    /// <summary>The registry operation exceeded its deadline.</summary>
    Timeout,

    /// <summary>The registry returned an unsuccessful HTTP response.</summary>
    HttpResponse,

    /// <summary>The registry returned data that could not be processed.</summary>
    InvalidResponse,

    /// <summary>The registry attempt failed for another reason.</summary>
    Unexpected,
}

/// <summary>
/// A sanitized snapshot of one failed registry attempt.
/// </summary>
/// <remarks>
/// This type intentionally retains only an origin, a broad failure category, and
/// a category-derived message. It never retains the original exception, response
/// content, endpoint credentials, or configured headers.
/// </remarks>
public sealed class RegistryAttemptFailure
{
    /// <summary>
    /// Initializes a sanitized registry-attempt failure.
    /// </summary>
    /// <param name="endpointUrl">
    /// The attempted endpoint URL. Only its origin is retained.
    /// </param>
    /// <param name="category">The safe public failure category.</param>
    public RegistryAttemptFailure(string? endpointUrl, RegistryFailureCategory category)
    {
        EndpointOrigin = SanitizeOrigin(endpointUrl);
        Category = category;
        Message = GetSafeMessage(category);
    }

    /// <summary>
    /// Gets the attempted endpoint origin without user information, path, query, or fragment.
    /// </summary>
    public string EndpointOrigin { get; }

    /// <summary>Gets the safe public failure category.</summary>
    public RegistryFailureCategory Category { get; }

    /// <summary>Gets a scrubbed, category-derived failure message.</summary>
    public string Message { get; }

    internal static RegistryAttemptFailure Capture(
        RegistryEndpoint endpoint,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exception);

        return new RegistryAttemptFailure(endpoint.Url, Categorize(exception));
    }

    private static RegistryFailureCategory Categorize(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return RegistryFailureCategory.Timeout;
        }

        if (exception is HttpRequestException httpRequestException)
        {
            return httpRequestException.StatusCode.HasValue
                ? RegistryFailureCategory.HttpResponse
                : RegistryFailureCategory.Network;
        }

        if (exception is JsonException or InvalidDataException or FormatException)
        {
            return RegistryFailureCategory.InvalidResponse;
        }

        return RegistryFailureCategory.Unexpected;
    }

    private static string GetSafeMessage(RegistryFailureCategory category) =>
        category switch
        {
            RegistryFailureCategory.Network => "The registry could not be reached.",
            RegistryFailureCategory.Timeout => "The registry operation timed out.",
            RegistryFailureCategory.HttpResponse =>
                "The registry returned an unsuccessful HTTP response.",
            RegistryFailureCategory.InvalidResponse =>
                "The registry returned an invalid response.",
            _ => "The registry attempt failed.",
        };

    internal static string SanitizeOrigin(string? endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpoint)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            return "unknown";
        }

        string host = endpoint.HostNameType == UriHostNameType.IPv6
            ? $"[{endpoint.IdnHost.Trim('[', ']')}]"
            : endpoint.IdnHost;
        string port = endpoint.IsDefaultPort ? string.Empty : $":{endpoint.Port}";
        return $"{endpoint.Scheme}://{host}{port}";
    }
}

/// <summary>
/// Represents a registry operation that could not produce an authoritative result.
/// </summary>
public sealed class RegistryOperationException : HttpRequestException
{
    /// <summary>
    /// Initializes a registry operation exception from sanitized attempt failures.
    /// </summary>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="packageId">The requested package identifier.</param>
    /// <param name="failures">Sanitized snapshots of failed registry attempts.</param>
    public RegistryOperationException(
        string operation,
        string packageId,
        IEnumerable<RegistryAttemptFailure> failures)
        : base(CreateMessage(failures, out IReadOnlyList<RegistryAttemptFailure> snapshots))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        Operation = operation;
        PackageId = packageId;
        Failures = snapshots;
    }

    /// <summary>Gets the registry operation that failed.</summary>
    public string Operation { get; }

    /// <summary>Gets the requested package identifier.</summary>
    public string PackageId { get; }

    /// <summary>Gets sanitized snapshots of the failed registry attempts.</summary>
    public IReadOnlyList<RegistryAttemptFailure> Failures { get; }

    private static string CreateMessage(
        IEnumerable<RegistryAttemptFailure> failures,
        out IReadOnlyList<RegistryAttemptFailure> snapshots)
    {
        ArgumentNullException.ThrowIfNull(failures);

        RegistryAttemptFailure[] captured = failures.ToArray();
        if (captured.Length == 0)
        {
            throw new ArgumentException(
                "At least one registry attempt failure is required.",
                nameof(failures));
        }

        snapshots = Array.AsReadOnly(captured);
        return captured.Length == 1
            ? "The registry operation failed after one attempt."
            : $"The registry operation failed after {captured.Length} attempts.";
    }
}
