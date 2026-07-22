// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class DiskPackageCacheValidityTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-validity-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("missing-package")]
    [InlineData("missing-manifest")]
    [InlineData("malformed-manifest")]
    [InlineData("mismatched-name")]
    [InlineData("mismatched-version")]
    [InlineData("manifest-directory")]
    public async Task CorruptEntry_IsRejectedByEveryReadSurface(string shape)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        Directory.CreateDirectory(targetPath);
        string contentPath = Path.Combine(targetPath, "package");
        if (shape != "missing-package")
            Directory.CreateDirectory(contentPath);

        string manifestPath = Path.Combine(contentPath, "package.json");
        switch (shape)
        {
            case "missing-package":
            case "missing-manifest":
                break;
            case "malformed-manifest":
                await File.WriteAllTextAsync(
                    manifestPath,
                    "{not-json",
                    TestContext.Current.CancellationToken);
                break;
            case "mismatched-name":
                await File.WriteAllTextAsync(
                    manifestPath,
                    """{"name":"other.package","version":"1.0.0"}""",
                    TestContext.Current.CancellationToken);
                break;
            case "mismatched-version":
                await File.WriteAllTextAsync(
                    manifestPath,
                    """{"name":"example.package","version":"2.0.0"}""",
                    TestContext.Current.CancellationToken);
                break;
            case "manifest-directory":
                Directory.CreateDirectory(manifestPath);
                break;
        }

        if (Directory.Exists(contentPath))
        {
            await File.WriteAllTextAsync(
                Path.Combine(contentPath, ".index.json"),
                """{"files":[]}""",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(contentPath, "visible.txt"),
                "must not be returned",
                TestContext.Current.CancellationToken);
        }

        using DiskPackageCache cache = CreateCache();
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 1
            },
            TestContext.Current.CancellationToken);

        (await cache.IsInstalledAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await cache.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await cache.ReadManifestAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.GetIndexAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.GetFileContentAsync(
            reference,
            "visible.txt",
            TestContext.Current.CancellationToken)).ShouldBeNull();
        cache.GetPackageContentPath(reference).ShouldBeNull();
    }

    [Fact]
    public async Task ValidEntryWithoutMetadata_IsReturnedByEveryReadSurface()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = Path.Combine(targetPath, "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"example.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"files":[]}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "visible.txt"),
            "content",
            TestContext.Current.CancellationToken);

        using DiskPackageCache cache = CreateCache();

        (await cache.IsInstalledAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await cache.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        (await cache.ReadManifestAsync(
            reference,
            TestContext.Current.CancellationToken))!.Version
            .ShouldBe("1.0.0");
        (await cache.GetIndexAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await cache.GetFileContentAsync(
            reference,
            "visible.txt",
            TestContext.Current.CancellationToken)).ShouldBe("content");
        cache.GetPackageContentPath(reference).ShouldBe(contentPath);
    }

    [Fact]
    public async Task AliasEntry_WithConcreteManifestVersion_IsValid()
    {
        PackageReference reference = new(
            "example.package",
            "current$main");
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"example.package","version":"4.1.0"}""",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();

        (await cache.IsInstalledAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task StrictCorruption_FailsBeforeReadingReplacement()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"other.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        using FailOnReadStream source = new();
        using DiskPackageCache cache = CreateCache();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    source,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        CorruptCacheBehavior = CorruptCacheBehavior.Strict
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CorruptCache);
        exception.Stage.ShouldBe(PackageInstallStage.CacheInspection);
        source.ReadAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task ManagerStrictCorruption_PreemptsUnresolvedDirectiveReturn()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"other.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();
        Mock<IRegistryClient> registry = new();
        registry.Setup(client => client.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);
        FhirPackageManagerOptions options = new()
        {
            CorruptCacheBehavior = CorruptCacheBehavior.Strict
        };
        using FhirPackageManager manager = new(
            cache,
            registry.Object,
            Mock.Of<IVersionResolver>(),
            Mock.Of<IDependencyResolver>(),
            Mock.Of<IPackageIndexer>(),
            options,
            NullLogger<FhirPackageManager>.Instance,
            memoryCache: null,
            managerInstallLimits: new PackageInstallLimits());

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    reference.FhirDirective,
                    cancellationToken:
                        TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CorruptCache);
        registry.Verify(client => client.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManagerStrictDevCorruption_FailsBeforeLocalEarlyReturn()
    {
        PackageReference reference = new(
            "example.package",
            "dev");
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            "{corrupt",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();
        Mock<IRegistryClient> registry = new();
        FhirPackageManagerOptions options = new()
        {
            CorruptCacheBehavior = CorruptCacheBehavior.Strict
        };
        using FhirPackageManager manager = new(
            cache,
            registry.Object,
            Mock.Of<IVersionResolver>(),
            Mock.Of<IDependencyResolver>(),
            Mock.Of<IPackageIndexer>(),
            options,
            NullLogger<FhirPackageManager>.Instance,
            memoryCache: null,
            managerInstallLimits: new PackageInstallLimits());

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    reference.FhirDirective,
                    cancellationToken:
                        TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CorruptCache);
        registry.Verify(client => client.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DefaultRepair_ReplacesValidatedCorruptTarget()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            "{not-json",
            TestContext.Current.CancellationToken);
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package/package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File("package/value.txt", "repaired"));
        using DiskPackageCache cache = CreateCache();

        PackageRecord record = await cache.InstallAsync(
            reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);

        record.Manifest.Version.ShouldBe("1.0.0");
        (await cache.GetFileContentAsync(
            reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("repaired");
    }

    [Fact]
    public async Task UnsafeScopedAncestor_IsNotTreatedAsRepairableTarget()
    {
        PackageReference reference = new(
            "@scope/example.package",
            "1.0.0",
            "@scope");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string scopePath = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(_cacheRoot);
        await File.WriteAllTextAsync(
            scopePath,
            "not a directory",
            TestContext.Current.CancellationToken);
        using FailOnReadStream source = new();
        using DiskPackageCache cache = CreateCache();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    source,
                    new InstallCacheOptions { VerifyChecksum = false },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CorruptCache);
        source.ReadAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task UnixFifoManifest_IsRejectedWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string manifestPath = CreateManifestParent(reference);
        MkFifo(manifestPath, 0x180).ShouldBe(0);
        using DiskPackageCache cache = CreateCache();

        bool installed = await cache.IsInstalledAsync(
                reference,
                TestContext.Current.CancellationToken)
            .WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        installed.ShouldBeFalse();
    }

    [Fact]
    public async Task UnixSocketManifest_IsRejectedWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        string repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        string socketCacheRoot = Path.Combine(
            repositoryRoot,
            $".s-{Guid.NewGuid():N}"[..11]);
        PackageReference reference = new(
            "p",
            "1");
        try
        {
            string contentPath = Path.Combine(
                PackageCacheKey.Create(reference)
                    .GetPackageDirectoryPath(socketCacheRoot),
                "package");
            Directory.CreateDirectory(contentPath);
            string manifestPath = Path.Combine(
                contentPath,
                "package.json");
            using Socket socket = new(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(manifestPath));
            using DiskPackageCache cache = new(
                socketCacheRoot,
                logger: null,
                timeProvider: null,
                new PackageInstallLimits());

            bool installed = await cache.IsInstalledAsync(
                    reference,
                    TestContext.Current.CancellationToken)
                .WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);

            installed.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(socketCacheRoot))
                Directory.Delete(socketCacheRoot, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    private DiskPackageCache CreateCache() =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits());

    private string CreateManifestParent(PackageReference reference)
    {
        string contentPath = Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        return Path.Combine(contentPath, "package.json");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

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
            throw new InvalidOperationException("Source must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            throw new InvalidOperationException("Source must not be read.");
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }
}
