// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

// The content of a release-scoped approval of exact replacement bytes for
// immutable corpus artifacts. Where an approval file came from (the commit
// that merged it) is checked by the release tool, not here.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureApproval
{
    internal const int SupportedSchemaVersion = 1;

    private const string WildcardCharacters = "*?[]";

    private static readonly Regex s_releaseTagPattern = new(
        "^v[0-9]+\\.[0-9]+\\.[0-9]+\\z",
        RegexOptions.CultureInvariant);

    public required int SchemaVersion { get; init; }

    public required string Release { get; init; }

    public required string ApprovedBy { get; init; }

    public required string Reason { get; init; }

    public required IReadOnlyList<FixtureApprovalReplacement> Replacements { get; init; }

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    internal static FixtureApproval Parse(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(Utf8ByteOrderMark))
            content = content[Utf8ByteOrderMark.Length..];

        FixtureApproval? approval;
        try
        {
            approval = JsonSerializer.Deserialize<FixtureApproval>(
                content,
                QualificationJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"The approval is not a valid schema version {SupportedSchemaVersion} approval: {exception.Message}");
        }

        return approval
            ?? throw Invalid("The approval is not a JSON object.");
    }

    // A bundle records each applied replacement on its own, so verification
    // re-checks each one as a single-replacement approval.
    internal static FixtureApproval FromBundleApproval(
        FixtureBundleApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new FixtureApproval
        {
            SchemaVersion = SupportedSchemaVersion,
            Release = approval.Release,
            ApprovedBy = approval.ApprovedBy,
            Reason = approval.Reason,
            Replacements =
            [
                new FixtureApprovalReplacement
                {
                    Artifact = approval.Artifact,
                    PinnedSha256 = approval.PinnedSha256,
                    ApprovedSha256 = approval.ApprovedSha256,
                    ObservedLastModified = approval.ObservedLastModified
                }
            ]
        };
    }

    internal IReadOnlyList<FixtureBundleApproval> ToBundleApprovals(
        string approvalCommit)
    {
        ArgumentNullException.ThrowIfNull(approvalCommit);
        return Replacements
            .OrderBy(
                replacement => replacement.Artifact,
                StringComparer.Ordinal)
            .Select(replacement => new FixtureBundleApproval
            {
                Artifact = replacement.Artifact,
                PinnedSha256 = replacement.PinnedSha256,
                ApprovedSha256 = replacement.ApprovedSha256,
                Release = Release,
                ApprovedBy = ApprovedBy,
                Reason = Reason,
                ApprovalCommit = approvalCommit,
                ObservedLastModified = replacement.ObservedLastModified
            })
            .ToArray();
    }

    internal void ValidateStructure()
    {
        if (SchemaVersion != SupportedSchemaVersion)
        {
            throw Invalid(
                $"The approval declares schema version {SchemaVersion}; only version {SupportedSchemaVersion} is supported.");
        }

        if (Release is null || !s_releaseTagPattern.IsMatch(Release))
        {
            throw Invalid(
                $"The approval release '{Release}' is not a release tag of the form v<major>.<minor>.<patch>.");
        }

        if (string.IsNullOrWhiteSpace(ApprovedBy))
            throw Invalid("The approval names no approver.");

        if (string.IsNullOrWhiteSpace(Reason))
            throw Invalid("The approval gives no reason.");

        if (Replacements is null || Replacements.Count == 0)
            throw Invalid("The approval lists no replacements.");

        HashSet<string> artifacts = new(StringComparer.Ordinal);
        foreach (FixtureApprovalReplacement? replacement in Replacements)
        {
            if (replacement is null)
                throw Invalid("The approval lists an empty replacement.");

            ValidateReplacement(replacement);
            if (!artifacts.Add(replacement.Artifact))
            {
                throw Invalid(
                    $"The approval lists artifact '{replacement.Artifact}' more than once.");
            }
        }
    }

    internal void ValidateAgainst(
        QualificationCorpus corpus,
        string releaseTag)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(releaseTag);
        if (!string.Equals(
                Release,
                releaseTag,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"The approval is for release {Release}, not release {releaseTag}.");
        }

        if (Replacements is null || Replacements.Count == 0)
            throw Invalid("The approval lists no replacements.");

        foreach (FixtureApprovalReplacement? replacement in Replacements)
        {
            if (replacement is null)
                throw Invalid("The approval lists an empty replacement.");

            ImmutableArtifactDefinition? definition =
                corpus.ImmutableArtifacts.FirstOrDefault(
                    artifact => string.Equals(
                        artifact.Id,
                        replacement.Artifact,
                        StringComparison.Ordinal));
            if (definition is null)
            {
                throw Invalid(
                    IsMutableArtifact(corpus, replacement.Artifact)
                        ? $"Approval artifact '{replacement.Artifact}' is a mutable corpus artifact; approvals name immutable artifacts only."
                        : $"Approval artifact '{replacement.Artifact}' is not an immutable corpus artifact.");
            }

            if (!string.Equals(
                    replacement.PinnedSha256,
                    definition.Sha256,
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Approval for '{replacement.Artifact}' records pinned SHA-256 {replacement.PinnedSha256}, not the corpus pin {definition.Sha256}.");
            }
        }
    }

    private static void ValidateReplacement(
        FixtureApprovalReplacement replacement)
    {
        // Deserialization does not enforce non-nullable annotations.
        string? artifact = replacement.Artifact;
        string[] parts = artifact?.Split('#') ?? [];
        if (parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw Invalid(
                $"Approval artifact '{artifact}' does not name one exact name#version.");
        }

        if (artifact!.AsSpan().IndexOfAny(WildcardCharacters) >= 0)
        {
            throw Invalid(
                $"Approval artifact '{artifact}' contains one of {WildcardCharacters}; approvals name exact artifacts only.");
        }

        if (!FixtureBundle.IsSha256(replacement.PinnedSha256))
        {
            throw Invalid(
                $"Approval for '{artifact}' records a pinned SHA-256 that is not 64 lowercase hexadecimal characters.");
        }

        if (!FixtureBundle.IsSha256(replacement.ApprovedSha256))
        {
            throw Invalid(
                $"Approval for '{artifact}' records an approved SHA-256 that is not 64 lowercase hexadecimal characters.");
        }

        if (string.Equals(
                replacement.PinnedSha256,
                replacement.ApprovedSha256,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"Approval for '{artifact}' approves its pinned bytes; the approved SHA-256 must differ from the pin.");
        }
    }

    private static bool IsMutableArtifact(
        QualificationCorpus corpus,
        string artifact) =>
        corpus.MutableArtifacts.Any(
            definition => string.Equals(
                    definition.Id,
                    artifact,
                    StringComparison.Ordinal)
                || string.Equals(
                    $"{definition.Name}#{definition.Selector}",
                    artifact,
                    StringComparison.Ordinal));

    private static FixtureBundleException Invalid(string message) =>
        new(QualificationFailureCodes.ApprovalInvalid, message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureApprovalReplacement
{
    public required string Artifact { get; init; }

    public required string PinnedSha256 { get; init; }

    public required string ApprovedSha256 { get; init; }

    public DateTimeOffset? ObservedLastModified { get; init; }
}
