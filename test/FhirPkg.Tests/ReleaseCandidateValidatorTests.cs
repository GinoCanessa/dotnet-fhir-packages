// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class ReleaseCandidateValidatorTests
{
    [Fact]
    public void Validate_AcceptsSynchronizedSdkAndCliArtifacts()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            IReleaseCandidateValidator validator =
                new ReleaseCandidateValidator();

            ReleaseCandidateValidationResult result = validator.Validate(
                candidateDirectory,
                ReleaseValidationFixture.Version,
                ReleaseValidationFixture.Tag,
                ReleaseValidationFixture.RepositoryCommit);

            string sdkPackagePath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg");
            string sdkSymbolsPath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.snupkg");
            string sdkManifestPath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.sha512");
            string cliPackagePath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-cli.{ReleaseValidationFixture.Version}.nupkg");
            string cliSymbolsPath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-cli.{ReleaseValidationFixture.Version}.snupkg");
            string cliManifestPath = Path.Combine(
                candidateDirectory,
                $"fhir-pkg-cli.{ReleaseValidationFixture.Version}.sha512");

            result.SdkPackagePath.ShouldBe(sdkPackagePath);
            result.SdkSymbolsPath.ShouldBe(sdkSymbolsPath);
            result.SdkManifestPath.ShouldBe(sdkManifestPath);
            result.SdkSha256.ShouldBe(GetFileSha256(sdkPackagePath));
            result.CliPackagePath.ShouldBe(cliPackagePath);
            result.CliSymbolsPath.ShouldBe(cliSymbolsPath);
            result.CliManifestPath.ShouldBe(cliManifestPath);
            result.CliSha256.ShouldBe(GetFileSha256(cliPackagePath));
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_AcceptsCaseInsensitiveMetadataPropertyNames()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            string metadataPath = Path.Combine(
                candidateDirectory,
                "release-metadata.json");
            string metadata = File.ReadAllText(metadataPath);
            string[] propertyNames =
            [
                "version",
                "tag",
                "repositoryCommit",
                "feed",
                "packages",
                "packageId",
                "packageFile",
                "symbolsFile",
                "packageSha256",
                "symbolsSha256",
                "packageSha512",
                "symbolsSha512",
            ];
            foreach (string propertyName in propertyNames)
            {
                string pascalCaseName =
                    char.ToUpperInvariant(propertyName[0]) +
                    propertyName[1..];
                metadata = metadata.Replace(
                    $"\"{propertyName}\"",
                    $"\"{pascalCaseName}\"",
                    StringComparison.Ordinal);
            }

            File.WriteAllText(metadataPath, metadata);
            IReleaseCandidateValidator validator =
                new ReleaseCandidateValidator();

            ReleaseCandidateValidationResult result = validator.Validate(
                candidateDirectory,
                ReleaseValidationFixture.Version,
                ReleaseValidationFixture.Tag,
                ReleaseValidationFixture.RepositoryCommit);

            result.SdkPackagePath.ShouldEndWith(".nupkg");
            result.CliPackagePath.ShouldEndWith(".nupkg");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsCliSdkAssemblyMismatch()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate(
            mismatchCliAssembly: true);
        try
        {
            IReleaseCandidateValidator validator =
                new ReleaseCandidateValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.Tag,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                "The CLI embedded SDK assembly for 'net9.0' does not match the SDK package.");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsMissingOrUnexpectedArtifact()
    {
        string missingDirectory = ReleaseValidationFixture.CreateCandidate();
        string unexpectedDirectory = ReleaseValidationFixture.CreateCandidate();
        try
        {
            File.Delete(
                Path.Combine(
                    missingDirectory,
                    $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.sha512"));

            IReleaseCandidateValidator validator =
                new ReleaseCandidateValidator();
            ReleaseValidationException missingException =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        missingDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.Tag,
                        ReleaseValidationFixture.RepositoryCommit));

            missingException.Message.ShouldContain(
                "Release candidate inventory must contain exactly:");
            missingException.Message.ShouldContain(
                $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.sha512");

            File.WriteAllText(
                Path.Combine(unexpectedDirectory, "unexpected.txt"),
                "unexpected");

            ReleaseValidationException unexpectedException =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        unexpectedDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.Tag,
                        ReleaseValidationFixture.RepositoryCommit));

            unexpectedException.Message.ShouldContain(
                "Release candidate inventory must contain exactly:");
            unexpectedException.Message.ShouldContain("unexpected.txt");
        }
        finally
        {
            Directory.Delete(missingDirectory, recursive: true);
            Directory.Delete(unexpectedDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsInvalidNestedPackageMetadata()
    {
        string candidateDirectory = ReleaseValidationFixture.CreateCandidate(
            invalidSdkPackageMetadata: true);
        try
        {
            IReleaseCandidateValidator validator =
                new ReleaseCandidateValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        candidateDirectory,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.Tag,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                "The package repository metadata does not match the release commit.");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    private static string GetFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}
