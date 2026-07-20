// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Installation;

public class PackageIdentityValidatorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"identity-validator-{Guid.NewGuid():N}");

    public PackageIdentityValidatorTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ValidateExpectedAsync_ExactIdentity_ReturnsValidatedManifest()
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"example.package","version":"1.2.3"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "1.2.3"));

        PackageIdentityValidationResult result =
            await PackageIdentityValidator.ValidateExpectedAsync(
                manifestPath,
                expectation,
                "example.package#1.2.3",
                TestContext.Current.CancellationToken);

        result.Manifest.Name.ShouldBe("example.package");
        result.ManifestReference.ShouldBe(
            new PackageReference("example.package", "1.2.3"));
        result.CacheKey.DisplayReference.ShouldBe(expectation.Reference);
    }

    [Theory]
    [InlineData("other.package", "1.2.3")]
    [InlineData("example.package", "1.2.4")]
    public async Task ValidateExpectedAsync_ExactMismatch_ThrowsInvalidIdentity(
        string manifestName,
        string manifestVersion)
    {
        string manifestPath = await WriteManifestAsync(
            $$"""{"name":"{{manifestName}}","version":"{{manifestVersion}}"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "1.2.3"));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#1.2.3",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("dev")]
    [InlineData("current$feature/branch")]
    public async Task ValidateExpectedAsync_Alias_AllowsConcreteManifestVersion(
        string alias)
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"example.package","version":"9.8.7-build"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", alias));

        PackageIdentityValidationResult result =
            await PackageIdentityValidator.ValidateExpectedAsync(
                manifestPath,
                expectation,
                $"example.package#{alias}",
                TestContext.Current.CancellationToken);

        result.ManifestReference.Version.ShouldBe("9.8.7-build");
        result.CacheKey.DisplayReference.Version.ShouldBe(alias);
    }

    [Fact]
    public async Task ValidateExpectedAsync_AliasNameMismatch_ThrowsInvalidIdentity()
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"other.package","version":"1.0.0"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "current"));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#current",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Fact]
    public async Task ValidateExpectedAsync_PinnedAliasVersion_IsEnforced()
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"example.package","version":"2.0.1"}""");
        PackageIdentityExpectation expectation = new()
        {
            Kind = PackageIdentityExpectationKind.Alias,
            Reference =
                new PackageReference(
                    "example.package",
                    "current"),
            ExpectedManifestReference =
                new PackageReference(
                    "example.package",
                    "2.0.0"),
        };

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#current",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(
            PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("current")]
    [InlineData("dev")]
    [InlineData("1.2.x")]
    [InlineData("1.2")]
    [InlineData("*")]
    [InlineData("^1.2.0")]
    [InlineData(">=1.2.0")]
    [InlineData("1.2.0 - 2.0.0")]
    public async Task ValidateExpectedAsync_NonConcreteManifestVersion_ThrowsInvalidIdentity(
        string manifestVersion)
    {
        string manifestPath = await WriteManifestAsync(
            $$"""{"name":"example.package","version":"{{manifestVersion}}"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "current"));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#current",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.2.x")]
    [InlineData("1.2")]
    [InlineData("*")]
    [InlineData("^1.2.0")]
    [InlineData(">=1.2.0")]
    [InlineData("1.2.0 - 2.0.0")]
    public void CreateExpectation_DynamicDirectIdentity_ThrowsInvalidIdentity(
        string version)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", version)));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("^1.2.0")]
    [InlineData(">=1.2.0")]
    public async Task DiskPackageCache_UnsupportedSelectorFailsBeforeSourceRead(
        string version)
    {
        string cacheRoot = Path.Combine(
            _testRoot,
            Guid.NewGuid().ToString("N"));
        using DiskPackageCache cache = new DiskPackageCache(
            cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits());
        using FailOnReadStream source = new FailOnReadStream();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    new PackageReference("example.package", version),
                    source,
                    new InstallCacheOptions { VerifyChecksum = false },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
        source.ReadAttempts.ShouldBe(0);
        Directory.Exists(
            Path.Combine(cacheRoot, ".fhirpkg", "staging")).ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateExpectedAsync_MalformedJson_ThrowsInvalidArchive()
    {
        string manifestPath = await WriteManifestAsync("{not-json");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "1.0.0"));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#1.0.0",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Theory]
    [InlineData("", "1.0.0")]
    [InlineData(" ", "1.0.0")]
    [InlineData(" example.package", "1.0.0")]
    [InlineData("example.package", "")]
    [InlineData("example.package", " 1.0.0")]
    public async Task ValidateExpectedAsync_UntrimmedOrEmptyIdentity_ThrowsInvalidIdentity(
        string name,
        string version)
    {
        string manifestPath = await WriteManifestAsync(
            $$"""{"name":"{{name}}","version":"{{version}}"}""");
        PackageIdentityExpectation expectation =
            PackageIdentityValidator.CreateExpectation(
                new PackageReference("example.package", "1.0.0"));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.ValidateExpectedAsync(
                    manifestPath,
                    expectation,
                    "example.package#1.0.0",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Fact]
    public async Task DiscoverAsync_DerivesValidatedExactCacheIdentity()
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"@Example/Package","version":"2.0.0-Alpha"}""");

        PackageIdentityValidationResult result =
            await PackageIdentityValidator.DiscoverAsync(
                manifestPath,
                directive: null,
                TestContext.Current.CancellationToken);

        result.ManifestReference.ShouldBe(
            PackageReference.Parse("@Example/Package@2.0.0-Alpha"));
        result.CacheKey.CanonicalReference.Name.ShouldBe("@example/package");
        result.CacheKey.CanonicalReference.Version.ShouldBe("2.0.0-Alpha");
    }

    [Fact]
    public async Task DiscoverAsync_InvalidCacheIdentity_ThrowsInvalidIdentity()
    {
        string manifestPath = await WriteManifestAsync(
            """{"name":"CON","version":"1.0.0"}""");

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageIdentityValidator.DiscoverAsync(
                    manifestPath,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Fact]
    public void PackageProgressPhase_AddsStagesWithoutChangingExistingValues()
    {
        ((int)PackageProgressPhase.Resolving).ShouldBe(0);
        ((int)PackageProgressPhase.Downloading).ShouldBe(1);
        ((int)PackageProgressPhase.Extracting).ShouldBe(2);
        ((int)PackageProgressPhase.Indexing).ShouldBe(3);
        ((int)PackageProgressPhase.Complete).ShouldBe(4);
        ((int)PackageProgressPhase.Failed).ShouldBe(5);
        ((int)PackageProgressPhase.Acquiring).ShouldBe(6);
        ((int)PackageProgressPhase.Validating).ShouldBe(7);
        ((int)PackageProgressPhase.WaitingForLock).ShouldBe(8);
        ((int)PackageProgressPhase.Repairing).ShouldBe(9);
        ((int)PackageProgressPhase.Committing).ShouldBe(10);
    }

    private async Task<string> WriteManifestAsync(string content)
    {
        string path = Path.Combine(
            _testRoot,
            $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            content,
            TestContext.Current.CancellationToken);
        return path;
    }

    private sealed class FailOnReadStream : Stream
    {
        internal int ReadAttempts { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempts++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
