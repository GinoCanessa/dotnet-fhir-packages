// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Shouldly;
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
        DiskPackageCache cache = CreateCache();

        IReadOnlyList<PackageRecord> packages = await cache.ListPackagesAsync();

        packages.ShouldBeEmpty();
    }

    // ───────────────────────── Install ─────────────────────────

    [Fact]
    public async Task Install_ValidTarball_CreatesDirectoryStructure()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["Patient.json"] = """{"resourceType":"Patient"}""" });

        PackageRecord record = await cache.InstallAsync(reference, tarball);

        record.ShouldNotBeNull();
        record.Reference.Name.ShouldBe("test.package");
        record.Reference.Version.ShouldBe("1.0.0");

        // Verify directory structure: {cache}/test.package#1.0.0/package/
        string pkgDir = Path.Combine(TempCacheDir, "test.package#1.0.0", "package");
        Directory.Exists(pkgDir).ShouldBeTrue("package content directory should exist");
        File.Exists(Path.Combine(pkgDir, "package.json")).ShouldBeTrue("manifest should exist");
        File.Exists(Path.Combine(pkgDir, "Patient.json")).ShouldBeTrue("resource file should exist");
    }

    [Fact]
    public async Task Install_AlreadyExists_ThrowsByDefault()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball1 = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball1);

        using Stream tarball2 = CreateTestTarball("test.package", "1.0.0");
        Func<Task<PackageRecord>> act = () => cache.InstallAsync(reference, tarball2);

        await Should.ThrowAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task Install_OverwriteExisting_ReplacesPackage()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball1 = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["old.json"] = "{}" });
        await cache.InstallAsync(reference, tarball1);

        using Stream tarball2 = CreateTestTarball("test.package", "1.0.0",
            new Dictionary<string, string> { ["new.json"] = "{}" });
        PackageRecord record = await cache.InstallAsync(reference, tarball2,
            new InstallCacheOptions { OverwriteExisting = true });

        record.ShouldNotBeNull();
        string pkgDir = Path.Combine(TempCacheDir, "test.package#1.0.0", "package");
        File.Exists(Path.Combine(pkgDir, "new.json")).ShouldBeTrue("new file should exist after overwrite");
    }

    // ───────────────────────── ReadManifest ─────────────────────────

    [Fact]
    public async Task ReadManifest_InstalledPackage_ReturnsManifest()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#2.0.0";

        using Stream tarball = CreateTestTarball("test.package", "2.0.0");
        await cache.InstallAsync(reference, tarball);

        PackageManifest? manifest = await cache.ReadManifestAsync(reference);

        manifest.ShouldNotBeNull();
        manifest!.Name.ShouldBe("test.package");
        manifest.Version.ShouldBe("2.0.0");
    }

    // ───────────────────────── Remove ─────────────────────────

    [Fact]
    public async Task Remove_InstalledPackage_DeletesDirectory()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        bool removed = await cache.RemoveAsync(reference);

        removed.ShouldBeTrue();
        Directory.Exists(Path.Combine(TempCacheDir, "test.package#1.0.0")).ShouldBeFalse();
    }

    [Fact]
    public async Task Remove_NonExistent_ReturnsFalse()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "nonexistent#1.0.0";

        bool removed = await cache.RemoveAsync(reference);

        removed.ShouldBeFalse();
    }

    // ───────────────────────── Clear ─────────────────────────

    [Fact]
    public async Task Clear_RemovesAllPackages()
    {
        DiskPackageCache cache = CreateCache();

        using Stream tarball1 = CreateTestTarball("pkg.a", "1.0.0");
        await cache.InstallAsync(PackageReference.Parse("pkg.a#1.0.0"), tarball1);

        using Stream tarball2 = CreateTestTarball("pkg.b", "2.0.0");
        await cache.InstallAsync(PackageReference.Parse("pkg.b#2.0.0"), tarball2);

        int count = await cache.ClearAsync();

        count.ShouldBeGreaterThanOrEqualTo(2);

        IReadOnlyList<PackageRecord> remaining = await cache.ListPackagesAsync();
        remaining.ShouldBeEmpty();
    }

    // ───────────────────── GetPackageContentPath ─────────────────────

    [Fact]
    public async Task GetPackageContentPath_InstalledPackage_ReturnsCorrectPath()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        string? path = cache.GetPackageContentPath(reference);

        path.ShouldNotBeNull();
        path.ShouldEndWith(Path.Combine("test.package#1.0.0", "package"));
        Directory.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void GetPackageContentPath_MissingPackage_ReturnsNull()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "missing.package#1.0.0";

        string? path = cache.GetPackageContentPath(reference);

        path.ShouldBeNull();
    }

    // ───────────────────────── Metadata ─────────────────────────

    [Fact]
    public async Task Metadata_UpdatedOnInstall()
    {
        DiskPackageCache cache = CreateCache();
        PackageReference reference = "test.package#1.0.0";

        using Stream tarball = CreateTestTarball("test.package", "1.0.0");
        await cache.InstallAsync(reference, tarball);

        string iniPath = Path.Combine(TempCacheDir, "packages.ini");
        File.Exists(iniPath).ShouldBeTrue("packages.ini should be created after install");

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections = IniParser.ParseFile(iniPath);
        sections.Keys.ShouldContain("packages");
        sections["packages"].Keys.ShouldContain("test.package#1.0.0");
    }

    // ───────────────────────── ListWithFilter ─────────────────────────

    [Fact]
    public async Task ListWithFilter_ReturnsMatchingOnly()
    {
        DiskPackageCache cache = CreateCache();

        using Stream tarball1 = CreateTestTarball("hl7.fhir.r4.core", "4.0.1");
        await cache.InstallAsync(PackageReference.Parse("hl7.fhir.r4.core#4.0.1"), tarball1);

        using Stream tarball2 = CreateTestTarball("other.package", "1.0.0");
        await cache.InstallAsync(PackageReference.Parse("other.package#1.0.0"), tarball2);

        IReadOnlyList<PackageRecord> filtered = await cache.ListPackagesAsync(packageIdFilter: "hl7");

        filtered.ShouldHaveSingleItem()
            .Reference.Name.ShouldBe("hl7.fhir.r4.core");
    }
}
