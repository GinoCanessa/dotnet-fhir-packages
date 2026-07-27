// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Release.Validation;

internal sealed record ReleaseArtifactValidationResult(
    string Path,
    string Sha256);

internal sealed record ReleaseCandidateValidationResult(
    string SdkPackagePath,
    string SdkSymbolsPath,
    string SdkManifestPath,
    string SdkSha256,
    string CliPackagePath,
    string CliSymbolsPath,
    string CliManifestPath,
    string CliSha256);

internal sealed record ReleaseInputValidationResult(
    string Commit,
    string MainCommit);

internal enum ReleasePackagePublicationState
{
    Missing,
    Verified,
}

internal sealed record ReleasePublicationStateResult(
    ReleasePackagePublicationState CliState,
    ReleasePackagePublicationState SdkState);

internal sealed record PublishedPackageValidationResult(
    string PublishedSha256);
