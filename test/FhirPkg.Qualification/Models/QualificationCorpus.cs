// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirPkg.Qualification.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record QualificationCorpus
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public required int SchemaVersion { get; init; }

    public required IReadOnlyList<ImmutableArtifactDefinition>
        ImmutableArtifacts { get; init; }

    public required IReadOnlyList<MutableArtifactDefinition>
        MutableArtifacts { get; init; }

    public required LocalFixtureDefinition LocalFixture { get; init; }

    public required DownstreamClosureDefinition DownstreamClosure { get; init; }

    internal static async Task<QualificationCorpus> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        QualificationCorpus corpus =
            await JsonSerializer.DeserializeAsync<QualificationCorpus>(
                    stream,
                    QualificationJson.SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw new JsonException(
                "The qualification corpus did not contain an object.");
        corpus.Validate();
        return corpus;
    }

    private void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported qualification corpus schema version {SchemaVersion}.");
        }

        if (!string.Equals(
                Schema,
                "Schemas/qualification-corpus.schema.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The qualification corpus must reference its pinned schema.");
        }

        if (ImmutableArtifacts.Count != 15)
        {
            throw new InvalidDataException(
                "The qualification corpus must contain exactly 15 immutable artifacts.");
        }

        HashSet<string> immutableIds = new(StringComparer.Ordinal);
        foreach (ImmutableArtifactDefinition artifact in ImmutableArtifacts)
        {
            artifact.Validate();
            if (!immutableIds.Add(artifact.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate immutable artifact '{artifact.Id}'.");
            }
        }

        HashSet<string> mutableIds = new(StringComparer.Ordinal);
        foreach (MutableArtifactDefinition artifact in MutableArtifacts)
        {
            artifact.Validate();
            if (!mutableIds.Add(artifact.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate mutable artifact '{artifact.Id}'.");
            }
        }

        if (!mutableIds.SetEquals(
            ["r6-current", "r6-current-master"]))
        {
            throw new InvalidDataException(
                "The qualification corpus must contain current and current$master R6 artifacts.");
        }

        LocalFixture.Validate();
        DownstreamClosure.Validate(immutableIds);
    }

    internal ImmutableArtifactDefinition GetImmutable(string id) =>
        ImmutableArtifacts.Single(
            artifact => string.Equals(
                artifact.Id,
                id,
                StringComparison.Ordinal));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImmutableArtifactDefinition
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Uri { get; init; }

    public required string Sha256 { get; init; }

    [JsonIgnore]
    public string Id => $"{Name}#{Version}";

    internal void Validate()
    {
        ValidatePackagePart(Name, nameof(Name));
        ValidatePackagePart(Version, nameof(Version));
        ValidateHttpsUri(Uri, Id);
        ValidateSha256(Sha256, Id);
        string expectedUri =
            $"https://packages2.fhir.org/packages/{Name}/{Version}";
        if (!string.Equals(
                Uri,
                expectedUri,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Immutable artifact '{Id}' does not use its pinned packages2.fhir.org URI.");
        }
    }

    internal static void ValidateSha256(
        string value,
        string description)
    {
        if (value.Length != 64
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"Artifact '{description}' has an invalid SHA-256 pin.");
        }
    }

    internal static void ValidateHttpsUri(
        string value,
        string description)
    {
        if (!System.Uri.TryCreate(
                value,
                UriKind.Absolute,
                out System.Uri? uri)
            || !string.Equals(
                uri.Scheme,
                System.Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Artifact '{description}' must use an absolute HTTPS URI.");
        }
    }

    internal static void ValidatePackagePart(
        string value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Qualification corpus {description} must not be empty.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MutableArtifactDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Selector { get; init; }

    public required string Uri { get; init; }

    internal void Validate()
    {
        ImmutableArtifactDefinition.ValidatePackagePart(Id, nameof(Id));
        ImmutableArtifactDefinition.ValidatePackagePart(Name, nameof(Name));
        ImmutableArtifactDefinition.ValidatePackagePart(
            Selector,
            nameof(Selector));
        ImmutableArtifactDefinition.ValidateHttpsUri(Uri, Id);
        if (!string.Equals(Name, "hl7.fhir.r6.core", StringComparison.Ordinal)
            || Selector is not ("current" or "current$master"))
        {
            throw new InvalidDataException(
                $"Mutable artifact '{Id}' has an unexpected identity.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LocalFixtureDefinition
{
    public required string Name { get; init; }

    public required string Selector { get; init; }

    public required string ManifestVersion { get; init; }

    public required string Sha256 { get; init; }

    [JsonIgnore]
    public string Id => $"{Name}#{Selector}";

    internal void Validate()
    {
        ImmutableArtifactDefinition.ValidatePackagePart(Name, nameof(Name));
        ImmutableArtifactDefinition.ValidatePackagePart(
            ManifestVersion,
            nameof(ManifestVersion));
        if (!string.Equals(Selector, "dev", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The local qualification fixture selector must be dev.");
        }

        ImmutableArtifactDefinition.ValidateSha256(Sha256, Id);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DownstreamClosureDefinition
{
    public required IReadOnlyList<string> Roots { get; init; }

    public required IReadOnlyList<string> ResolverMembers { get; init; }

    public required IReadOnlyList<string> RequiredMembers { get; init; }

    internal void Validate(IReadOnlySet<string> immutableIds)
    {
        string[] expectedRoots =
        [
            "hl7.fhir.us.core#6.0.0",
            "hl7.fhir.uv.ips#1.1.0"
        ];
        if (!Roots.Order(StringComparer.Ordinal).SequenceEqual(
                expectedRoots.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The downstream closure roots do not match the pinned qualification roots.");
        }

        if (RequiredMembers.Count != 12
            || RequiredMembers.Distinct(StringComparer.Ordinal).Count() != 12)
        {
            throw new InvalidDataException(
                "The downstream closure must contain 12 unique researched R4 members.");
        }

        if (ResolverMembers.Count != 12
            || ResolverMembers.Distinct(
                StringComparer.Ordinal).Count() != 12)
        {
            throw new InvalidDataException(
                "The resolver closure must contain 12 unique transitive members.");
        }

        if (!ResolverMembers.Order(StringComparer.Ordinal)
            .SequenceEqual(
                RequiredMembers.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The resolver and direct downstream closure members must match.");
        }

        foreach (string id in ResolverMembers)
        {
            if (!immutableIds.Contains(id))
            {
                throw new InvalidDataException(
                    $"Resolver closure member '{id}' is not pinned as an immutable artifact.");
            }
        }

        foreach (string id in RequiredMembers)
        {
            if (!immutableIds.Contains(id))
            {
                throw new InvalidDataException(
                    $"Downstream closure member '{id}' is not pinned as an immutable artifact.");
            }
        }
    }
}
