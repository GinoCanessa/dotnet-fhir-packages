// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Qualification;
using FhirPkg.Qualification.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class QualificationFixtureRegistryTests : IDisposable
{
    private const string SdkHttpClientName = "FhirPackages";
    private const string ReportFileName = "qualification-report.json";
    private const string ValidDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string UppercaseDigest =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string ShortDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde";
    private const string R6Name = "hl7.fhir.r6.core";
    private const string R6Version = "6.0.0-ballot3";
    private const string CurrentId = "r6-current";
    private const string CurrentUri =
        "https://build.fhir.org/hl7.fhir.r6.core.tgz";
    private const string CurrentMasterId = "r6-current-master";
    private const string CurrentMasterUri =
        "https://build.fhir.org/branches/master/hl7.fhir.r6.core.tgz";
    private const string RootName = "example.registry.root";
    private const string RootVersion = "1.0.0";
    private const string LeftName = "example.registry.left";
    private const string LeftVersion = "2.0.0";
    private const string RightName = "example.registry.right";
    private const string RightVersion = "3.0.0";
    private const string R4CoreId = "hl7.fhir.r4.core#4.0.1";
    private const string UsCoreId = "hl7.fhir.us.core#6.0.0";
    private const string IpsId = "hl7.fhir.uv.ips#1.1.0";

    private static readonly string s_corpusSha256 = new('c', 64);
    private static readonly DateTimeOffset s_publishedUtc =
        new(2026, 7, 1, 12, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset s_capturedUtc =
        new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);
    private static readonly Func<string, string?> s_noEnvironment =
        static _ => null;
    private static readonly byte[] s_r6Archive =
        CreateArchive(R6Name, R6Version, R6Version, []);
    private static readonly SyntheticPackage s_root = CreatePackage(
        RootName,
        RootVersion,
        "4.0.1",
        (LeftName, LeftVersion),
        (RightName, RightVersion));
    private static readonly SyntheticPackage s_left =
        CreatePackage(LeftName, LeftVersion, "4.0.1");
    private static readonly SyntheticPackage s_right =
        CreatePackage(RightName, RightVersion, "4.0.1");
    private static readonly SyntheticPackage[] s_registryPackages =
        [s_root, s_left, s_right];
    private static readonly SyntheticPackage s_r4Core =
        CreatePackage("hl7.fhir.r4.core", "4.0.1", "4.0.1");
    private static readonly SyntheticPackage s_r4bCore =
        CreatePackage("hl7.fhir.r4b.core", "4.3.0", "4.3.0");
    private static readonly SyntheticPackage s_r5Core =
        CreatePackage("hl7.fhir.r5.core", "5.0.0", "5.0.0");
    private static readonly SyntheticPackage s_terminology =
        CreatePackage("hl7.terminology", "7.2.0", "4.0.1");
    private static readonly SyntheticPackage s_ips = CreatePackage(
        "hl7.fhir.uv.ips",
        "1.1.0",
        "4.0.1",
        ("hl7.fhir.r4.core", "4.0.1"));

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-qualification-registry-{Guid.NewGuid():N}");
    private readonly string _bundleRoot;
    private readonly List<QualificationManagerFactory> _factories = [];

    public QualificationFixtureRegistryTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _bundleRoot = Path.Combine(_tempRoot, "bundle");
    }

    [Fact]
    public void UnroutedFixtureRequest_IsAStableCode()
    {
        QualificationFailureCodes.UnroutedFixtureRequest
            .ShouldBe("UnroutedFixtureRequest");
    }

    [Fact]
    public async Task Handler_ArchiveRoute_ServesTheFrozenBlob()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        using HttpClient client = CreateClient(registry.Routes, log);

        using HttpResponseMessage response = await client.GetAsync(
            s_root.SourceUri,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage capture = await client.GetAsync(
            CurrentMasterUri,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/octet-stream");
        response.Content.Headers.ContentLength
            .ShouldBe(s_root.Archive.LongLength);
        response.Content.Headers.LastModified.ShouldBe(s_publishedUtc);
        (await response.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken))
            .SequenceEqual(s_root.Archive)
            .ShouldBeTrue();
        capture.StatusCode.ShouldBe(HttpStatusCode.OK);
        capture.Content.Headers.LastModified.ShouldBe(s_capturedUtc);
        (await capture.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken))
            .SequenceEqual(s_r6Archive)
            .ShouldBeTrue();
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handler_Head_ReturnsTheGetHeadersWithoutABody()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        using HttpClient client = CreateClient(registry.Routes, log);
        string listingUri = FixtureRoutes.GetListingUri(RootName);
        using HttpResponseMessage listing = await client.GetAsync(
            listingUri,
            TestContext.Current.CancellationToken);

        using HttpResponseMessage archiveHead = await SendHeadAsync(
            client,
            s_root.SourceUri);
        using HttpResponseMessage listingHead = await SendHeadAsync(
            client,
            listingUri);

        archiveHead.StatusCode.ShouldBe(HttpStatusCode.OK);
        archiveHead.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/octet-stream");
        archiveHead.Content.Headers.ContentLength
            .ShouldBe(s_root.Archive.LongLength);
        archiveHead.Content.Headers.LastModified.ShouldBe(s_publishedUtc);
        (await archiveHead.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        listingHead.StatusCode.ShouldBe(HttpStatusCode.OK);
        listingHead.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/json");
        listingHead.Content.Headers.ContentLength
            .ShouldBe(listing.Content.Headers.ContentLength);
        listingHead.Content.Headers.LastModified.ShouldBeNull();
        (await listingHead.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handler_ListingRoute_SynthesizesTheNpmListingFromTheArchive()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        using HttpClient client = CreateClient(registry.Routes, log);

        using HttpResponseMessage response = await client.GetAsync(
            "https://packages2.fhir.org/packages/example.registry.root",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/json");
        response.Content.Headers.LastModified.ShouldBeNull();
        byte[] body = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);
        response.Content.Headers.ContentLength.ShouldBe(body.LongLength);
        Encoding.UTF8.GetString(body).ShouldBe(
            "{\"_id\":\"example.registry.root\",\"name\":\"example.registry.root\","
            + "\"dist-tags\":{\"latest\":\"1.0.0\"},"
            + "\"versions\":{\"1.0.0\":{\"name\":\"example.registry.root\",\"version\":\"1.0.0\","
            + "\"fhirVersion\":\"4.0.1\","
            + "\"dependencies\":{\"example.registry.left\":\"2.0.0\",\"example.registry.right\":\"3.0.0\"},"
            + "\"dist\":{\"tarball\":\"https://packages2.fhir.org/packages/example.registry.root/1.0.0\","
            + $"\"shasum\":\"{Sha1(s_root.Archive)}\"}}}}}}}}");
        registry.Routes.Uris
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(
            [
                CurrentMasterUri,
                CurrentUri,
                "https://packages2.fhir.org/packages/example.registry.left",
                "https://packages2.fhir.org/packages/example.registry.left/2.0.0",
                "https://packages2.fhir.org/packages/example.registry.right",
                "https://packages2.fhir.org/packages/example.registry.right/3.0.0",
                "https://packages2.fhir.org/packages/example.registry.root",
                "https://packages2.fhir.org/packages/example.registry.root/1.0.0"
            ]);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handler_ListingRoute_FollowsWhatEachManifestDeclares()
    {
        SyntheticPackage bare = new(
            "example.registry.bare",
            "1.0.0",
            CreateArchiveFromManifest(
                """{"name":"example.registry.bare","version":"1.0.0"}"""));
        SyntheticPackage singular = new(
            "example.registry.singular",
            "1.0.0",
            CreateArchiveFromManifest(
                """{"name":"example.registry.singular","version":"1.0.0","fhirVersion":"4.3.0"}"""));
        SyntheticPackage newer =
            CreatePackage(LeftName, "10.0.0", "5.0.0");
        FixtureRegistry registry = await CreateRegistryAsync(
            [bare, singular, s_left, newer]);
        FixtureRequestLog log = new();
        using HttpClient client = CreateClient(registry.Routes, log);

        JsonObject bareVersion = (await GetListingAsync(client, bare.Name))
            ["versions"]![bare.Version]!.AsObject();
        JsonObject singularVersion =
            (await GetListingAsync(client, singular.Name))
                ["versions"]![singular.Version]!.AsObject();
        JsonObject leftListing = await GetListingAsync(client, LeftName);

        bareVersion.Select(property => property.Key)
            .ToArray()
            .ShouldBe(["name", "version", "dist"]);
        singularVersion["fhirVersion"]!.GetValue<string>()
            .ShouldBe("4.3.0");
        singularVersion.ContainsKey("dependencies").ShouldBeFalse();
        leftListing["dist-tags"]!["latest"]!.GetValue<string>()
            .ShouldBe("10.0.0");
        JsonObject versions = leftListing["versions"]!.AsObject();
        versions.Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(["10.0.0", "2.0.0"]);
        versions["10.0.0"]!["fhirVersion"]!.GetValue<string>()
            .ShouldBe("5.0.0");
        versions["10.0.0"]!["dist"]!["tarball"]!.GetValue<string>()
            .ShouldBe(newer.SourceUri);
        versions["2.0.0"]!["dist"]!["shasum"]!.GetValue<string>()
            .ShouldBe(Sha1(s_left.Archive));
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handler_UnroutedRequests_AreRecordedAndAnswered404()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        using HttpClient client = CreateClient(registry.Routes, log);
        using HttpRequestMessage post = new(
            HttpMethod.Post,
            s_root.SourceUri)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };

        using HttpResponseMessage missing = await client.GetAsync(
            "https://packages2.fhir.org/packages/example.registry.missing",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage posted = await client.SendAsync(
            post,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage otherHost = await SendHeadAsync(
            client,
            "https://packages.fhir.org/example.registry.root");
        using HttpResponseMessage search = await client.GetAsync(
            "https://packages2.fhir.org/packages/catalog?op=find&name=example",
            TestContext.Current.CancellationToken);

        foreach (HttpResponseMessage response in
            new[] { missing, posted, otherHost, search })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadAsByteArrayAsync(
                    TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
        }

        log.Drain().ToArray().ShouldBe(
        [
            "GET https://packages2.fhir.org/packages/example.registry.missing",
            "POST https://packages2.fhir.org/packages/example.registry.root/1.0.0",
            "HEAD https://packages.fhir.org/example.registry.root",
            "GET https://packages2.fhir.org/packages/catalog?op=find&name=example"
        ]);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handler_Dispose_LeavesTheSharedRoutesAndLogIntact()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        using (HttpClient first = CreateClient(registry.Routes, log))
        {
            using HttpResponseMessage missing = await first.GetAsync(
                "https://packages2.fhir.org/packages/example.registry.missing",
                TestContext.Current.CancellationToken);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using HttpClient second = CreateClient(registry.Routes, log);
        using HttpResponseMessage response = await second.GetAsync(
            s_left.SourceUri,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        log.Drain().ToArray().ShouldBe(
        [
            "GET https://packages2.fhir.org/packages/example.registry.missing"
        ]);
    }

    [Fact]
    public async Task DirectiveInstall_SecondaryRegistryWithChecksums_UsesTheSynthesizedListing()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);
        using FhirPackageManager manager = CreateManager(
            factory,
            CreateSecondaryOptions("directive"));
        QualificationProgress progress = new();

        PackageRecord? record = await manager.InstallAsync(
            s_root.Id,
            new InstallOptions
            {
                Progress = progress
            },
            TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record!.Reference.FhirDirective.ShouldBe(s_root.Id);
        record.Manifest.Name.ShouldBe(RootName);
        record.Manifest.Version.ShouldBe(RootVersion);
        progress.Phases.ShouldContain(PackageProgressPhase.Resolving);
        progress.Phases.ShouldContain(PackageProgressPhase.Downloading);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task DirectiveInstall_BlobReplacedAfterTheListing_FailsTheListingShasum()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        await File.WriteAllBytesAsync(
            registry.Bundle.GetBlobPath(s_root.Id),
            CreateArchive(
                RootName,
                RootVersion,
                "4.0.1",
                [(LeftName, LeftVersion), (RightName, RightVersion)],
                marker: "replaced"),
            TestContext.Current.CancellationToken);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);
        using FhirPackageManager verifying = CreateManager(
            factory,
            CreateSecondaryOptions("verifying"));
        using FhirPackageManager trusting = CreateManager(
            factory,
            CreateSecondaryOptions("trusting", verifyChecksums: false));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => verifying.InstallAsync(
                    s_root.Id,
                    options: null,
                    TestContext.Current.CancellationToken));
        PackageRecord? trusted = await trusting.InstallAsync(
            s_root.Id,
            options: null,
            TestContext.Current.CancellationToken);

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ChecksumMismatch);
        exception.Message.ShouldContain("SHA-1");
        trusted.ShouldNotBeNull();
        trusted!.Manifest.Version.ShouldBe(RootVersion);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task UriSources_InstallAndImport_ReadTheFrozenArchives()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);
        QualificationProgress progress = new();
        PackageRecord installed;
        PackageRecord imported;

        using (FhirPackageManager manager = CreateManager(
            factory,
            CreateDefaultChainOptions("uri-expected")))
        {
            installed = await manager.InstallAsync(
                new PackageReference(LeftName, LeftVersion),
                new Uri(s_left.SourceUri),
                new PackageSourceInstallOptions
                {
                    ExpectedSha256 = Sha256(s_left.Archive),
                    Progress = progress
                },
                TestContext.Current.CancellationToken);
        }

        using (FhirPackageManager manager = CreateManager(
            factory,
            CreateDefaultChainOptions("uri-discovery")))
        {
            imported = await manager.ImportAsync(
                new Uri(s_right.SourceUri),
                new PackageSourceInstallOptions
                {
                    ExpectedSha256 = Sha256(s_right.Archive)
                },
                TestContext.Current.CancellationToken);
        }

        installed.Reference.FhirDirective.ShouldBe(s_left.Id);
        imported.Reference.FhirDirective.ShouldBe(s_right.Id);
        progress.Phases.First().ShouldBe(PackageProgressPhase.Downloading);
        progress.Phases.Count(
                phase => phase == PackageProgressPhase.Complete)
            .ShouldBe(1);
        progress.Phases.ShouldNotContain(PackageProgressPhase.Failed);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Restore_ProjectDependingOnTheRoot_ResolvesExactlyTheSyntheticClosure()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);
        using FhirPackageManager manager = CreateManager(
            factory,
            CreateSecondaryOptions(
                "restore",
                verifyChecksums: false,
                maxParallelRegistryQueries: 1));
        string projectPath = Path.Combine(_tempRoot, "restore-project");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(
            Path.Combine(projectPath, "package.json"),
            $$"""
            {
              "name": "local.registry.restore",
              "version": "1.0.0",
              "dependencies": {
                "{{RootName}}": "{{RootVersion}}"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        PackageClosure closure = await manager.RestoreAsync(
            projectPath,
            cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> cached = await manager.ListCachedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        string[] expected = new[] { s_left.Id, s_right.Id, s_root.Id }
            .Order(StringComparer.Ordinal)
            .ToArray();
        closure.Missing.ShouldBeEmpty();
        closure.Resolved.Values
            .Select(reference => reference.FhirDirective)
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(expected);
        cached.Select(record => record.Reference.FhirDirective)
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(expected);
        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task CiAliases_DefaultChain_ResolveToTheCapturedBlobs()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);

        foreach (string selector in new[] { "current", "current$master" })
        {
            using FhirPackageManager manager = CreateManager(
                factory,
                CreateDefaultChainOptions(
                    $"alias-{selector.Replace('$', '-')}"));
            QualificationProgress progress = new();
            PackageReference expected = new(R6Name, selector);

            PackageRecord? record = await manager.InstallAsync(
                expected.FhirDirective,
                new InstallOptions
                {
                    Progress = progress
                },
                TestContext.Current.CancellationToken);

            record.ShouldNotBeNull();
            record!.Reference.ShouldBe(expected);
            record.Manifest.Name.ShouldBe(R6Name);
            record.Manifest.Version.ShouldBe(R6Version);
            progress.Phases.ShouldContain(PackageProgressPhase.Resolving);
            progress.Phases.ShouldContain(PackageProgressPhase.Downloading);
        }

        log.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownPackage_SecondaryRegistry_IsRecordedAsUnrouted()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FixtureRequestLog log = new();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, log);
        using FhirPackageManager manager = CreateManager(
            factory,
            CreateSecondaryOptions("unknown"));

        PackageRecord? record = await manager.InstallAsync(
            "unknown.package#1.0.0",
            options: null,
            TestContext.Current.CancellationToken);

        record.ShouldBeNull();
        log.Drain().ToArray().ShouldBe(
        [
            "GET https://packages2.fhir.org/packages/unknown.package",
            "GET https://packages2.fhir.org/packages/unknown.package"
        ]);
    }

    [Fact]
    public async Task PostConfigureAll_ReplacesThePrimaryHandlerTheSdkRegisters()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        FhirPackageManagerOptions options =
            CreateSecondaryOptions("override");
        ServiceCollection services = new();
        services.AddSingleton(options);
        services.AddFhirPackageManagement();
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, new FixtureRequestLog());

        using (ServiceProvider sdkOnly = services.BuildServiceProvider())
        {
            GetPrimaryHandler(sdkOnly).ShouldBeOfType<HttpClientHandler>();
        }

        using ServiceProvider overridden =
            factory.CreateServiceProvider(options);
        GetPrimaryHandler(overridden)
            .ShouldBeOfType<FixtureRegistryHandler>();
        overridden.GetRequiredService<IFhirPackageManager>()
            .ShouldBeOfType<FhirPackageManager>();
    }

    [Fact]
    public async Task DisposeTracked_DisposesEveryContainerBuiltManager()
    {
        FixtureRegistry registry =
            await CreateRegistryAsync(s_registryPackages);
        QualificationManagerFactory factory =
            CreateFactory(registry.Routes, new FixtureRequestLog());
        FhirPackageManager first = CreateManager(
            factory,
            CreateSecondaryOptions("tracked-first"));
        FhirPackageManager second = CreateManager(
            factory,
            CreateDefaultChainOptions("tracked-second"));

        factory.DisposeTracked();
        factory.DisposeTracked();

        foreach (FhirPackageManager manager in new[] { first, second })
        {
            await Should.ThrowAsync<ObjectDisposedException>(
                () => manager.ListCachedAsync(
                    cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void LiveFactory_BuildsManagersWithoutAContainer()
    {
        QualificationManagerFactory factory = new();
        FhirPackageManagerOptions options =
            CreateDefaultChainOptions("live");

        using (FhirPackageManager manager =
            factory.CreateRegistryManager(options))
        {
            manager.ShouldNotBeNull();
        }

        Should.Throw<QualificationInvariantException>(
                () => factory.CreateServiceProvider(options))
            .Message.ShouldBe(
                "Live qualification builds no fixture registry container.");
        factory.DisposeTracked();
    }

    [Fact]
    public async Task RunBundleAsync_DigestMismatch_StopsBeforeAcquisition()
    {
        FixtureRegistry registry = await CreateRegistryAsync(
            CreateHarnessPackages(),
            CreateHarnessClosure());
        string otherDigest = new('d', 64);
        QualificationRunner runner = CreateBundleRunner(
            registry,
            otherDigest);

        QualificationReport report = await runner.RunBundleAsync(
            _bundleRoot,
            otherDigest,
            TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        report.Artifacts.ShouldBeEmpty();
        QualificationCaseResult verify =
            report.Cases.ShouldHaveSingleItem();
        verify.Id.ShouldBe("bundle-verify");
        verify.Success.ShouldBeFalse();
        verify.Details.ShouldBeEmpty();
        QualificationFailure failure =
            verify.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        failure.Message.ShouldBe(
            $"The bundle manifest SHA-256 was {registry.Bundle.Digest}, not the expected {otherDigest}.");
        Directory.Exists(Path.Combine(_tempRoot, "cache", ".qualification"))
            .ShouldBeFalse();
        AssertReportMatchesSchema(report);
    }

    [Fact]
    public async Task RunBundleAsync_HarnessShapedBundle_QualifiesEveryNetworkCaseOffline()
    {
        FixtureRegistry registry = await CreateRegistryAsync(
            CreateHarnessPackages(),
            CreateHarnessClosure());
        AssertFixtureHandlerIsPrimary(
            CreateFactory(registry.Routes, new FixtureRequestLog()),
            CreateDefaultChainOptions("guard"));
        QualificationRunner runner = CreateBundleRunner(
            registry,
            registry.Bundle.Digest);

        QualificationReport report = await runner.RunBundleAsync(
            _bundleRoot,
            registry.Bundle.Digest,
            TestContext.Current.CancellationToken);

        report.Cases.Select(result => result.Id).ToArray().ShouldBe(
        [
            "bundle-verify",
            "01-immutable-expected-stream",
            "02-directive-exact",
            "03-uri-sources",
            "04-discovery-stream-checksum",
            "05-dependencies-closure",
            "06-downstream-direct-streams",
            "07-mutable-aliases",
            "08-dev-local-only",
            "09-cancellation",
            "10-corrupt-cache",
            "11-in-process-concurrency",
            "12-process-host"
        ]);
        report.Cases[0].Details
            .Select(detail => $"{detail.Name}={detail.Value}")
            .ToArray()
            .ShouldBe(
            [
                "approvals=none",
                $"bundleSha256={registry.Bundle.Digest}",
                $"corpusSha256={s_corpusSha256}"
            ]);
        DescribeFailedCases(report, "12-process-host").ShouldBeEmpty();
        report.Cases.Single(result => result.Id == "12-process-host")
            .Failures.ShouldHaveSingleItem()
            .Message.ShouldBe("The configured process host does not exist.");
        report.Artifacts.Count.ShouldBe(9);
        report.Artifacts.ShouldAllBe(artifact => artifact.Success);
        foreach (QualificationArtifactResult artifact in report.Artifacts
            .Where(artifact => artifact.Kind != "local"))
        {
            artifact.FinalUri.ShouldBe(artifact.SourceUri);
            artifact.PublicationUtc.ShouldBe(
                (artifact.Kind == "mutable" ? s_capturedUtc : s_publishedUtc)
                    .ToString("O"));
        }

        AssertReportMatchesSchema(report);
        QualificationSummaryWriter.Render(
                report,
                "qualify-windows-latest-net10.0",
                ReportFileName)
            .Markdown.ShouldContain(
                $"\nQualification inputs: bundle sha256:{registry.Bundle.Digest} \u00b7 corpus {s_corpusSha256} \u00b7 approvals: none\n");
    }

    [Fact]
    public async Task RunBundleAsync_UnpinnedDependency_FailsOnlyThatCaseWithUnroutedRequests()
    {
        FixtureRegistry registry = await CreateRegistryAsync(
            CreateHarnessPackages(("example.unpinned", "1.0.0")),
            CreateHarnessClosure());
        AssertFixtureHandlerIsPrimary(
            CreateFactory(registry.Routes, new FixtureRequestLog()),
            CreateDefaultChainOptions("guard"));
        QualificationRunner runner = CreateBundleRunner(
            registry,
            registry.Bundle.Digest);

        QualificationReport report = await runner.RunBundleAsync(
            _bundleRoot,
            registry.Bundle.Digest,
            TestContext.Current.CancellationToken);

        DescribeFailedCases(
                report,
                "05-dependencies-closure",
                "12-process-host")
            .ShouldBeEmpty();
        QualificationCaseResult closure = report.Cases.Single(
            result => result.Id == "05-dependencies-closure");
        closure.Success.ShouldBeFalse();
        QualificationFailure unrouted = closure.Failures.Single(
            failure => failure.Code
                == QualificationFailureCodes.UnroutedFixtureRequest);
        unrouted.ExceptionType.ShouldBe(
            nameof(QualificationInvariantException));
        unrouted.Message.ShouldBe(
            "Unrouted fixture request(s): GET https://packages2.fhir.org/packages/example.unpinned");
        report.Cases
            .Where(result => result.Id != "05-dependencies-closure")
            .SelectMany(result => result.Failures)
            .ShouldNotContain(failure => failure.Code
                == QualificationFailureCodes.UnroutedFixtureRequest);
        AssertReportMatchesSchema(report);
    }

    [Fact]
    public void Parse_BundleAndDigest_AreExposed()
    {
        QualificationArguments bundle = QualificationArguments.Parse(
        [
            .. CreateRequiredArguments(),
            "--bundle",
            "fixture-bundle",
            "--bundle-sha256",
            ValidDigest
        ]);
        QualificationArguments live = QualificationArguments.Parse(
            CreateRequiredArguments());

        bundle.BundlePath.ShouldBe(Path.GetFullPath("fixture-bundle"));
        bundle.BundleSha256.ShouldBe(ValidDigest);
        bundle.ValidateOnly.ShouldBeFalse();
        bundle.FreezeBundlePath.ShouldBeNull();
        live.BundlePath.ShouldBeNull();
        live.BundleSha256.ShouldBeNull();
    }

    [Theory]
    [InlineData(
        new[] { "--bundle", "fixture-bundle" },
        "Option '--bundle-sha256' is required with '--bundle'.")]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", UppercaseDigest },
        "Option '--bundle-sha256' must be exactly 64 lowercase hexadecimal characters.")]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", ShortDigest },
        "Option '--bundle-sha256' must be exactly 64 lowercase hexadecimal characters.")]
    [InlineData(
        new[] { "--bundle", " ", "--bundle-sha256", ValidDigest },
        "Option '--bundle' requires a value.")]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", ValidDigest, "--validate-only", "true" },
        "Option '--bundle' cannot be combined with '--validate-only true'.")]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", ValidDigest, "--freeze-bundle", "frozen" },
        "Option '--bundle' cannot be combined with '--freeze-bundle'.")]
    [InlineData(
        new[] { "--bundle-sha256", ValidDigest },
        "Option '--bundle-sha256' requires '--bundle'.")]
    public void Parse_InvalidBundleOptions_AreRejected(
        string[] options,
        string message)
    {
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => QualificationArguments.Parse(
                [.. CreateRequiredArguments(), .. options]));

        exception.Message.ShouldBe(message);
    }

    public void Dispose()
    {
        foreach (QualificationManagerFactory factory in _factories)
            factory.DisposeTracked();

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    private static string Sha1(byte[] content) =>
        Convert.ToHexString(SHA1.HashData(content))
            .ToLowerInvariant();

    private static SyntheticPackage CreatePackage(
        string name,
        string version,
        string fhirVersion,
        params (string Name, string Version)[] dependencies) =>
        new(
            name,
            version,
            CreateArchive(name, version, fhirVersion, dependencies));

    private static byte[] CreateArchive(
        string name,
        string version,
        string fhirVersion,
        IReadOnlyList<(string Name, string Version)> dependencies,
        string marker = "synthetic") =>
        CreateArchiveFromManifest(
            JsonSerializer.Serialize(new
            {
                name,
                version,
                fhirVersions = new[] { fhirVersion },
                dependencies = dependencies.ToDictionary(
                    dependency => dependency.Name,
                    dependency => dependency.Version,
                    StringComparer.Ordinal)
            }),
            marker);

    private static byte[] CreateArchiveFromManifest(
        string manifest,
        string marker = "synthetic")
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", manifest),
            ArbitraryTarBuilder.File("package/marker.txt", marker));
        return archive.ToArray();
    }

    private static SyntheticPackage[] CreateHarnessPackages(
        params (string Name, string Version)[] extraUsCoreDependencies) =>
    [
        s_r4Core,
        s_r4bCore,
        s_r5Core,
        s_terminology,
        CreatePackage(
            "hl7.fhir.us.core",
            "6.0.0",
            "4.0.1",
            [("hl7.fhir.r4.core", "4.0.1"), .. extraUsCoreDependencies]),
        s_ips
    ];

    private static DownstreamClosureDefinition CreateHarnessClosure() =>
        new()
        {
            Roots = [UsCoreId, IpsId],
            ResolverMembers = [R4CoreId, UsCoreId, IpsId],
            RequiredMembers = [R4CoreId, UsCoreId, IpsId]
        };

    private static HttpClient CreateClient(
        FixtureRoutes routes,
        FixtureRequestLog log) =>
        new(
            new FixtureRegistryHandler(routes, log),
            disposeHandler: true);

    private static Task<HttpResponseMessage> SendHeadAsync(
        HttpClient client,
        string uri)
    {
        HttpRequestMessage request = new(HttpMethod.Head, uri);
        return client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
    }

    private static async Task<JsonObject> GetListingAsync(
        HttpClient client,
        string name)
    {
        using HttpResponseMessage response = await client.GetAsync(
            FixtureRoutes.GetListingUri(name),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(
                await response.Content.ReadAsByteArrayAsync(
                    TestContext.Current.CancellationToken))!
            .AsObject();
    }

    private static HttpMessageHandler GetPrimaryHandler(
        IServiceProvider provider)
    {
        HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(SdkHttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler
                ?? throw new InvalidOperationException(
                    "A delegating handler had no inner handler.");
        }

        return handler;
    }

    // Proves, without sending a request, that the SDK client a manager
    // would use ends in the fixture handler, so no test can reach a host.
    private static void AssertFixtureHandlerIsPrimary(
        QualificationManagerFactory factory,
        FhirPackageManagerOptions options)
    {
        using ServiceProvider provider =
            factory.CreateServiceProvider(options);
        GetPrimaryHandler(provider).ShouldBeOfType<FixtureRegistryHandler>();
    }

    private static FhirPackageManager CreateManager(
        QualificationManagerFactory factory,
        FhirPackageManagerOptions options)
    {
        AssertFixtureHandlerIsPrimary(factory, options);
        return factory.CreateRegistryManager(options);
    }

    private static string[] DescribeFailedCases(
        QualificationReport report,
        params string[] expectedFailures) =>
        report.Cases
            .Where(result => !result.Success
                && !expectedFailures.Contains(result.Id))
            .Select(result =>
                $"{result.Id}: {string.Join(" | ", result.Failures.Select(failure => $"{failure.Code} {failure.Message}"))}")
            .ToArray();

    private static void AssertReportMatchesSchema(
        QualificationReport report) =>
        QualificationSchemaValidator.ValidateJson(
            JsonSerializer.Serialize(
                report,
                QualificationJson.SerializerOptions),
            QualificationSchemaPaths.Report);

    private QualificationManagerFactory CreateFactory(
        FixtureRoutes routes,
        FixtureRequestLog log)
    {
        QualificationManagerFactory factory = new(routes, log);
        _factories.Add(factory);
        return factory;
    }

    private string[] CreateRequiredArguments() =>
    [
        "--cache",
        Path.Combine(_tempRoot, "cache"),
        "--output",
        Path.Combine(_tempRoot, ReportFileName)
    ];

    private string CreateCachePath(string name)
    {
        string path = Path.Combine(_tempRoot, "caches", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private FhirPackageManagerOptions CreateSecondaryOptions(
        string cacheName,
        bool verifyChecksums = true,
        int maxParallelRegistryQueries = 3)
    {
        FhirPackageManagerOptions options = new()
        {
            CachePath = CreateCachePath(cacheName),
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            HttpTimeout = TimeSpan.FromSeconds(30),
            MaxParallelRegistryQueries = maxParallelRegistryQueries,
            ResourceCacheSize = 0,
            VerifyChecksums = verifyChecksums
        };
        options.Registries.Add(RegistryEndpoint.FhirSecondary);
        return options;
    }

    private FhirPackageManagerOptions CreateDefaultChainOptions(
        string cacheName) =>
        new()
        {
            CachePath = CreateCachePath(cacheName),
            CorruptCacheBehavior = CorruptCacheBehavior.Repair,
            HttpTimeout = TimeSpan.FromSeconds(30),
            MaxParallelRegistryQueries = 3,
            ResourceCacheSize = 0
        };

    private QualificationRunner CreateBundleRunner(
        FixtureRegistry registry,
        string bundleSha256) =>
        new(
            QualificationArguments.Parse(
            [
                .. CreateRequiredArguments(),
                "--bundle",
                _bundleRoot,
                "--bundle-sha256",
                bundleSha256,
                "--http-timeout-seconds",
                "30",
                "--process-host",
                Path.Combine(_tempRoot, "missing-process-host.dll")
            ]),
            registry.Corpus,
            new QualificationBuildSnapshot(
                "source",
                RequestedPackageVersion: null,
                PackageVersion: null,
                FhirPkgAssemblyVersion: "1.0.0.0",
                FhirPkgInformationalVersion: null),
            new ReportSanitizer([_tempRoot]),
            DateTimeOffset.UnixEpoch,
            s_corpusSha256);

    private async Task<FixtureRegistry> CreateRegistryAsync(
        IReadOnlyList<SyntheticPackage> packages,
        DownstreamClosureDefinition? closure = null)
    {
        QualificationCorpus repositoryCorpus =
            await QualificationCorpus.LoadAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "qualification-corpus.json"),
                TestContext.Current.CancellationToken);
        QualificationCorpus corpus = new()
        {
            SchemaVersion = 1,
            ImmutableArtifacts = packages
                .Select(package => new ImmutableArtifactDefinition
                {
                    Name = package.Name,
                    Version = package.Version,
                    Uri = package.SourceUri,
                    Sha256 = Sha256(package.Archive)
                })
                .ToArray(),
            MutableArtifacts =
            [
                new MutableArtifactDefinition
                {
                    Id = CurrentId,
                    Name = R6Name,
                    Selector = "current",
                    Uri = CurrentUri
                },
                new MutableArtifactDefinition
                {
                    Id = CurrentMasterId,
                    Name = R6Name,
                    Selector = "current$master",
                    Uri = CurrentMasterUri
                }
            ],
            LocalFixture = repositoryCorpus.LocalFixture,
            DownstreamClosure = closure ?? new DownstreamClosureDefinition
            {
                Roots = [],
                ResolverMembers = [],
                RequiredMembers = []
            }
        };
        Dictionary<string, DownloadedArtifact> artifacts =
            new(StringComparer.Ordinal);
        foreach (SyntheticPackage package in packages)
        {
            artifacts[package.Id] = await CreateDownloadedArtifactAsync(
                package.Id,
                package.Archive,
                s_publishedUtc);
        }

        artifacts[CurrentId] = await CreateDownloadedArtifactAsync(
            CurrentId,
            s_r6Archive,
            s_capturedUtc);
        artifacts[CurrentMasterId] = await CreateDownloadedArtifactAsync(
            CurrentMasterId,
            s_r6Archive,
            s_capturedUtc);
        string digest = await FixtureBundleWriter.WriteFrozenAsync(
            _bundleRoot,
            corpus,
            s_corpusSha256,
            artifacts,
            s_noEnvironment,
            TestContext.Current.CancellationToken);
        VerifiedFixtureBundle bundle =
            await FixtureBundleVerifier.VerifyAsync(
                _bundleRoot,
                digest,
                corpus,
                s_corpusSha256,
                TestContext.Current.CancellationToken);
        FixtureRoutes routes = await FixtureRoutes.CreateAsync(
            bundle,
            TestContext.Current.CancellationToken);
        return new FixtureRegistry(corpus, bundle, routes);
    }

    private async Task<DownloadedArtifact> CreateDownloadedArtifactAsync(
        string id,
        byte[] content,
        DateTimeOffset lastModified)
    {
        string directory = Path.Combine(_tempRoot, "downloads");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tgz");
        await File.WriteAllBytesAsync(
            path,
            content,
            TestContext.Current.CancellationToken);
        ArchiveMetrics metrics = await ArchiveMetricsInspector.InspectAsync(
            path,
            TestContext.Current.CancellationToken);
        return new DownloadedArtifact(
            id,
            path,
            Sha256(content),
            content.LongLength,
            metrics,
            FinalUri: null,
            PublicationDate: lastModified);
    }

    private sealed record SyntheticPackage(
        string Name,
        string Version,
        byte[] Archive)
    {
        internal string Id => $"{Name}#{Version}";

        internal string SourceUri =>
            $"https://packages2.fhir.org/packages/{Name}/{Version}";
    }

    private sealed record FixtureRegistry(
        QualificationCorpus Corpus,
        VerifiedFixtureBundle Bundle,
        FixtureRoutes Routes);
}
