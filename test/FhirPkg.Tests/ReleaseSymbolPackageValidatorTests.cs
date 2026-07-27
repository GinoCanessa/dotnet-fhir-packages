// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using FhirPkg.Release.Validation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseSymbolPackageValidatorTests
{
    [Theory]
    [InlineData("fhir-pkg-lib")]
    [InlineData("fhir-pkg-cli")]
    public void Validate_AcceptsSdkAndCliSymbolPackageShapes(
        string packageId)
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string symbolsPath = Path.Combine(
                workingDirectory,
                $"{packageId}.{ReleaseValidationFixture.Version}.snupkg");
            if (string.Equals(
                    packageId,
                    "fhir-pkg-lib",
                    StringComparison.Ordinal))
            {
                ReleaseValidationFixture.CreateSdkSymbolsPackage(
                    symbolsPath);
            }
            else
            {
                ReleaseValidationFixture.CreateCliSymbolsPackage(
                    symbolsPath);
            }

            string relativePath = Path.GetRelativePath(
                Environment.CurrentDirectory,
                symbolsPath);
            IReleaseSymbolPackageValidator validator =
                new ReleaseSymbolPackageValidator();

            ReleaseArtifactValidationResult result =
                validator.Validate(
                    packageId,
                    relativePath,
                    ReleaseValidationFixture.Version,
                    ReleaseValidationFixture.RepositoryCommit);

            result.Path.ShouldBe(Path.GetFullPath(relativePath));
            result.Sha256.ShouldBe(GetSha256(symbolsPath));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsSymbolsPackageWithUnexpectedFileNameCase()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string symbolsPath = Path.Combine(
                workingDirectory,
                $"FHIR-PKG-LIB.{ReleaseValidationFixture.Version}.snupkg");
            ReleaseValidationFixture.CreateSdkSymbolsPackage(
                symbolsPath);
            IReleaseSymbolPackageValidator validator =
                new ReleaseSymbolPackageValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        "fhir-pkg-lib",
                        symbolsPath,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                $"Expected 'fhir-pkg-lib.{ReleaseValidationFixture.Version}.snupkg', found 'FHIR-PKG-LIB.{ReleaseValidationFixture.Version}.snupkg'.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsSymbolsPackageWithWrongType()
    {
        string workingDirectory =
            ReleaseValidationFixture.CreateArtifactsDirectory();
        try
        {
            string symbolsPath = Path.Combine(
                workingDirectory,
                $"fhir-pkg-cli.{ReleaseValidationFixture.Version}.snupkg");
            ReleaseValidationFixture.CreateCliSymbolsPackage(
                symbolsPath,
                packageType: "DotnetTool");
            IReleaseSymbolPackageValidator validator =
                new ReleaseSymbolPackageValidator();

            ReleaseValidationException exception =
                Should.Throw<ReleaseValidationException>(
                    () => validator.Validate(
                        "fhir-pkg-cli",
                        symbolsPath,
                        ReleaseValidationFixture.Version,
                        ReleaseValidationFixture.RepositoryCommit));

            exception.Message.ShouldBe(
                "The symbols package type must be SymbolsPackage.");
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
