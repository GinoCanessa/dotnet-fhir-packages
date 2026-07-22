// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirPkg.Qualification.Models;

internal sealed record QualificationReport
{
    public int SchemaVersion { get; init; } = 1;

    public required string Mode { get; init; }

    public required bool ValidationOnly { get; init; }

    public string? RequestedPackageVersion { get; init; }

    public string? PackageVersion { get; init; }

    public required string FhirPkgAssemblyVersion { get; init; }

    public string? FhirPkgInformationalVersion { get; init; }

    public required string CorpusSha256 { get; init; }

    public required string CorpusHashAlgorithm { get; init; }

    public required string Framework { get; init; }

    public required string OperatingSystem { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset CompletedUtc { get; set; }

    public required bool Success { get; set; }

    public required List<QualificationArtifactResult> Artifacts { get; init; }

    public required List<QualificationCaseResult> Cases { get; init; }

    public required QualificationSummary Summary { get; set; }
}

internal sealed record QualificationArtifactResult
{
    public required string Kind { get; init; }

    public required string Id { get; init; }

    public required string SourceUri { get; init; }

    public string? FinalUri { get; set; }

    public string? ExpectedSha256 { get; init; }

    public string? ActualSha256 { get; set; }

    public bool? HashMatch { get; set; }

    public long? CompressedBytes { get; set; }

    public long? ExpandedBytes { get; set; }

    public long? LargestEntryBytes { get; set; }

    public int? EntryCount { get; set; }

    public string? PublicationUtc { get; set; }

    public string? ManifestName { get; set; }

    public string? ManifestVersion { get; set; }

    public string? ManifestDate { get; set; }

    public required bool Success { get; set; }

    public QualificationFailure? Failure { get; set; }
}

internal sealed record QualificationCaseResult
{
    public required string Id { get; init; }

    public required bool Success { get; set; }

    public required long DurationMilliseconds { get; set; }

    public required List<QualificationDetail> Details { get; init; }

    public required List<QualificationFailure> Failures { get; init; }
}

internal sealed record QualificationDetail
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

internal sealed record QualificationFailure
{
    public string? Code { get; init; }

    public string? Stage { get; init; }

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }
}

internal sealed record QualificationSummary
{
    public required int ArtifactCount { get; init; }

    public required int ArtifactFailures { get; init; }

    public required int CaseCount { get; init; }

    public required int CaseFailures { get; init; }
}

internal static class QualificationJson
{
    internal static JsonSerializerOptions SerializerOptions { get; } =
        new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
}
