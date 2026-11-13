// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public class PackageInstallLimitsTests : IDisposable
{
    private static readonly string[] s_environmentVariables =
    [
        PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
        PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
        PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
        PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
        PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
        PackageInstallLimits.MaxArchiveDepthEnvironmentVariable
    ];

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public PackageInstallLimitsTests()
    {
        try
        {
            foreach (string variableName in s_environmentVariables)
            {
                _originalEnvironment[variableName] =
                    Environment.GetEnvironmentVariable(variableName);
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach ((string variableName, string? value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(variableName, value);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Defaults_AreDocumentedFiniteValues()
    {
        PackageInstallLimits limits = new PackageInstallLimits();

        PackageInstallLimits.DefaultMaxCompressedBytes.ShouldBe(100L * 1024 * 1024);
        PackageInstallLimits.DefaultMaxExpandedBytes.ShouldBe(1L * 1024 * 1024 * 1024);
        PackageInstallLimits.DefaultMaxEntryBytes.ShouldBe(128L * 1024 * 1024);
        PackageInstallLimits.DefaultMaxArchiveEntries.ShouldBe(50_000);
        PackageInstallLimits.DefaultMaxArchivePathLength.ShouldBe(1_024);
        PackageInstallLimits.DefaultMaxArchiveDepth.ShouldBe(32);
        limits.MaxCompressedBytes.ShouldBe(PackageInstallLimits.DefaultMaxCompressedBytes);
        limits.MaxExpandedBytes.ShouldBe(PackageInstallLimits.DefaultMaxExpandedBytes);
        limits.MaxEntryBytes.ShouldBe(PackageInstallLimits.DefaultMaxEntryBytes);
        limits.MaxArchiveEntries.ShouldBe(PackageInstallLimits.DefaultMaxArchiveEntries);
        limits.MaxArchivePathLength.ShouldBe(PackageInstallLimits.DefaultMaxArchivePathLength);
        limits.MaxArchiveDepth.ShouldBe(PackageInstallLimits.DefaultMaxArchiveDepth);
    }

    [Fact]
    public void FromEnvironment_ParsesEveryLimit()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            "101");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
            "1001");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
            "501");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
            "41");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
            "301");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveDepthEnvironmentVariable,
            "21");

        PackageInstallLimits limits = PackageInstallLimits.FromEnvironment();

        limits.MaxCompressedBytes.ShouldBe(101);
        limits.MaxExpandedBytes.ShouldBe(1001);
        limits.MaxEntryBytes.ShouldBe(501);
        limits.MaxArchiveEntries.ShouldBe(41);
        limits.MaxArchivePathLength.ShouldBe(301);
        limits.MaxArchiveDepth.ShouldBe(21);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-number")]
    public void FromEnvironment_InvalidLong_ThrowsTypedPolicyError(string value)
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            value);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            PackageInstallLimits.FromEnvironment);

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    [Fact]
    public void Validate_RejectsContradictoryAndOverflowProneValues()
    {
        PackageInstallLimits contradictory = new PackageInstallLimits
        {
            MaxExpandedBytes = 10,
            MaxEntryBytes = 11
        };
        PackageInstallLimits overflowProne = new PackageInstallLimits
        {
            MaxCompressedBytes = long.MaxValue,
            MaxExpandedBytes = long.MaxValue,
            MaxEntryBytes = 1
        };

        Should.Throw<PackageInstallException>(contradictory.Validate)
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        Should.Throw<PackageInstallException>(overflowProne.Validate)
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
    }

    [Fact]
    public void ManagerConstruction_ExplicitValueOverridesMalformedEnvironment()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            "malformed");
        FhirPackageManagerOptions options = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 64
            }
        };

        using FhirPackageManager manager = CreateManager(options);
    }

    [Fact]
    public void ManagerConstruction_InvalidEnvironmentFailsBeforeSourceAccess()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
            "0");

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => CreateManager(new FhirPackageManagerOptions()));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
    }

    [Fact]
    public void CacheConstruction_InvalidEnvironmentFailsBeforeCreatingCacheRoot()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveDepthEnvironmentVariable,
            "invalid");
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-policy-{Guid.NewGuid():N}");

        try
        {
            Should.Throw<PackageInstallException>(() => new DiskPackageCache(cacheRoot));
            Directory.Exists(cacheRoot).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PerCallLimits_CannotLoosenManagerPolicyBeforeResolution()
    {
        Mock<IRegistryClient> registry = new Mock<IRegistryClient>();
        FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 10
            }
        };
        using FhirPackageManager manager = CreateManager(managerOptions, registry);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                new InstallOptions
                {
                    InstallLimits = new PackageInstallLimits
                    {
                        MaxCompressedBytes = 11
                    }
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        registry.Verify(
            client => client.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PerCallLimits_TightenCompressedAcquisition()
    {
        Mock<IHardenedPackageCache> cache =
            new Mock<IHardenedPackageCache>();
        Mock<IRegistryClient> registry = new Mock<IRegistryClient>();
        ResolvedDirective resolved = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };
        registry.Setup(client => client.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        registry.Setup(client => client.DownloadAsync(
                resolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3, 4]),
                ContentType = "application/gzip",
                ContentLength = 4
            });
        cache.Setup(instance => instance.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        cache.Setup(instance => instance.InspectAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HardenedPackageCacheInspection
            {
                State = HardenedPackageCacheState.Missing
            });
        cache.Setup(instance => instance.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.Is<InstallCacheOptions?>(options =>
                    options != null
                    && options.Limits != null
                    && options.Limits.MaxCompressedBytes == 3),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PackageInstallException(
                PackageInstallErrorCode.CompressedSizeLimitExceeded,
                PackageInstallStage.Acquisition,
                "Compressed package exceeds the configured limit.",
                resolved.Reference.FhirDirective));

        FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 10
            }
        };
        using FhirPackageManager manager = CreateManager(
            managerOptions,
            registry,
            cache.Object);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                new InstallOptions
                {
                    InstallLimits = new PackageInstallLimits
                    {
                        MaxCompressedBytes = 3
                    }
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CompressedSizeLimitExceeded);
        cache.Verify(instance => instance.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public static IEnumerable<object[]> CompletePolicyCases()
    {
        foreach (string field in DirectPolicyTestSupport.Fields.Append("all"))
        {
            yield return [field, false];
            yield return [field, true];
        }
    }

    public static IEnumerable<object[]> PartialPolicyCases()
    {
        foreach (string field in DirectPolicyTestSupport.Fields)
        {
            foreach (string mode in new[] { "explicit", "environment", "default", "unresolved-invalid" })
                yield return [field, mode];
        }
    }

    public static IEnumerable<object[]> InvalidPolicyCases()
    {
        foreach (string invalid in DirectPolicyTestSupport.InvalidCases())
        {
            yield return [invalid, false];
            yield return [invalid, true];
        }
    }

    public static IEnumerable<object[]> PolicyFields() =>
        DirectPolicyTestSupport.Fields.Select(field => new object[] { field });

    [Theory]
    [MemberData(nameof(CompletePolicyCases))]
    public async Task DirectCache_CompleteExplicitPolicy_OverridesAllAmbientFields(
        string field,
        bool invalidAmbient)
    {
        DirectPolicyTestSupport fixture = new();
        foreach (string selected in field == "all" ? DirectPolicyTestSupport.Fields : [field])
        {
            DirectPolicyTestSupport.SetEnvironment(
                selected,
                invalidAmbient ? "malformed" : (fixture.Quantity(selected) - 1).ToString(CultureInfo.InvariantCulture));
        }

        string root = NewCacheRoot();
        try
        {
            using DiskPackageCache cache = PublicCache(root, fixture.MeasuredLimits());
            await fixture.AssertImportAcceptedAsync(cache);
            foreach (string selected in DirectPolicyTestSupport.Fields)
                await fixture.AssertUpwardRejectedAsync(cache, selected, fixture.Quantity(selected) + 1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(PartialPolicyCases))]
    public async Task DirectCache_PartialPolicy_UsesExplicitEnvironmentDefaultPrecedence(
        string field,
        string mode)
    {
        DirectPolicyTestSupport fixture = new();
        PackageInstallLimits limits = fixture.MeasuredLimits(except: field);
        long ceiling = fixture.Quantity(field);
        if (mode == "explicit")
        {
            DirectPolicyTestSupport.Set(limits, field, ceiling);
            DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        }
        else if (mode == "environment")
        {
            DirectPolicyTestSupport.SetEnvironment(field, ceiling.ToString(CultureInfo.InvariantCulture));
        }
        else if (mode == "unresolved-invalid")
        {
            DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        }
        else
        {
            ceiling = DirectPolicyTestSupport.Get(new PackageInstallLimits(), field);
            if (field == "entry")
                limits.MaxExpandedBytes = PackageInstallLimits.DefaultMaxExpandedBytes;
        }

        string root = NewCacheRoot();
        try
        {
            if (mode == "unresolved-invalid")
            {
                DirectPolicyTestSupport.AssertInvalidPolicy(
                    Should.Throw<PackageInstallException>(() => PublicCache(root, limits)));
                Directory.Exists(root).ShouldBeFalse();
                return;
            }

            using DiskPackageCache cache = PublicCache(root, limits);
            await fixture.AssertImportAcceptedAsync(cache, DirectPolicyTestSupport.Single(field, ceiling));
            await fixture.AssertUpwardRejectedAsync(cache, field, ceiling + 1);
            if (mode != "default")
                await fixture.AssertImportRejectedAsync(cache, field, DirectPolicyTestSupport.Single(field, ceiling - 1));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(PolicyFields))]
    public async Task DirectCache_PartialPolicy_MixesExplicitEnvironmentAndDefaults(string field)
    {
        DirectPolicyTestSupport fixture = new();
        int index = Array.IndexOf(DirectPolicyTestSupport.Fields, field);
        string ambientField = DirectPolicyTestSupport.Fields[(index + 1) % 6];
        PackageInstallLimits partial = DirectPolicyTestSupport.Single(field, fixture.Quantity(field));
        // A finite expanded ceiling also requires a compatible entry ceiling.
        if ((field == "expanded" || ambientField == "expanded") && field != "entry" && ambientField != "entry")
            partial.MaxEntryBytes = fixture.Quantity("entry");
        DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        DirectPolicyTestSupport.SetEnvironment(
            ambientField, fixture.Quantity(ambientField).ToString(CultureInfo.InvariantCulture));
        string root = NewCacheRoot();
        try
        {
            using DiskPackageCache cache = PublicCache(root, partial);
            await fixture.AssertImportAcceptedAsync(cache);
            foreach (string selected in DirectPolicyTestSupport.Fields)
            {
                long ceiling = selected == field || selected == ambientField
                    || (selected == "entry" && (field == "expanded" || ambientField == "expanded"))
                    ? fixture.Quantity(selected)
                    : DirectPolicyTestSupport.Get(new PackageInstallLimits(), selected);
                await fixture.AssertUpwardRejectedAsync(cache, selected, ceiling + 1);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidPolicyCases))]
    public void DirectCache_InvalidPolicy_FailsBeforeRootCreation(string invalidCase, bool invalidAmbient)
    {
        DirectPolicyTestSupport fixture = new();
        foreach (string field in DirectPolicyTestSupport.Fields)
        {
            DirectPolicyTestSupport.SetEnvironment(
                field, invalidAmbient ? "malformed" : (fixture.Quantity(field) + 1).ToString(CultureInfo.InvariantCulture));
        }
        string root = NewCacheRoot();
        try
        {
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
                () => PublicCache(root, DirectPolicyTestSupport.InvalidLimits(invalidCase)!)));
            Directory.Exists(root).ShouldBeFalse();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(PolicyFields))]
    public async Task DirectCache_PolicySnapshots_AreIndependent(string field)
    {
        DirectPolicyTestSupport fixture = new();
        PackageInstallLimits acceptedPolicy = fixture.MeasuredLimits();
        PackageInstallLimits rejectedPolicy = fixture.MeasuredLimits();
        DirectPolicyTestSupport.Set(rejectedPolicy, field, fixture.Quantity(field) - 1);
        string root = NewCacheRoot();
        try
        {
            FileSystemRetryOptions retry = DirectPolicyTestSupport.Retry();
            using DiskPackageCache accepted = new(Path.Combine(root, "accepted"), null, null, retry, acceptedPolicy);
            using DiskPackageCache rejected = new(Path.Combine(root, "rejected"), null, null, retry, rejectedPolicy);
            await fixture.AssertImportAcceptedAsync(accepted);
            await fixture.AssertImportRejectedAsync(rejected, field);
            retry.RetryCount = -1;
            accepted.FileSystemContext.Policy.RetryCount.ShouldBe(0);
            rejected.FileSystemContext.Policy.RetryCount.ShouldBe(0);

            foreach (string selected in DirectPolicyTestSupport.Fields)
            {
                DirectPolicyTestSupport.Set(acceptedPolicy, selected, 0);
                DirectPolicyTestSupport.Set(rejectedPolicy, selected, long.MaxValue);
                DirectPolicyTestSupport.SetEnvironment(selected, "malformed");
            }

            await fixture.AssertImportAcceptedAsync(accepted);
            await fixture.AssertImportRejectedAsync(rejected, field);
            await fixture.AssertImportAcceptedAsync(accepted);
            await fixture.AssertImportRejectedAsync(rejected, field);
            await fixture.AssertUpwardRejectedAsync(accepted, field, fixture.Quantity(field) + 1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(PolicyFields))]
    public async Task DirectCache_PerCallLimits_OnlyTightenConstructionPolicy(string field)
    {
        DirectPolicyTestSupport fixture = new();
        string root = NewCacheRoot();
        try
        {
            using DiskPackageCache cache = PublicCache(root, fixture.MeasuredLimits());
            await fixture.AssertImportAcceptedAsync(cache, fixture.MeasuredLimits());
            await fixture.AssertImportRejectedAsync(
                cache, field, DirectPolicyTestSupport.Single(field, fixture.Quantity(field) - 1));
            await fixture.AssertUpwardRejectedAsync(cache, field, fixture.Quantity(field) + 1);
            await fixture.AssertImportAcceptedAsync(cache);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DirectCache_HigherFiniteConstructionPolicy_IsSupported()
    {
        DirectPolicyTestSupport fixture = new();
        PackageInstallLimits higher = DirectPolicyTestSupport.HigherLimits();
        string root = NewCacheRoot();
        try
        {
            using DiskPackageCache cache = PublicCache(root, higher);
            await fixture.AssertImportAcceptedAsync(cache, higher);
            foreach (string field in DirectPolicyTestSupport.Fields)
                await fixture.AssertUpwardRejectedAsync(cache, field, DirectPolicyTestSupport.Get(higher, field) + 1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DirectCache_RetrySnapshot_IgnoresOverriddenAndLaterAmbientValues()
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new(includeRetry: true);
        foreach (string variable in DirectPolicyTestSupport.RetryEnvironmentVariables)
            Environment.SetEnvironmentVariable(variable, "malformed");
        string root = NewCacheRoot();
        try
        {
            DirectPolicyTestSupport fixture = new();
            FileSystemRetryOptions retry = DirectPolicyTestSupport.Retry();
            using DiskPackageCache cache = new(root, null, null, retry, fixture.MeasuredLimits());
            retry.RetryCount = -1;
            retry.MinimumDelayMilliseconds = -1;
            retry.MaximumJitterMilliseconds = -1;
            foreach (string variable in DirectPolicyTestSupport.RetryEnvironmentVariables)
                Environment.SetEnvironmentVariable(variable, "-1");
            await fixture.AssertImportAcceptedAsync(cache);
            cache.FileSystemContext.Policy.RetryCount.ShouldBe(0);
            cache.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(0);
            cache.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(0);
            string invalidRoot = Path.Combine(root, "invalid");
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
                () => new DiskPackageCache(invalidRoot, null, null, new FileSystemRetryOptions(), fixture.MeasuredLimits())));
            Directory.Exists(invalidRoot).ShouldBeFalse();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("count")]
    [InlineData("delay")]
    [InlineData("jitter")]
    [InlineData("overflow")]
    public void DirectCache_RetryValidation_PrecedesRootCreation(string invalidCase)
    {
        FileSystemRetryOptions? retry = DirectPolicyTestSupport.InvalidRetry(invalidCase);
        string root = NewCacheRoot();
        try
        {
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
                () => new DiskPackageCache(root, null, null, retry!, new DirectPolicyTestSupport().MeasuredLimits())));
            Directory.Exists(root).ShouldBeFalse();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static DiskPackageCache PublicCache(string root, PackageInstallLimits limits) =>
        new(root, null, null, DirectPolicyTestSupport.Retry(), limits);

    private static string NewCacheRoot() =>
        Path.Combine(Path.GetTempPath(), $"fhirpkg-policy-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static FhirPackageManager CreateManager(
        FhirPackageManagerOptions options,
        Mock<IRegistryClient>? registry = null,
        IPackageCache? cache = null)
    {
        Mock<IRegistryClient> effectiveRegistry = registry ?? new Mock<IRegistryClient>();

        return new FhirPackageManager(
            cache ?? new Mock<IPackageCache>().Object,
            effectiveRegistry.Object,
            new Mock<IVersionResolver>().Object,
            new Mock<IDependencyResolver>().Object,
            new Mock<IPackageIndexer>().Object,
            options,
            NullLogger<FhirPackageManager>.Instance);
    }

    // Shared, independently authored archive specimens for public-route policy tests.
    // No private installation-policy state is inspected.
    internal sealed class DirectPolicyTestSupport
    {
        internal static readonly string[] Fields =
            ["compressed", "expanded", "entry", "entries", "path", "depth"];

        internal static readonly string[] EnvironmentVariables =
        [
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
            PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
            PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
            PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
            PackageInstallLimits.MaxArchiveDepthEnvironmentVariable
        ];

        internal static readonly string[] RetryEnvironmentVariables =
        [
            FileSystemRetryOptions.RetryCountEnvironmentVariable,
            FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
            FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable
        ];

        internal PackageReference Reference { get; } = new("example.policy", "1.0.0");
        internal byte[] Archive { get; }
        internal byte[] Manifest { get; } = Encoding.UTF8.GetBytes(
            """{"name":"example.policy","version":"1.0.0","unknown":{"keep":true}}""");
        internal byte[] Payload { get; } = Enumerable.Range(0, 512).Select(i => (byte)(i * 37)).ToArray();
        internal const string PayloadPath = "package/a/b/resource.bin";

        internal DirectPolicyTestSupport()
        {
            using MemoryStream archive = new();
            using (GZipStream gzip = new(archive, CompressionMode.Compress, leaveOpen: true))
            using (TarWriter writer = new(gzip, TarEntryFormat.Ustar, leaveOpen: true))
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/package.json")
                {
                    DataStream = new MemoryStream(Manifest)
                });
                writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "package/empty/"));
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, PayloadPath)
                {
                    DataStream = new MemoryStream(Payload)
                });
            }
            Archive = archive.ToArray();
        }

        internal long Quantity(string field) => field switch
        {
            "compressed" => Archive.LongLength,
            "expanded" => Manifest.LongLength + Payload.LongLength,
            "entry" => Payload.LongLength,
            "entries" => 3,
            "path" => PayloadPath.Length,
            "depth" => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        internal string DescribeQuantities() =>
            string.Join(", ", Fields.Select(field => $"{field}={Quantity(field)}"));

        internal PackageInstallLimits MeasuredLimits(string? except = null)
        {
            PackageInstallLimits limits = new();
            foreach (string field in Fields)
            {
                if (field != except)
                    Set(limits, field, Quantity(field));
            }
            return limits;
        }

        internal static PackageInstallLimits Single(string field, long value)
        {
            PackageInstallLimits limits = new();
            Set(limits, field, value);
            return limits;
        }

        internal static PackageInstallLimits HigherLimits()
        {
            PackageInstallLimits limits = new();
            foreach (string field in Fields)
                Set(limits, field, Get(limits, field) + 1);
            return limits;
        }

        internal static long Get(PackageInstallLimits limits, string field) => field switch
        {
            "compressed" => limits.MaxCompressedBytes,
            "expanded" => limits.MaxExpandedBytes,
            "entry" => limits.MaxEntryBytes,
            "entries" => limits.MaxArchiveEntries,
            "path" => limits.MaxArchivePathLength,
            "depth" => limits.MaxArchiveDepth,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        internal static void Set(PackageInstallLimits limits, string field, long value)
        {
            switch (field)
            {
                case "compressed": limits.MaxCompressedBytes = value; break;
                case "expanded": limits.MaxExpandedBytes = value; break;
                case "entry": limits.MaxEntryBytes = value; break;
                case "entries": limits.MaxArchiveEntries = unchecked((int)value); break;
                case "path": limits.MaxArchivePathLength = unchecked((int)value); break;
                case "depth": limits.MaxArchiveDepth = unchecked((int)value); break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }

        internal static void SetEnvironment(string field, string? value) =>
            Environment.SetEnvironmentVariable(EnvironmentVariables[Array.IndexOf(Fields, field)], value);

        internal static IEnumerable<string> InvalidCases()
        {
            foreach (string field in Fields)
            {
                yield return $"{field}:0";
                yield return $"{field}:-1";
            }
            yield return "null";
            yield return "contradictory";
            yield return "sum-overflow";
            yield return "product-overflow";
        }

        internal static PackageInstallLimits? InvalidLimits(string invalidCase)
        {
            if (invalidCase == "null")
                return null;
            PackageInstallLimits limits = new DirectPolicyTestSupport().MeasuredLimits();
            switch (invalidCase)
            {
                case "contradictory": limits.MaxEntryBytes = limits.MaxExpandedBytes + 1; break;
                case "sum-overflow": limits.MaxCompressedBytes = long.MaxValue; break;
                case "product-overflow":
                    limits.MaxCompressedBytes = 1;
                    limits.MaxExpandedBytes = long.MaxValue - 1;
                    limits.MaxEntryBytes = long.MaxValue / 2;
                    limits.MaxArchiveEntries = 3;
                    break;
                default:
                    string[] parts = invalidCase.Split(':');
                    Set(limits, parts[0], long.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
            }
            return limits;
        }

        internal static FileSystemRetryOptions Retry() => new()
        {
            RetryCount = 0,
            MinimumDelayMilliseconds = 0,
            MaximumJitterMilliseconds = 0
        };

        internal static FileSystemRetryOptions? InvalidRetry(string invalidCase)
        {
            if (invalidCase == "null")
                return null;
            FileSystemRetryOptions retry = Retry();
            switch (invalidCase)
            {
                case "count": retry.RetryCount = -1; break;
                case "delay": retry.MinimumDelayMilliseconds = -1; break;
                case "jitter": retry.MaximumJitterMilliseconds = -1; break;
                case "overflow":
                    retry.MinimumDelayMilliseconds = int.MaxValue;
                    retry.MaximumJitterMilliseconds = 1;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(invalidCase));
            }
            return retry;
        }

        internal static PackageInstallErrorCode Error(string field) => field switch
        {
            "compressed" => PackageInstallErrorCode.CompressedSizeLimitExceeded,
            "expanded" => PackageInstallErrorCode.ExpandedSizeLimitExceeded,
            "entry" => PackageInstallErrorCode.EntrySizeLimitExceeded,
            "entries" => PackageInstallErrorCode.ArchiveEntryCountLimitExceeded,
            "path" => PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
            "depth" => PackageInstallErrorCode.ArchiveDepthLimitExceeded,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        internal static void AssertInvalidPolicy(PackageInstallException exception)
        {
            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
            exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
        }

        internal async Task AssertImportAcceptedAsync(DiskPackageCache cache, PackageInstallLimits? perCall = null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"Native import accepted: {DescribeQuantities()}");
            using MemoryStream source = new(Archive);
            PackageRecord record = await cache.ImportAsync(source, new InstallCacheOptions
            {
                Unmodified = true,
                Limits = perCall
            }, TestContext.Current.CancellationToken);
            source.CanRead.ShouldBeTrue();
            source.ToArray().ShouldBe(Archive);
            record.Reference.ShouldBe(Reference);
            record.SizeBytes.ShouldBe(Quantity("expanded"));
            AssertTree(record.DirectoryPath);
            AssertClean(cache.CacheDirectory);
        }

        internal async Task AssertImportRejectedAsync(
            DiskPackageCache cache, string field, PackageInstallLimits? perCall = null)
        {
            string target = Path.Combine(cache.CacheDirectory, Reference.FhirDirective);
            bool existed = Directory.Exists(target);
            using MemoryStream source = new(Archive);
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => cache.ImportAsync(source, new InstallCacheOptions
                {
                    Limits = perCall,
                    OverwriteExisting = true,
                    Unmodified = true
                }, TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(Error(field));
            exception.Stage.ShouldBe(field == "compressed"
                ? PackageInstallStage.Acquisition
                : PackageInstallStage.ArchiveValidation);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Native import rejected: {field} quantity={Quantity(field)}, ceiling={Quantity(field) - 1}");
            source.CanRead.ShouldBeTrue();
            source.ToArray().ShouldBe(Archive);
            Directory.Exists(target).ShouldBe(existed);
            if (existed)
                AssertTree(target);
            AssertClean(cache.CacheDirectory);
        }

        internal async Task AssertUpwardRejectedAsync(DiskPackageCache cache, string field, long value)
        {
            using MemoryStream source = new(Archive);
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => cache.ImportAsync(source, new InstallCacheOptions
                {
                    Limits = Single(field, value)
                }, TestContext.Current.CancellationToken));
            AssertInvalidPolicy(exception);
            source.Position.ShouldBe(0);
            AssertClean(cache.CacheDirectory);
        }

        private void AssertTree(string root)
        {
            File.ReadAllBytes(Path.Combine(root, "package", "package.json")).ShouldBe(Manifest);
            File.ReadAllBytes(Path.Combine(root, PayloadPath.Replace('/', Path.DirectorySeparatorChar))).ShouldBe(Payload);
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order()
                .ShouldBe(new[] { "package/package.json", PayloadPath }.Order());
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order()
                .ShouldBe(new[] { "package", "package/a", "package/a/b", "package/empty" }.Order());
        }

        internal static void AssertClean(string root)
        {
            foreach (string name in new[] { "staging", "transactions", "backup", "quarantine" })
            {
                string path = Path.Combine(root, ".fhirpkg", name);
                if (Directory.Exists(path))
                    Directory.GetFileSystemEntries(path).ShouldBeEmpty();
            }
        }

        internal sealed class EnvironmentScope : IDisposable
        {
            private readonly Dictionary<string, string?> _original = [];

            internal EnvironmentScope(bool includeRetry = false)
            {
                try
                {
                    foreach (string variable in EnvironmentVariables.Concat(includeRetry ? RetryEnvironmentVariables : []))
                    {
                        _original.Add(variable, Environment.GetEnvironmentVariable(variable));
                        Environment.SetEnvironmentVariable(variable, null);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach ((string variable, string? value) in _original)
                    Environment.SetEnvironmentVariable(variable, value);
            }
        }
    }
}
