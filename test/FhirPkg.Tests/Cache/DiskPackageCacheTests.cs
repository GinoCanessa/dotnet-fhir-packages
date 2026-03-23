// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

/// <summary>
/// Serializes test classes that mutate process-global environment variables so they
/// cannot race with one another when xUnit runs classes in parallel.
/// </summary>
[CollectionDefinition("EnvironmentVariable")]
public class EnvironmentVariableCollection : ICollectionFixture<EnvironmentVariableCollection> { }

[Collection("EnvironmentVariable")]
public class DiskPackageCacheTests : IDisposable
{
    private readonly string _tempDir;
    private string? _originalEnvVar;

    public DiskPackageCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fhir-cache-test-{Guid.NewGuid():N}");
        _originalEnvVar = Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER");
    }

    public void Dispose()
    {
        // Restore original env var
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", _originalEnvVar);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_ExplicitPath_UsesExplicitPath()
    {
        var cache = new DiskPackageCache(_tempDir);

        cache.CacheDirectory.ShouldBe(_tempDir);
        Directory.Exists(_tempDir).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_EnvVarSet_UsesEnvVar()
    {
        var envDir = Path.Combine(_tempDir, "from-env");
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", envDir);

        var cache = new DiskPackageCache();

        cache.CacheDirectory.ShouldBe(envDir);
        Directory.Exists(envDir).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_ExplicitPath_TakesPriorityOverEnvVar()
    {
        var envDir = Path.Combine(_tempDir, "from-env");
        var explicitDir = Path.Combine(_tempDir, "explicit");
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", envDir);

        var cache = new DiskPackageCache(explicitDir);

        cache.CacheDirectory.ShouldBe(explicitDir);
        Directory.Exists(explicitDir).ShouldBeTrue();
        Directory.Exists(envDir).ShouldBeFalse("env var path should not be created when explicit path is provided");
    }

    [Fact]
    public void Dispose_DisposesInstallLock()
    {
        var cache = new DiskPackageCache(_tempDir);

        cache.Dispose();

        // Verify the cache implements IDisposable and does not throw on double-dispose
        cache.Dispose();
    }

    [Fact]
    public void Constructor_NoPathNoEnvVar_UsesDefault()
    {
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", null);

        var cache = new DiskPackageCache();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fhir",
            "packages");
        cache.CacheDirectory.ShouldBe(expected);
    }

    [Fact]
    public async Task InstallAsync_AtomicMove_ProducesValidPackage()
    {
        using var cache = new DiskPackageCache(_tempDir);
        var reference = new PackageReference("test.package", "1.0.0");

        using var tarball = CreateTestTarball("""{"name":"test.package","version":"1.0.0"}""");

        var record = await cache.InstallAsync(reference, tarball, new InstallCacheOptions { VerifyChecksum = false });

        record.ShouldNotBeNull();
        record.Reference.Name.ShouldBe("test.package");
        record.Reference.Version.ShouldBe("1.0.0");
        Directory.Exists(record.DirectoryPath).ShouldBeTrue();

        var manifestPath = Path.Combine(record.ContentPath, "package.json");
        File.Exists(manifestPath).ShouldBeTrue();
    }

    private static MemoryStream CreateTestTarball(string packageJsonContent)
    {
        var memStream = new MemoryStream();
        using (var gzipStream = new GZipStream(memStream, CompressionMode.Compress, leaveOpen: true))
        using (var tarWriter = new TarWriter(gzipStream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(packageJsonContent))
            };
            tarWriter.WriteEntry(entry);
        }

        memStream.Position = 0;
        return memStream;
    }
}
