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
    private const int ErrorPrivilegeNotHeld = 1314;

    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-validity-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("missing-package", false)]
    [InlineData("missing-manifest", false)]
    [InlineData("malformed-manifest", false)]
    [InlineData("mismatched-name", false)]
    [InlineData("mismatched-version", false)]
    [InlineData("manifest-directory", false)]
    [InlineData("missing-manifest", true)]
    [InlineData("malformed-manifest", true)]
    [InlineData("mismatched-name", true)]
    [InlineData("mismatched-version", true)]
    [InlineData("manifest-directory", true)]
    public async Task CorruptEntry_IsRejectedByEveryReadSurface(
        string shape,
        bool rootLayout)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        Directory.CreateDirectory(targetPath);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
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

        await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidEntryWithoutMetadata_IsReturnedByEveryReadSurface(
        bool rootLayout)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
        await WriteValidContentAsync(contentPath);
        using DiskPackageCache cache = CreateCache();

        await AssertValidByEveryReadSurfaceAsync(
            cache,
            reference,
            targetPath,
            contentPath);
        File.Exists(Path.Combine(_cacheRoot, "packages.ini")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasEntry_WithConcreteManifestVersion_IsValid(bool rootLayout)
    {
        PackageReference reference = new(
            "example.package",
            "current$main");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
        await WriteValidContentAsync(contentPath, version: "4.1.0");
        using DiskPackageCache cache = CreateCache();

        await AssertValidByEveryReadSurfaceAsync(
            cache,
            reference,
            targetPath,
            contentPath,
            manifestVersion: "4.1.0");
    }

    [Theory]
    [InlineData("file")]
    [InlineData("empty-directory")]
    [InlineData("subtree")]
    [InlineData("wrong-case-manifest")]
    [InlineData("wrong-case-directory")]
    [InlineData("manifest-directory")]
    public async Task RootEntry_IncidentalPackagePath_DoesNotRedirectAnyReader(string shape)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string packagePath = Path.Combine(targetPath, "package");
        await WriteValidContentAsync(targetPath, marker: "root");
        switch (shape)
        {
            case "file":
                await File.WriteAllTextAsync(
                    packagePath,
                    "incidental package file",
                    TestContext.Current.CancellationToken);
                break;
            case "empty-directory":
                Directory.CreateDirectory(packagePath);
                break;
            case "subtree":
                Directory.CreateDirectory(Path.Combine(packagePath, "nested"));
                await File.WriteAllTextAsync(
                    Path.Combine(packagePath, "nested", "package.json"),
                    "{opaque malformed nested manifest",
                    TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(packagePath, ".index.json"),
                    "{opaque index",
                    TestContext.Current.CancellationToken);
                break;
            case "wrong-case-manifest":
                await WriteValidContentAsync(
                    packagePath,
                    marker: "incidental",
                    version: "2.0.0",
                    manifestFileName: "Package.json");
                break;
            case "wrong-case-directory":
                await WriteValidContentAsync(
                    Path.Combine(targetPath, "Package"),
                    marker: "incidental",
                    version: "2.0.0");
                break;
            case "manifest-directory":
                Directory.CreateDirectory(Path.Combine(packagePath, "package.json"));
                break;
        }
        using DiskPackageCache cache = CreateCache();

        await AssertValidByEveryReadSurfaceAsync(
            cache,
            reference,
            targetPath,
            targetPath,
            marker: "root");
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("mismatched-name")]
    [InlineData("mismatched-version")]
    public async Task BothRegularManifestSlots_StandardIsSelectedWithoutFallback(string shape)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = Path.Combine(targetPath, "package");
        await WriteValidContentAsync(targetPath, marker: "root");
        await WriteValidContentAsync(contentPath, marker: "standard");
        if (shape != "valid")
        {
            string manifest = shape switch
            {
                "malformed" => "{not-json",
                "mismatched-name" => """{"name":"other.package","version":"1.0.0"}""",
                "mismatched-version" => """{"name":"example.package","version":"2.0.0"}""",
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
            };
            await File.WriteAllTextAsync(
                Path.Combine(contentPath, "package.json"),
                manifest,
                TestContext.Current.CancellationToken);
        }
        using DiskPackageCache cache = CreateCache();

        if (shape == "valid")
        {
            await AssertValidByEveryReadSurfaceAsync(
                cache,
                reference,
                targetPath,
                contentPath,
                marker: "standard");
        }
        else
        {
            await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
            new PackageCacheValidator(_cacheRoot).Inspect(reference)
                .ContentPath.ShouldBe(contentPath);
            (await cache.InspectAsync(reference, TestContext.Current.CancellationToken))
                .ContentPath.ShouldBe(contentPath);
        }
    }

    [Theory]
    [InlineData("{opaque malformed root manifest")]
    [InlineData("""{"name":"other.package","version":"2.0.0"}""")]
    public async Task StandardEntry_DoesNotValidateUnselectedRootManifest(string rootManifest)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = Path.Combine(targetPath, "package");
        await WriteValidContentAsync(contentPath, marker: "standard");
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, "package.json"),
            rootManifest,
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();

        await AssertValidByEveryReadSurfaceAsync(
            cache,
            reference,
            targetPath,
            contentPath,
            marker: "standard");
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("""{"name":"other.package","version":"1.0.0"}""")]
    [InlineData("""{"name":"example.package","version":"2.0.0"}""")]
    public async Task InvalidRootManifest_DoesNotFallBackToWrongCaseStandardManifest(
        string manifest)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        await WriteValidContentAsync(targetPath, marker: "root");
        await WriteValidContentAsync(
            Path.Combine(targetPath, "package"),
            marker: "incidental",
            manifestFileName: "Package.json");
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, "package.json"),
            manifest,
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();

        await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
        new PackageCacheValidator(_cacheRoot).Inspect(reference)
            .ContentPath.ShouldBe(targetPath);
        (await cache.InspectAsync(reference, TestContext.Current.CancellationToken))
            .ContentPath.ShouldBe(targetPath);
    }

    [Theory]
    [InlineData("package", "Package.json")]
    [InlineData("Package", "package.json")]
    [InlineData("Package", "Package.json")]
    public async Task StandardOnlyEntry_RetainsHostCaseCompatibility(
        string directoryName,
        string manifestName)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        await WriteValidContentAsync(
            Path.Combine(targetPath, directoryName),
            manifestFileName: manifestName);
        string contentPath = Path.Combine(targetPath, "package");
        bool hostCanReadStandardSlot = File.Exists(Path.Combine(contentPath, "package.json"));
        using DiskPackageCache cache = CreateCache();

        if (hostCanReadStandardSlot)
        {
            await AssertValidByEveryReadSurfaceAsync(
                cache,
                reference,
                targetPath,
                contentPath);
        }
        else
        {
            await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
        }
    }

    [Fact]
    public async Task RootManifestWrongCase_IsNotAnAuthoritativeCacheSlot()
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        await WriteValidContentAsync(targetPath, manifestFileName: "Package.json");
        using DiskPackageCache cache = CreateCache();

        await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledUnmodifiedEntry_ReopensThroughEveryReadSurface(bool rootLayout)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string prefix = rootLayout ? "" : "package/";
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                $"{prefix}package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File($"{prefix}visible.txt", "content"),
            ArbitraryTarBuilder.File(
                $"{prefix}.index.json",
                """{"files":[{"filename":"visible.txt","resourceType":"Patient","id":"content"}]}"""),
            ArbitraryTarBuilder.File("other/package.json", "{opaque"),
            ArbitraryTarBuilder.File(".fhirpkg/payload.txt", "not cache control"),
            ArbitraryTarBuilder.File("packages.ini", "not cache metadata"));
        PackageRecord installed;
        using (DiskPackageCache cache = CreateCache())
        {
            installed = await cache.InstallAsync(
                reference,
                archive,
                new InstallCacheOptions { Unmodified = true },
                TestContext.Current.CancellationToken);
        }
        using DiskPackageCache reopened = CreateCache();

        await AssertValidByEveryReadSurfaceAsync(
            reopened,
            reference,
            installed.DirectoryPath,
            installed.ContentPath);
        PackageRecord record = (await reopened.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        record.SizeBytes.ShouldBe(installed.SizeBytes);
        record.InstalledAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("1.0.0", false, false)]
    [InlineData("1.0.0", false, true)]
    [InlineData("1.0.0", true, false)]
    [InlineData("1.0.0", true, true)]
    [InlineData("current", false, false)]
    [InlineData("current", false, true)]
    [InlineData("current", true, false)]
    [InlineData("current", true, true)]
    public async Task ValidCacheHit_DoesNotReadSourceOrConvertLayout(
        string version,
        bool rootLayout,
        bool unmodified)
    {
        PackageReference reference = new("example.package", version);
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
        await WriteValidContentAsync(contentPath);
        using DiskPackageCache cache = CreateCache();
        using FailOnReadStream source = new();
        InstallCacheOptions options = new() { Unmodified = unmodified };

        PackageRecord record = await cache.InstallAsync(
            reference,
            source,
            options,
            TestContext.Current.CancellationToken);

        source.ReadAttempts.ShouldBe(0);
        source.CanRead.ShouldBeTrue();
        options.InstallOutcome.Effect.ShouldBe(PackageCacheInstallEffect.Unchanged);
        record.ContentPath.ShouldBe(contentPath);
        await AssertValidByEveryReadSurfaceAsync(
            cache,
            reference,
            targetPath,
            contentPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentFileReads_RemainConfinedToSelectedDirectory(bool rootLayout)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
        await WriteValidContentAsync(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, "sibling-only.txt"),
            "distributed root file",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_cacheRoot, "cache-only.txt"),
            "outside identity",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();

        (await cache.GetFileContentAsync(
            reference,
            "sibling-only.txt",
            TestContext.Current.CancellationToken))
            .ShouldBe(rootLayout ? "distributed root file" : null);
        await Should.ThrowAsync<ArgumentException>(
            () => cache.GetFileContentAsync(
                reference,
                rootLayout ? "../cache-only.txt" : "../sibling-only.txt",
                TestContext.Current.CancellationToken));
        (await File.ReadAllTextAsync(
            Path.Combine(targetPath, "sibling-only.txt"),
            TestContext.Current.CancellationToken)).ShouldBe("distributed root file");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixFifoManifest_IsRejectedWithoutBlocking(bool rootLayout)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        PackageReference reference = new(
            "example.package",
            "1.0.0");
        string manifestPath = CreateManifestParent(reference, rootLayout);
        MkFifo(manifestPath, 0x180).ShouldBe(0);
        using DiskPackageCache cache = CreateCache();

        bool installed = await cache.IsInstalledAsync(
                reference,
                TestContext.Current.CancellationToken)
            .WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        installed.ShouldBeFalse();
        cache.GetPackageContentPath(reference).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnixSocketManifest_IsRejectedWithoutBlocking(bool rootLayout)
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
            string targetPath = PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(socketCacheRoot);
            string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
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
            cache.GetPackageContentPath(reference).ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(socketCacheRoot))
                Directory.Delete(socketCacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymbolicLinkManifest_IsRejectedByEveryReadSurface(bool rootLayout)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string manifestPath = CreateManifestParent(reference, rootLayout);
        string linkTarget = Path.Combine(_cacheRoot, "external-manifest.json");
        await File.WriteAllTextAsync(
            linkTarget,
            """{"name":"example.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        try
        {
            File.CreateSymbolicLink(manifestPath, linkTarget);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link requires privileges on this host.");
        }
        catch (IOException exception) when (
            OperatingSystem.IsWindows()
            && (exception.HResult & 0xFFFF) == ErrorPrivilegeNotHeld)
        {
            Assert.Skip("Creating a symbolic link requires privileges on this host.");
        }
        using DiskPackageCache cache = CreateCache();

        await AssertRejectedByEveryReadSurfaceAsync(cache, reference);
        File.Exists(linkTarget).ShouldBeTrue();
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

    private string CreateManifestParent(
        PackageReference reference,
        bool rootLayout = false)
    {
        string targetPath = PackageCacheKey.Create(reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = rootLayout ? targetPath : Path.Combine(targetPath, "package");
        Directory.CreateDirectory(contentPath);
        return Path.Combine(contentPath, "package.json");
    }

    private static async Task WriteValidContentAsync(
        string contentPath,
        string marker = "content",
        string version = "1.0.0",
        string manifestFileName = "package.json")
    {
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, manifestFileName),
            $$"""{"name":"example.package","version":"{{version}}"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            $$"""{"files":[{"filename":"visible.txt","resourceType":"Patient","id":"{{marker}}"}]}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "visible.txt"),
            marker,
            TestContext.Current.CancellationToken);
    }

    private static async Task AssertValidByEveryReadSurfaceAsync(
        DiskPackageCache cache,
        PackageReference reference,
        string targetPath,
        string contentPath,
        string marker = "content",
        string manifestVersion = "1.0.0")
    {
        PackageCacheValidator validator = new(cache.CacheDirectory);
        PackageCacheInspection[] inspections =
        [
            validator.Inspect(reference),
            await validator.InspectAsync(reference, TestContext.Current.CancellationToken),
            await cache.InspectAsync(reference, TestContext.Current.CancellationToken)
        ];
        foreach (PackageCacheInspection inspection in inspections)
        {
            inspection.State.ShouldBe(PackageCacheInspectionState.Valid);
            inspection.TargetPath.ShouldBe(targetPath);
            inspection.ContentPath.ShouldBe(contentPath);
            inspection.ManifestPath.ShouldBe(Path.Combine(contentPath, "package.json"));
            inspection.Manifest!.Name.ShouldBe(reference.Name);
            inspection.Manifest.Version.ShouldBe(manifestVersion);
        }

        (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await ((IHardenedPackageCache)cache).InspectAsync(
            reference,
            TestContext.Current.CancellationToken)).State.ShouldBe(HardenedPackageCacheState.Valid);
        PackageRecord retrieved = (await cache.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        PackageRecord hydrated = (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        PackageRecord summary = (await cache.ListPackageSummariesAsync(
            ct: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        PackageRecord indexing = (await ((IPackageCacheIndexStore)cache).ListPackagesForIndexingAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        foreach (PackageRecord record in new[] { retrieved, hydrated, summary, indexing })
        {
            record.Reference.ShouldBe(reference);
            record.DirectoryPath.ShouldBe(targetPath);
            record.ContentPath.ShouldBe(contentPath);
            record.Manifest.Name.ShouldBe(reference.Name);
            record.Manifest.Version.ShouldBe(manifestVersion);
        }
        retrieved.Index.ShouldNotBeNull().Files.ShouldHaveSingleItem().Id.ShouldBe(marker);
        hydrated.Index.ShouldNotBeNull().Files.ShouldHaveSingleItem().Id.ShouldBe(marker);
        summary.Index.ShouldBeNull();
        indexing.Index.ShouldBeNull();
        (await cache.ReadManifestAsync(reference, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Version.ShouldBe(manifestVersion);
        (await cache.GetIndexAsync(reference, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Files.ShouldHaveSingleItem().Id.ShouldBe(marker);
        (await cache.GetFileContentAsync(
            reference,
            "visible.txt",
            TestContext.Current.CancellationToken)).ShouldBe(marker);
        (await ((IPackageCacheResourceStore)cache).ReadFileAsync<string>(
            reference,
            "visible.txt",
            _ => null,
            (_, content) => content,
            TestContext.Current.CancellationToken)).ShouldBe(marker);
        cache.GetPackageContentPath(reference).ShouldBe(contentPath);
    }

    private static async Task AssertRejectedByEveryReadSurfaceAsync(
        DiskPackageCache cache,
        PackageReference reference)
    {
        PackageCacheValidator validator = new(cache.CacheDirectory);
        validator.Inspect(reference).State.ShouldBe(PackageCacheInspectionState.Corrupt);
        (await validator.InspectAsync(reference, TestContext.Current.CancellationToken))
            .State.ShouldBe(PackageCacheInspectionState.Corrupt);
        (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await ((IHardenedPackageCache)cache).InspectAsync(
            reference,
            TestContext.Current.CancellationToken)).State.ShouldBe(HardenedPackageCacheState.Corrupt);
        (await cache.GetPackageAsync(reference, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await cache.ListPackagesAsync(ct: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        (await cache.ListPackageSummariesAsync(ct: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        (await ((IPackageCacheIndexStore)cache).ListPackagesForIndexingAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await cache.ReadManifestAsync(reference, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await cache.GetIndexAsync(reference, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await cache.GetFileContentAsync(
            reference,
            "visible.txt",
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await ((IPackageCacheResourceStore)cache).ReadFileAsync<string>(
            reference,
            "visible.txt",
            _ => null,
            (_, content) => content,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        cache.GetPackageContentPath(reference).ShouldBeNull();
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
