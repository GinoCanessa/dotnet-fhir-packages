// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FluentAssertions;
using FhirPkg.Cache;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public class CacheIntegrationTests : IntegrationTestBase
{
    private DiskPackageCache CreateCache() => new(TempCacheDir);

    // ───────────────────────── List ─────────────────────────

    [Fact]
    public async Task List_EmptyCache_ReturnsEmpty()
    {
        var cache = CreateCache();

        var packages = await cache.ListPackagesAsync();

        packages.Should().BeEmpty();
    }

    // ───────────────────────── Install ─────────────────────────

    [Fact]
    public async Task Install_ValidTarball_CreatesDirectoryStructure()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["Patient.json"] = """{"resourceType":"Patient"}""" });

        var record = await cache.InstallAsync(reference, tarball);

        record.Should().NotBeNull();
        record.Reference.Name.Should().Be("test.package");
        record.Reference.Version.Should().Be("1.0.0");

        // Verify directory structure: {cache}/test.package#1.0.0/package/
        var pkgDir = Path.Combine(TempCacheDir, "test.package#1.0.0", "package");
        Directory.Exists(pkgDir).Should().BeTrue("package content directory should exist");
        File.Exists(Path.Combine(pkgDir, "package.json")).Should().BeTrue("manifest should exist");
        File.Exists(Path.Combine(pkgDir, "Patient.json")).Should().BeTrue("resource file should exist");
    }

    [Fact]
    public async Task Install_AlreadyExists_ThrowsByDefault()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball1 = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball1);

        using var tarball2 = CreateTestTarball("test.package", "1.0.0");
        var act = () => cache.InstallAsync(reference, tarball2);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Install_OverwriteExisting_ReplacesPackage()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball1 = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["old.json"] = "{}" });
        await cache.InstallAsync(reference, tarball1);

        using var tarball2 = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["new.json"] = "{}" });
        var record = await cache.InstallAsync(reference, tarball2,
            new InstallCacheOptions { OverwriteExisting = true });

        record.Should().NotBeNull();
        var pkgDir = Path.Combine(TempCacheDir, "test.package#1.0.0", "package");
        File.Exists(Path.Combine(pkgDir, "new.json")).Should().BeTrue("new file should exist after overwrite");
    }

    // ───────────────────────── ReadManifest ─────────────────────────

    [Fact]
    public async Task ReadManifest_InstalledPackage_ReturnsManifest()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#2.0.0";

        using var tarball = CreateTestTarball("test.package", "2.0.0");
        await cache.InstallAsync(reference, tarball);

        var manifest = await cache.ReadManifestAsync(reference);

        manifest.Should().NotBeNull();
        manifest!.Name.Should().Be("test.package");
        manifest.Version.Should().Be("2.0.0");
    }

    // ───────────────────────── Remove ─────────────────────────

    [Fact]
    public async Task Remove_InstalledPackage_DeletesDirectory()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        var removed = await cache.RemoveAsync(reference);

        removed.Should().BeTrue();
        Directory.Exists(Path.Combine(TempCacheDir, "test.package#1.0.0")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_NonExistent_ReturnsFalse()
    {
        var cache = CreateCache();
        PackageReference reference = "nonexistent#1.0.0";

        var removed = await cache.RemoveAsync(reference);

        removed.Should().BeFalse();
    }

    // ───────────────────────── Clear ─────────────────────────

    [Fact]
    public async Task Clear_RemovesAllPackages()
    {
        var cache = CreateCache();

        using var tarball1 = CreateTestTarball("pkg.a", "1.0.0");
        await cache.InstallAsync(PackageReference.Parse("pkg.a#1.0.0"), tarball1);

        using var tarball2 = CreateTestTarball("pkg.b", "2.0.0");
        await cache.InstallAsync(PackageReference.Parse("pkg.b#2.0.0"), tarball2);

        var count = await cache.ClearAsync();

        count.Should().BeGreaterThanOrEqualTo(2);

        var remaining = await cache.ListPackagesAsync();
        remaining.Should().BeEmpty();
    }

    // ───────────────────── GetPackageContentPath ─────────────────────

    [Fact]
    public async Task GetPackageContentPath_InstalledPackage_ReturnsCorrectPath()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        var path = cache.GetPackageContentPath(reference);

        path.Should().NotBeNull();
        path.Should().EndWith(Path.Combine("test.package#1.0.0", "package"));
        Directory.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void GetPackageContentPath_MissingPackage_ReturnsNull()
    {
        var cache = CreateCache();
        PackageReference reference = "missing.package#1.0.0";

        var path = cache.GetPackageContentPath(reference);

        path.Should().BeNull();
    }

    // ───────────────────────── Metadata ─────────────────────────

    [Fact]
    public async Task Metadata_UpdatedOnInstall()
    {
        var cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using var tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        var iniPath = Path.Combine(TempCacheDir, "packages.ini");
        File.Exists(iniPath).Should().BeTrue("packages.ini should be created after install");

        var sections = IniParser.ParseFile(iniPath);
        sections.Should().ContainKey("packages");
        sections["packages"].Should().ContainKey("test.package#1.0.0");
    }

    // ───────────────────────── ListWithFilter ─────────────────────────

    [Fact]
    public async Task ListWithFilter_ReturnsMatchingOnly()
    {
        var cache = CreateCache();

        using var tarball1 = CreateTestTarball("hl7.fhir.r4.core", "4.0.1");
        await cache.InstallAsync(PackageReference.Parse("hl7.fhir.r4.core#4.0.1"), tarball1);

        using var tarball2 = CreateTestTarball("other.package", "1.0.0");
        await cache.InstallAsync(PackageReference.Parse("other.package#1.0.0"), tarball2);

        var filtered = await cache.ListPackagesAsync(packageIdFilter: "hl7");

        filtered.Should().ContainSingle()
            .Which.Reference.Name.Should().Be("hl7.fhir.r4.core");
    }
}
