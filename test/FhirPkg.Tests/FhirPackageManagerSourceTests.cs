// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public sealed class FhirPackageManagerSourceTests
{
    [Fact]
    public async Task ExpectedStream_UsesCurrentPositionAndLeavesCallerStreamOpen()
    {
        using TestDirectory directory = new();
        await using MemoryStream archive = CreatePackageArchive(
            "example.package",
            "1.0.0");
        byte[] archiveBytes = archive.ToArray();
        TrackingStream source = new([
            0x10,
            0x20,
            0x30,
            .. archiveBytes
        ]);
        source.Position = 3;
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RejectingHttpMessageHandler());

        PackageRecord record = await manager.InstallAsync(
            new PackageReference("example.package", "1.0.0"),
            source,
            options: null,
            TestContext.Current.CancellationToken);

        record.Manifest.Name.ShouldBe("example.package");
        record.Manifest.Version.ShouldBe("1.0.0");
        source.DisposeCount.ShouldBe(0);
        source.CanRead.ShouldBeTrue();
        source.Dispose();
    }

    [Fact]
    public async Task ImportStream_DiscoversIdentityAndLeavesCallerStreamOpen()
    {
        using TestDirectory directory = new();
        TrackingStream source = new(
            CreatePackageArchive("discovered.package", "2.3.4").ToArray());
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RejectingHttpMessageHandler());

        PackageRecord record = await manager.ImportAsync(
            source,
            options: null,
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(
            new PackageReference("discovered.package", "2.3.4"));
        source.DisposeCount.ShouldBe(0);
        source.Dispose();
    }

    [Fact]
    public async Task ExpectedUri_UsesConfiguredHttpClientAndReportedLength()
    {
        using TestDirectory directory = new();
        byte[] archive = CreatePackageArchive(
            "uri.package",
            "1.2.3").ToArray();
        RecordingHttpMessageHandler handler = new(archive);
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler);

        PackageRecord record = await manager.InstallAsync(
            new PackageReference("uri.package", "1.2.3"),
            new Uri("https://packages.example.test/uri.package.tgz"),
            options: null,
            TestContext.Current.CancellationToken);

        record.Reference.Version.ShouldBe("1.2.3");
        handler.RequestCount.ShouldBe(1);
        handler.LastRequestUri.ShouldBe(
            new Uri("https://packages.example.test/uri.package.tgz"));
    }

    [Fact]
    public async Task ImportUri_DiscoversIdentity()
    {
        using TestDirectory directory = new();
        byte[] archive = CreatePackageArchive(
            "uri.discovery",
            "4.5.6").ToArray();
        RecordingHttpMessageHandler handler = new(archive);
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler);

        PackageRecord record = await manager.ImportAsync(
            new Uri("https://packages.example.test/discovery.tgz"),
            options: null,
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(
            new PackageReference("uri.discovery", "4.5.6"));
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task UriSource_FollowsRedirectsWithRedirectDisabledTransport()
    {
        using TestDirectory directory = new();
        byte[] archive = CreatePackageArchive(
            "redirected.package",
            "1.0.0").ToArray();
        RedirectingHttpMessageHandler handler = new(archive);
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler);

        PackageRecord record = await manager.ImportAsync(
            new Uri("https://packages.example.test/start.tgz"),
            options: null,
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(
            new PackageReference("redirected.package", "1.0.0"));
        handler.RequestUris.ShouldBe(
        [
            new Uri("https://packages.example.test/start.tgz"),
            new Uri("https://packages.example.test/files/package.tgz")
        ]);
    }

    [Theory]
    [InlineData("file:///package.tgz")]
    [InlineData("ftp://packages.example.test/package.tgz")]
    [InlineData("/relative/package.tgz")]
    public async Task UriSource_RejectsUnsupportedUriBeforeNetworkAccess(
        string value)
    {
        using TestDirectory directory = new();
        RejectingHttpMessageHandler handler = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler);
        Uri uri = new Uri(value, UriKind.RelativeOrAbsolute);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    uri,
                    options: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DownloadFailed);
        exception.Stage.ShouldBe(
            PackageInstallStage.Acquisition);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExpectedChecksumMismatch_IsTypedAndLeavesStreamOpen()
    {
        using TestDirectory directory = new();
        TrackingStream source = new(
            CreatePackageArchive("checksum.package", "1.0.0").ToArray());
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RejectingHttpMessageHandler());

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    new PackageReference("checksum.package", "1.0.0"),
                    source,
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = new string('0', 64)
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ChecksumMismatch);
        exception.Stage.ShouldBe(
            PackageInstallStage.ChecksumValidation);
        source.DisposeCount.ShouldBe(0);
        source.Dispose();
    }

    [Fact]
    public async Task UnsupportedCacheCapability_FailsBeforeReadingSource()
    {
        Mock<IPackageCache> cache = new();
        FailOnReadStream source = new();
        using FhirPackageManager manager = CreateManager(
            cache.Object,
            new RejectingHttpMessageHandler());

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    new PackageReference("example.package", "1.0.0"),
                    source,
                    options: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.UnsupportedCacheCapability);
        source.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExternalHardenedCapability_ReceivesPublicCorruptionPolicy()
    {
        CapturingHardenedCache cache = new();
        RecordingHttpMessageHandler handler = new([0x1]);
        using FhirPackageManager manager = CreateManager(
            cache,
            handler);
        FailOnReadStream expectedSource = new();
        FailOnReadStream importedSource = new();
        PackageSourceInstallOptions options = new()
        {
            CorruptCacheBehavior = CorruptCacheBehavior.Strict
        };

        await manager.InstallAsync(
            new PackageReference("external.package", "1.0.0"),
            expectedSource,
            options,
            TestContext.Current.CancellationToken);
        await manager.ImportAsync(
            importedSource,
            options,
            TestContext.Current.CancellationToken);
        await manager.InstallAsync(
            new PackageReference("external.uri", "1.0.0"),
            new Uri("https://packages.example.test/expected.tgz"),
            options,
            TestContext.Current.CancellationToken);
        await manager.ImportAsync(
            new Uri("https://packages.example.test/import.tgz"),
            options,
            TestContext.Current.CancellationToken);

        cache.InstallCallCount.ShouldBe(2);
        cache.ImportCallCount.ShouldBe(2);
        cache.LastInstallOptions.ShouldNotBeNull();
        cache.LastInstallOptions!.CorruptCacheBehavior.ShouldBe(
            CorruptCacheBehavior.Strict);
        cache.LastImportOptions.ShouldNotBeNull();
        cache.LastImportOptions!.CorruptCacheBehavior.ShouldBe(
            CorruptCacheBehavior.Strict);
        expectedSource.ReadCount.ShouldBe(0);
        importedSource.ReadCount.ShouldBe(0);
        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidSourceCorruptionPolicy_FailsBeforeSourceAccess()
    {
        CapturingHardenedCache cache = new();
        RejectingHttpMessageHandler handler = new();
        using FhirPackageManager manager = CreateManager(cache, handler);
        PackageSourceInstallOptions options = new()
        {
            CorruptCacheBehavior = (CorruptCacheBehavior)int.MaxValue
        };
        FailOnReadStream source = new();

        PackageInstallException streamException =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    source,
                    options,
                    TestContext.Current.CancellationToken));
        PackageInstallException uriException =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri(
                        "https://packages.example.test/package.tgz"),
                    options,
                    TestContext.Current.CancellationToken));

        streamException.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPolicy);
        uriException.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPolicy);
        cache.InstallCallCount.ShouldBe(0);
        cache.ImportCallCount.ShouldBe(0);
        source.ReadCount.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task DefaultManagerCapability_FailsBeforeReadingSource()
    {
        IFhirPackageManager manager = new LegacyManager();
        FailOnReadStream source = new();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    source,
                    options: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.UnsupportedManagerCapability);
        source.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task DefaultCacheSummaryListing_StripsIndexAndPreservesLegacyRecord()
    {
        PackageIndex index = new()
        {
            IndexVersion = 2,
            Files = []
        };
        PackageRecord source = new()
        {
            Reference = new PackageReference(
                "legacy.cache",
                "1.0.0"),
            DirectoryPath = "cache",
            ContentPath = "cache/package",
            Manifest = new PackageManifest
            {
                Name = "legacy.cache",
                Version = "1.0.0"
            },
            Index = index,
            ContentGeneration = "generation-a"
        };
        CapturingHardenedCache concreteCache = new()
        {
            ListedRecords = [source]
        };
        IPackageCache cache = concreteCache;

        IReadOnlyList<PackageRecord> summaries =
            await cache.ListPackageSummariesAsync(
                ct: TestContext.Current.CancellationToken);
        PackageRecord summary = summaries.ShouldHaveSingleItem();

        ReferenceEquals(source, summary).ShouldBeFalse();
        summary.Index.ShouldBeNull();
        summary.ContentGeneration.ShouldBe(source.ContentGeneration);
        source.Index.ShouldBeSameAs(index);
    }

    [Fact]
    public async Task UriTimeout_CoversBodyCopyAndIsTyped()
    {
        using TestDirectory directory = new();
        BlockingHttpMessageHandler handler = new();
        RecordingProgress progress = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler,
            new FhirPackageManagerOptions
            {
                CachePath = directory.Path,
                HttpTimeout = TimeSpan.FromMilliseconds(50)
            });

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri("https://packages.example.test/slow.tgz"),
                    new PackageSourceInstallOptions
                    {
                        Progress = progress
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DownloadFailed);
        exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriHttpFailure_ReportsSingleTerminalFailure()
    {
        using TestDirectory directory = new();
        RecordingProgress progress = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new StatusHttpMessageHandler(
                HttpStatusCode.ServiceUnavailable));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri(
                        "https://packages.example.test/unavailable.tgz"),
                    new PackageSourceInstallOptions
                    {
                        Progress = progress
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DownloadFailed);
        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriIoFailure_ReportsSingleTerminalFailure()
    {
        using TestDirectory directory = new();
        RecordingProgress progress = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new ThrowingHttpMessageHandler(
                new IOException("Simulated source failure.")));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri(
                        "https://packages.example.test/io-failure.tgz"),
                    new PackageSourceInstallOptions
                    {
                        Progress = progress
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DownloadFailed);
        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriChecksumFailure_ReportsSingleTerminalFailure()
    {
        using TestDirectory directory = new();
        RecordingProgress progress = new();
        byte[] archive = CreatePackageArchive(
            "checksum.uri",
            "1.0.0").ToArray();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RecordingHttpMessageHandler(archive));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    new PackageReference("checksum.uri", "1.0.0"),
                    new Uri(
                        "https://packages.example.test/checksum.tgz"),
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = new string('0', 64),
                        Progress = progress
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ChecksumMismatch);
        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriArchiveFailure_ReportsSingleTerminalFailure()
    {
        using TestDirectory directory = new();
        RecordingProgress progress = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RecordingHttpMessageHandler(
                Encoding.UTF8.GetBytes("not an archive")));

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri(
                        "https://packages.example.test/invalid.tgz"),
                    new PackageSourceInstallOptions
                    {
                        Progress = progress
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidArchive);
        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriCallerCancellation_RemainsCancellationAndReportsFailure()
    {
        using TestDirectory directory = new();
        RecordingProgress progress = new();
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new BlockingHeadersHttpMessageHandler(),
            new FhirPackageManagerOptions
            {
                CachePath = directory.Path,
                HttpTimeout = TimeSpan.FromSeconds(5)
            });
        using CancellationTokenSource cancellationSource =
            new(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => manager.ImportAsync(
                new Uri(
                    "https://packages.example.test/cancelled.tgz"),
                new PackageSourceInstallOptions
                {
                    Progress = progress
                },
                cancellationSource.Token));

        AssertSingleTerminalFailure(progress);
    }

    [Fact]
    public async Task UriReportedLengthOverLimit_IsRejectedBeforeBodyRead()
    {
        using TestDirectory directory = new();
        FailOnReadStream body = new();
        ReportedLengthHttpMessageHandler handler = new(
            body,
            contentLength: 11);
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            handler,
            new FhirPackageManagerOptions
            {
                CachePath = directory.Path,
                InstallLimits = new PackageInstallLimits
                {
                    MaxCompressedBytes = 10
                }
            });

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.ImportAsync(
                    new Uri(
                        "https://packages.example.test/oversized.tgz"),
                    options: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        body.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExpectedStream_ValidatesManifestIdentity()
    {
        using TestDirectory directory = new();
        await using MemoryStream source =
            CreatePackageArchive("actual.package", "1.0.0");
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RejectingHttpMessageHandler());

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    new PackageReference("expected.package", "1.0.0"),
                    source,
                    options: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(
            PackageInstallStage.IdentityValidation);
    }

    [Fact]
    public async Task ExpectedStream_AcceptsCorrectSha256()
    {
        using TestDirectory directory = new();
        byte[] archive = CreatePackageArchive(
            "checksum.package",
            "2.0.0").ToArray();
        string checksum = Convert.ToHexString(
                SHA256.HashData(archive))
            .ToLowerInvariant();
        await using MemoryStream source = new(archive);
        using FhirPackageManager manager = CreateManager(
            directory.Path,
            new RejectingHttpMessageHandler());

        PackageRecord record = await manager.InstallAsync(
            new PackageReference("checksum.package", "2.0.0"),
            source,
            new PackageSourceInstallOptions
            {
                ExpectedSha256 = checksum
            },
            TestContext.Current.CancellationToken);

        record.Reference.Version.ShouldBe("2.0.0");
    }

    private static MemoryStream CreatePackageArchive(
        string name,
        string version) =>
        ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package/package.json",
                $$"""{"name":"{{name}}","version":"{{version}}"}"""),
            ArbitraryTarBuilder.File(
                "package/content.txt",
                "content"));

    private static FhirPackageManager CreateManager(
        string cachePath,
        HttpMessageHandler handler,
        FhirPackageManagerOptions? options = null)
    {
        FhirPackageManagerOptions resolvedOptions =
            options ?? new FhirPackageManagerOptions
            {
                CachePath = cachePath
            };
        resolvedOptions.CachePath = cachePath;
        DiskPackageCache cache = new(cachePath);
        return CreateManager(cache, handler, resolvedOptions);
    }

    private static FhirPackageManager CreateManager(
        IPackageCache cache,
        HttpMessageHandler handler,
        FhirPackageManagerOptions? options = null)
    {
        FhirPackageManagerOptions resolvedOptions =
            options ?? new FhirPackageManagerOptions();
        HttpClient httpClient = new(handler, disposeHandler: true)
        {
            Timeout = resolvedOptions.HttpTimeout
        };
        Mock<IRegistryClient> registry = new();
        Mock<IVersionResolver> versionResolver = new();
        Mock<IDependencyResolver> dependencyResolver = new();
        Mock<IPackageIndexer> indexer = new();
        PackageInstallLimits limits =
            PackageInstallLimits.ResolveManager(
                resolvedOptions.InstallLimits);
        return FhirPackageManager.CreateWithHttpClient(
            cache,
            registry.Object,
            versionResolver.Object,
            dependencyResolver.Object,
            indexer.Object,
            resolvedOptions,
            NullLogger<FhirPackageManager>.Instance,
            memoryCache: null,
            limits,
            httpClient);
    }

    private static void AssertSingleTerminalFailure(
        RecordingProgress progress)
    {
        IReadOnlyList<PackageProgressPhase> phases = progress.Phases;
        phases.ShouldNotBeEmpty();
        phases[0].ShouldBe(PackageProgressPhase.Downloading);
        phases[^1].ShouldBe(PackageProgressPhase.Failed);
        phases.Count(
            phase => phase == PackageProgressPhase.Failed)
            .ShouldBe(1);
        phases.ShouldNotContain(PackageProgressPhase.Complete);
    }

    private sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "phase5-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TrackingStream(byte[] content) :
        MemoryStream(content)
    {
        internal int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class FailOnReadStream : Stream
    {
        internal int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override void Flush() =>
            throw new NotSupportedException();

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

    private sealed class RecordingHttpMessageHandler(byte[] content) :
        HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentLength = content.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectingHttpMessageHandler(byte[] content) :
        HttpMessageHandler
    {
        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            if (RequestUris.Count == 1)
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        Headers =
                        {
                            Location = new Uri(
                                "/files/package.tgz",
                                UriKind.Relative)
                        }
                    });
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentLength = content.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class RejectingHttpMessageHandler :
        HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException(
                "An HTTP request was not expected.");
        }
    }

    private sealed class BlockingHttpMessageHandler :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StatusHttpMessageHandler(
        HttpStatusCode statusCode) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class ThrowingHttpMessageHandler(
        Exception exception) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class BlockingHeadersHttpMessageHandler :
        HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The cancelled request unexpectedly continued.");
        }
    }

    private sealed class ReportedLengthHttpMessageHandler(
        Stream body,
        long contentLength) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            };
            response.Content.Headers.ContentLength = contentLength;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
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

    private sealed class RecordingProgress :
        IProgress<PackageProgress>
    {
        private readonly object _sync = new();
        private readonly List<PackageProgressPhase> _phases = [];

        internal IReadOnlyList<PackageProgressPhase> Phases
        {
            get
            {
                lock (_sync)
                    return _phases.ToArray();
            }
        }

        public void Report(PackageProgress value)
        {
            lock (_sync)
                _phases.Add(value.Phase);
        }
    }

    private sealed class CapturingHardenedCache :
        IHardenedPackageCache
    {
        internal int InstallCallCount { get; private set; }
        internal int ImportCallCount { get; private set; }
        internal InstallCacheOptions? LastInstallOptions { get; private set; }
        internal InstallCacheOptions? LastImportOptions { get; private set; }
        internal IReadOnlyList<PackageRecord> ListedRecords { get; init; } = [];

        public string CacheDirectory => string.Empty;

        public Task<HardenedPackageCacheInspection> InspectAsync(
            PackageReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardenedPackageCacheInspection
            {
                State = HardenedPackageCacheState.Missing
            });

        public Task<PackageRecord> InstallAsync(
            PackageReference reference,
            Stream tarballStream,
            InstallCacheOptions? options = null,
            CancellationToken ct = default)
        {
            InstallCallCount++;
            LastInstallOptions = options;
            return Task.FromResult(CreateRecord(reference));
        }

        public Task<PackageRecord> ImportAsync(
            Stream tarballStream,
            InstallCacheOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ImportCallCount++;
            LastImportOptions = options;
            return Task.FromResult(CreateRecord(
                new PackageReference(
                    "external.import",
                    "1.0.0")));
        }

        public Task<bool> IsInstalledAsync(
            PackageReference reference,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<PackageRecord?> GetPackageAsync(
            PackageReference reference,
            CancellationToken ct = default) =>
            Task.FromResult<PackageRecord?>(null);

        public Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
            string? packageIdFilter = null,
            string? versionFilter = null,
            CancellationToken ct = default) =>
            Task.FromResult(ListedRecords);

        public Task<bool> RemoveAsync(
            PackageReference reference,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<int> ClearAsync(
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<PackageManifest?> ReadManifestAsync(
            PackageReference reference,
            CancellationToken ct = default) =>
            Task.FromResult<PackageManifest?>(null);

        public Task<PackageIndex?> GetIndexAsync(
            PackageReference reference,
            CancellationToken ct = default) =>
            Task.FromResult<PackageIndex?>(null);

        public Task<string?> GetFileContentAsync(
            PackageReference reference,
            string relativePath,
            CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public string? GetPackageContentPath(
            PackageReference reference) => null;

        public Task<CacheMetadata> GetMetadataAsync(
            CancellationToken ct = default) =>
            Task.FromResult(new CacheMetadata());

        public Task UpdateMetadataAsync(
            PackageReference reference,
            CacheMetadataEntry entry,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }

        private static PackageRecord CreateRecord(
            PackageReference reference) =>
            new()
            {
                Reference = reference,
                DirectoryPath = string.Empty,
                ContentPath = string.Empty,
                Manifest = new PackageManifest
                {
                    Name = reference.Name,
                    Version = reference.Version ?? "1.0.0"
                }
            };
    }

    private sealed class LegacyManager : IFhirPackageManager
    {
        public Task<PackageRecord?> InstallAsync(
            string directive,
            InstallOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
            IEnumerable<string> directives,
            InstallOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageClosure> RestoreAsync(
            string projectPath,
            RestoreOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PackageRecord>> ListCachedAsync(
            string? filter = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(
            string directive,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CleanCacheAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogEntry>> SearchAsync(
            PackageSearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageListing?> GetPackageListingAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResolvedDirective?> ResolveAsync(
            string directive,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(
            string tarballPath,
            RegistryEndpoint registry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
