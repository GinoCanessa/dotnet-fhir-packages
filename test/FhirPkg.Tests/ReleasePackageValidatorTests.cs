// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleasePackageValidatorTests
{
    [Theory]
    [InlineData("fhir-pkg-lib")]
    [InlineData("fhir-pkg-cli")]
    public void Validate_AcceptsSdkAndCliPackageShapes(
        string packageId)
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string packagePath = Path.Combine(
                workingDirectory,
                $"{packageId}.{ReleaseValidationFixture.Version}.nupkg");
            if (string.Equals(
                    packageId,
                    "fhir-pkg-lib",
                    StringComparison.Ordinal))
            {
                ReleaseValidationFixture.CreateSdkPackage(packagePath);
            }
            else
            {
                ReleaseValidationFixture.CreateCliPackage(packagePath);
            }

            string relativePath = Path.GetRelativePath(
                Environment.CurrentDirectory,
                packagePath);
            IReleasePackageValidator validator =
                new ReleasePackageValidator();

            ReleaseArtifactValidationResult result =
                validator.Validate(
                    packageId,
                    relativePath,
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit);

            result.Path.ShouldBe(Path.GetFullPath(relativePath));
            result.Sha256.ShouldBe(GetSha256(packagePath));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsSdkPackageWithInvalidMetadata()
    {
        string candidateDirectory =
            ReleaseValidationFixture.CreateCandidate(
                invalidSdkPackageMetadata: true);
        try
        {
            IReleasePackageValidator validator =
                new ReleasePackageValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        "fhir-pkg-lib",
                        Path.Combine(
                            candidateDirectory,
                            $"fhir-pkg-lib.{ReleaseValidationFixture.Version}.nupkg"),
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                "The package repository metadata does not match the release commit.");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsCliPackageWhenToolSettingsPathCaseDoesNotMatch()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string packagePath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-cli.{ReleaseValidationFixture.Version}.nupkg");
            ReleaseValidationFixture.CreateCliPackage(
                packagePath,
                settingsEntryName: "dotnettoolsettings.xml");
            IReleasePackageValidator validator =
                new ReleasePackageValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        "fhir-pkg-cli",
                        packagePath,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                "Expected one tool settings file for 'net8.0'.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
