// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using FhirPkg.Models;

namespace FhirPkg.Registry;

/// <summary>
/// Describes a package registry endpoint, including its URL, protocol type, and optional authentication.
/// </summary>
/// <remarks>
/// Use the well-known static properties (<see cref="FhirPrimary"/>, <see cref="FhirSecondary"/>, etc.)
/// for standard registries, or construct custom endpoints for private or enterprise registries.
/// </remarks>
public record RegistryEndpoint
{
    /// <summary>
    /// Gets the base URL of the registry (e.g., <c>https://packages.fhir.org/</c>).
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the registry protocol type, which determines how the client communicates with the endpoint.
    /// </summary>
    public required RegistryType Type { get; init; }

    /// <summary>
    /// Gets the optional value for the HTTP <c>Authorization</c> header
    /// (e.g., <c>Bearer &lt;token&gt;</c>).
    /// </summary>
    public string? AuthHeaderValue { get; init; }

    /// <summary>
    /// Gets optional custom HTTP headers sent with every request to this endpoint.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)>? CustomHeaders { get; init; }

    /// <summary>
    /// Gets the optional <c>User-Agent</c> header value.
    /// When <see langword="null"/>, a default user-agent is used.
    /// </summary>
    public string? UserAgent { get; init; }

    // ── Well-known endpoints ────────────────────────────────────────────

    /// <summary>
    /// The primary FHIR NPM registry at <c>https://packages.fhir.org/</c>.
    /// </summary>
    public static RegistryEndpoint FhirPrimary { get; } = new()
    {
        Url = "https://packages.fhir.org/",
        Type = RegistryType.FhirNpm,
    };

    /// <summary>
    /// The secondary FHIR NPM registry at <c>https://packages2.fhir.org/packages</c>.
    /// </summary>
    public static RegistryEndpoint FhirSecondary { get; } = new()
    {
        Url = "https://packages2.fhir.org/packages",
        Type = RegistryType.FhirNpm,
    };

    /// <summary>
    /// The FHIR CI build server at <c>https://build.fhir.org/</c>.
    /// </summary>
    public static RegistryEndpoint FhirCiBuild { get; } = new()
    {
        Url = "https://build.fhir.org/",
        Type = RegistryType.FhirCiBuild,
    };

    /// <summary>
    /// The HL7 FHIR website at <c>https://hl7.org/fhir/</c>, used as a fallback for core packages.
    /// </summary>
    public static RegistryEndpoint Hl7Website { get; } = new()
    {
        Url = "https://hl7.org/fhir/",
        Type = RegistryType.FhirHttp,
    };

    /// <summary>
    /// The public NPM registry at <c>https://registry.npmjs.org/</c>.
    /// </summary>
    public static RegistryEndpoint NpmPublic { get; } = new()
    {
        Url = "https://registry.npmjs.org/",
        Type = RegistryType.Npm,
    };

    // ── Default registry chains ─────────────────────────────────────────

    /// <summary>
    /// The default chain for resolving published packages: primary → secondary → HL7 website.
    /// </summary>
    public static IReadOnlyList<RegistryEndpoint> DefaultPublishedChain { get; } =
        [FhirPrimary, FhirSecondary, Hl7Website];

    /// <summary>
    /// The default chain that includes CI builds: primary → secondary → CI build → HL7 website.
    /// </summary>
    public static IReadOnlyList<RegistryEndpoint> DefaultFullChain { get; } =
        [FhirPrimary, FhirSecondary, FhirCiBuild, Hl7Website];
}
