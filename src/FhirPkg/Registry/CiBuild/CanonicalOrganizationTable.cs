// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// Maps FHIR package-identifier prefixes to the organization that canonically
/// publishes them on the CI build server.
/// </summary>
/// <remarks>
/// <para>
/// This is tier 1 of canonical-repository selection. It exists because a fork
/// check alone picks the wrong repository for several real package families —
/// for example <c>ch.fhir.ig.ch-emr</c>, where the canonical <c>hl7ch</c>
/// repository is not the oldest and the alternative is not a GitHub fork.
/// </para>
/// <para>
/// The rules are ordered longest-prefix-first so a specific rule is never
/// shadowed by a more general one; that ordering is enforced by test. The table
/// tracks upstream governance and is expected to need occasional maintenance;
/// tiers 2 and 3 cover every package it does not name.
/// </para>
/// </remarks>
internal static class CanonicalOrganizationTable
{
    private static readonly (string Prefix, string Organization)[] s_rules =
    [
        ("org.sql-on-fhir.", "FHIR"),
        ("smart.who.int.", "WorldHealthOrganization"),
        ("hl7.fhir.au.", "hl7au"),
        ("hl7.fhir.be.", "hl7-be"),
        ("hl7.fhir.eu.", "hl7-eu"),
        ("ch.fhir.ig.", "hl7ch"),
        ("zw.fhir.ig.", "mohcc"),
        ("openehr.", "FHIR"),
        ("et.fhir.", "MoH-Ethiopia"),
        ("hl7se.", "HL7Sweden"),
        ("hl7.", "HL7"),
    ];

    /// <summary>
    /// Gets the prefix rules in evaluation order, longest prefix first.
    /// </summary>
    internal static IReadOnlyList<(string Prefix, string Organization)> Rules => s_rules;

    /// <summary>
    /// Attempts to find the canonical publishing organization for a package identifier.
    /// </summary>
    /// <param name="packageId">The FHIR package identifier.</param>
    /// <param name="organization">The canonical organization when a rule matches.</param>
    /// <returns><see langword="true"/> when a rule matched; otherwise <see langword="false"/>.</returns>
    public static bool TryGetOrganization(string packageId, out string organization)
    {
        organization = string.Empty;

        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        foreach ((string prefix, string candidateOrganization) in s_rules)
        {
            if (packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                organization = candidateOrganization;
                return true;
            }
        }

        return false;
    }
}
