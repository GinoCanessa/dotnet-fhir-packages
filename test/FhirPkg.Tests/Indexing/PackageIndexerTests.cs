// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Indexing;
using FhirPkg.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Indexing;

public sealed class PackageIndexerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"package-indexer-{Guid.NewGuid():N}");

    [Fact]
    public async Task ManagedBuild_BuildsWithoutRegistering()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"one"}"""));
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageRecord record = CreateRecord(
            new PackageReference("example.package", "1.0.0"),
            contentPath);

        PackageIndex index = await managed.BuildIndexAsync(
            record,
            TestContext.Current.CancellationToken);

        index.Files.Count.ShouldBe(1);
        indexer.FindResources(new ResourceSearchCriteria()).ShouldBeEmpty();
    }

    [Fact]
    public async Task LowLevelIndexing_InvalidExistingIndexIsRebuiltAndRegistered()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"fresh"}"""));
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"index-version":1,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stale"}]}""",
            TestContext.Current.CancellationToken);
        PackageIndexer indexer = new(NullLogger.Instance);

        PackageIndex index = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken:
                TestContext.Current.CancellationToken);

        index.IndexVersion.ShouldBe(2);
        index.Files.Single().Id.ShouldBe("fresh");
        indexer.FindByResourceType("Patient")
            .Single().Id.ShouldBe("fresh");
    }

    [Fact]
    public void ManagedRegistration_UsesExactScopedPackageIdentity()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageReference first =
            PackageReference.Parse("@Example/Package@1.0.0");
        PackageReference second =
            PackageReference.Parse("@example/package@2.0.0");

        managed.RegisterPersistedIndex(
            first,
            CreateIndex(("first.json", "Patient", "first")));
        managed.RegisterPersistedIndex(
            second,
            CreateIndex(("second.json", "Patient", "second")));

        IReadOnlyList<ResourceInfo> firstResults =
            indexer.FindByResourceType(
                "Patient",
                "@example/package#1.0.0");
        IReadOnlyList<ResourceInfo> secondResults =
            indexer.FindByResourceType(
                "Patient",
                "@example/package#2.0.0");

        firstResults.Select(result => result.Id)
            .ShouldBe(["first"]);
        secondResults.Select(result => result.Id)
            .ShouldBe(["second"]);
        firstResults[0].PackageName.ShouldBe("@example/package");
    }

    [Fact]
    public void Searches_AreDeterministicAcrossRegistrationAndFileOrder()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        managed.RegisterPersistedIndex(
            new PackageReference("z.package", "1.0.0"),
            CreateIndex(
                ("z-second.json", "Patient", "z-second"),
                ("z-first.json", "Patient", "z-first")));
        managed.RegisterPersistedIndex(
            new PackageReference("a.package", "1.0.0"),
            CreateIndex(
                ("a-second.json", "Patient", "a-second"),
                ("a-first.json", "Patient", "a-first")));

        IReadOnlyList<ResourceInfo> results =
            indexer.FindResources(new ResourceSearchCriteria
            {
                ResourceTypes = ["Patient"]
            });

        results.Select(result => result.Id).ShouldBe(
        [
            "a-first",
            "a-second",
            "z-first",
            "z-second"
        ]);
        indexer.FindByCanonicalUrl("https://example/a-first")!
            .Id.ShouldBe("a-first");
    }

    [Fact]
    public void UnregisterAndClear_RemoveOnlyRequestedRegistrations()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageReference first = new("example.package", "1.0.0");
        PackageReference second = new("example.package", "2.0.0");
        managed.RegisterPersistedIndex(
            first,
            CreateIndex(("first.json", "Patient", "first")));
        managed.RegisterPersistedIndex(
            second,
            CreateIndex(("second.json", "Patient", "second")));

        managed.Unregister(first).ShouldBeTrue();
        managed.Unregister(first).ShouldBeFalse();
        indexer.FindByResourceType("Patient")
            .Select(result => result.Id)
            .ShouldBe(["second"]);

        managed.Clear();

        indexer.FindByResourceType("Patient").ShouldBeEmpty();
    }

    [Fact]
    public async Task ManagedSearchAndClear_DoNotAffectLegacyRegistrations()
    {
        string contentPath = CreatePackageContent(
            "legacy.package",
            "1.0.0",
            ("legacy.json", """{"resourceType":"Patient","id":"legacy"}"""));
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        _ = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        managed.RegisterPersistedIndex(
            new PackageReference(
                "managed.package",
                "1.0.0"),
            CreateIndex(
                ("managed.json", "Patient", "managed")));

        managed.FindManagedByResourceType("Patient")
            .Select(resource => resource.Id)
            .ShouldBe(["managed"]);

        managed.Clear();

        managed.FindManagedResources(
                new ResourceSearchCriteria())
            .ShouldBeEmpty();
        indexer.FindByResourceType("Patient")
            .Select(resource => resource.Id)
            .ShouldBe(["legacy"]);
    }

    [Fact]
    public async Task PublicSearch_ReplacesDuplicateIdentityAndPrefersManagedRegistration()
    {
        string firstPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"first"}"""));
        string secondPath = Path.Combine(
            _testRoot,
            "alternate",
            "example.package#1.0.0",
            "package");
        Directory.CreateDirectory(secondPath);
        File.WriteAllText(
            Path.Combine(
                secondPath,
                "package.json"),
            """{"name":"example.package","version":"1.0.0"}""");
        File.WriteAllText(
            Path.Combine(
                secondPath,
                "patient.json"),
            """{"resourceType":"Patient","id":"second"}""");
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;

        _ = await indexer.IndexPackageAsync(
            firstPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        _ = await indexer.IndexPackageAsync(
            secondPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        indexer.FindByResourceType(
                "Patient",
                "example.package#1.0.0")
            .Select(resource => resource.Id)
            .ShouldBe(["second"]);

        managed.RegisterPersistedIndex(
            new PackageReference(
                "example.package",
                "1.0.0"),
            CreateIndex(
                ("patient.json", "Patient", "managed")));

        indexer.FindByResourceType(
                "Patient",
                "example.package#1.0.0")
            .Select(resource => resource.Id)
            .ShouldBe(["managed"]);
    }

    [Fact]
    public void ManagedRegistration_InvalidStructureIsRejected()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageIndex invalid = new()
        {
            IndexVersion = 1,
            Files = []
        };

        Should.Throw<InvalidDataException>(
            () => managed.RegisterPersistedIndex(
                new PackageReference("example.package", "1.0.0"),
                invalid));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private string CreatePackageContent(
        string name,
        string version,
        params (string Filename, string Json)[] resources)
    {
        string contentPath = Path.Combine(
            _testRoot,
            $"{name}#{version}",
            "package");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(
            Path.Combine(contentPath, "package.json"),
            $$"""{"name":"{{name}}","version":"{{version}}"}""");
        foreach ((string filename, string json) in resources)
        {
            File.WriteAllText(
                Path.Combine(contentPath, filename),
                json);
        }

        return contentPath;
    }

    private static PackageRecord CreateRecord(
        PackageReference reference,
        string contentPath) =>
        new()
        {
            Reference = reference,
            DirectoryPath = Directory.GetParent(contentPath)!.FullName,
            ContentPath = contentPath,
            Manifest = new PackageManifest
            {
                Name = reference.Name,
                Version = reference.Version!
            }
        };

    private static PackageIndex CreateIndex(
        params (string Filename, string ResourceType, string Id)[] entries) =>
        new()
        {
            IndexVersion = 2,
            Files = entries
                .Select(entry => new ResourceIndexEntry
                {
                    Filename = entry.Filename,
                    ResourceType = entry.ResourceType,
                    Id = entry.Id,
                    Url = $"https://example/{entry.Id}"
                })
                .ToList()
        };
}
