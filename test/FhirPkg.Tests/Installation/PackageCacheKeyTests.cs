// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using FhirPkg.Installation;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Installation;

public class PackageCacheKeyTests
{
    [Fact]
    public void UnscopedReference_UsesOneCanonicalSegment()
    {
        PackageReference displayReference = new PackageReference("Example.Package", "1.0.0");

        PackageCacheKey key = PackageCacheKey.Create(displayReference);

        key.DisplayReference.ShouldBe(displayReference);
        key.CanonicalReference.Name.ShouldBe("example.package");
        key.RelativePath.ShouldBe("example.package#1.0.0");
        key.MetadataKey.ShouldBe("example.package#1.0.0");
        key.LockHash.Length.ShouldBe(64);
    }

    [Fact]
    public void ScopedReference_UsesScopeAndPackageSegments()
    {
        PackageCacheKey key = PackageCacheKey.Create(
            PackageReference.Parse("@Example/Package@1.0.0"));

        key.RelativePath.ShouldBe(Path.Combine("@example", "package#1.0.0"));
        key.MetadataKey.ShouldBe("@example/package#1.0.0");
    }

    [Fact]
    public void PackageNames_AreCaseInsensitiveCanonicalIdentities()
    {
        PackageCacheKey upper = PackageCacheKey.Create(
            new PackageReference("Example.Package", "1.0.0"));
        PackageCacheKey lower = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0"));

        upper.ShouldBe(lower);
        upper.LockHash.ShouldBe(lower.LockHash);
    }

    [Fact]
    public void CurrentBranch_EncodesUnsafeCharactersAndRoundTrips()
    {
        PackageCacheKey key = PackageCacheKey.Create(
            new PackageReference("example.package", "current$feature/fix #1"));

        key.RelativePath.ShouldBe("example.package#current%24feature%2ffix%20%231");
        PackageCacheKey.TryParseRelativePath(key.RelativePath, out PackageCacheKey? parsed)
            .ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed!.CanonicalReference.Version.ShouldBe("current$feature/fix #1");
        parsed.ShouldBe(key);
    }

    [Fact]
    public void CaseDistinctVersions_HaveDistinctPortableIdentities()
    {
        PackageCacheKey upper = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0-Alpha"));
        PackageCacheKey lower = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0-alpha"));

        upper.ShouldNotBe(lower);
        StringComparer.OrdinalIgnoreCase.Equals(
            upper.RelativePath,
            lower.RelativePath).ShouldBeFalse();
        StringComparer.OrdinalIgnoreCase.Equals(
            upper.MetadataKey,
            lower.MetadataKey).ShouldBeFalse();
        upper.TransactionKey.ShouldNotBe(lower.TransactionKey);
        upper.LockHash.ShouldNotBe(lower.LockHash);
    }

    [Fact]
    public void CaseDistinctBranches_HaveDistinctPortableIdentities()
    {
        PackageCacheKey upper = PackageCacheKey.Create(
            new PackageReference("example.package", "current$Feature"));
        PackageCacheKey lower = PackageCacheKey.Create(
            new PackageReference("example.package", "current$feature"));

        StringComparer.OrdinalIgnoreCase.Equals(
            upper.CanonicalIdentity,
            lower.CanonicalIdentity).ShouldBeFalse();
        upper.ShouldNotBe(lower);
    }

    [Fact]
    public void UnsafeCharacters_AreReversiblyEncodedWithoutRawEquals()
    {
        string version = "A=a%/B";
        PackageCacheKey key = PackageCacheKey.Create(
            new PackageReference("example=package", version));

        key.CanonicalIdentity.ShouldBe(
            "example%3dpackage#%41%3da%25%2f%42");
        key.MetadataKey.ShouldNotContain("=");
        key.MetadataKey.ShouldBe(key.CanonicalIdentity);
        key.TransactionKey.ShouldBe(key.CanonicalIdentity);
        string expectedLockHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key.CanonicalIdentity)))
            .ToLowerInvariant();
        key.LockHash.ShouldBe(expectedLockHash);
        PackageCacheKey.TryParseRelativePath(
            key.RelativePath,
            out PackageCacheKey? parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed!.CanonicalReference.Version.ShouldBe(version);
        parsed.DisplayReference.Name.ShouldBe("example=package");
    }

    [Theory]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0\r")]
    [InlineData("1.0.0\u0000")]
    [InlineData("current$feature\u007f")]
    public void ControlCharacters_AreRejected(string version)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageCacheKey.Create(
                new PackageReference("example.package", version)));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Fact]
    public void LoneSurrogate_IsRejectedInsteadOfCollidingWithReplacementCharacter()
    {
        PackageCacheKey replacementCharacter = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0-\uFFFD"));

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageCacheKey.Create(
                new PackageReference("example.package", "1.0.0-\uD800")));

        replacementCharacter.CanonicalIdentity.ShouldContain("%ef%bf%bd");
        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [InlineData("../escape", "1.0.0")]
    [InlineData("CON", "1.0.0")]
    [InlineData("example/package", "1.0.0")]
    [InlineData("example.package", "latest")]
    [InlineData("example.package", "1.0.x")]
    [InlineData("example.package", "current$../escape")]
    [InlineData("example.package", "current$")]
    public void UnsafeOrUnsupportedIdentity_IsRejected(string name, string version)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageCacheKey.Create(new PackageReference(name, version)));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Fact]
    public void PackageDirectory_RemainsBeneathCacheRoot()
    {
        string cacheRoot = Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}");
        PackageCacheKey key = PackageCacheKey.Create(
            new PackageReference("@scope/package", "current$feature/fix", "@scope"));

        string packageDirectory = key.GetPackageDirectoryPath(cacheRoot);
        string relative = Path.GetRelativePath(Path.GetFullPath(cacheRoot), packageDirectory);

        Path.IsPathRooted(relative).ShouldBeFalse();
        relative.StartsWith("..", StringComparison.Ordinal).ShouldBeFalse();
        relative.ShouldBe(Path.Combine(
            "@scope",
            "package#current%24feature%2ffix"));
    }
}
