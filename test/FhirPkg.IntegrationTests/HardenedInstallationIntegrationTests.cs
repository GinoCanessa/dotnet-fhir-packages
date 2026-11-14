// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirPkg.Cli;
using FhirPkg.Cli.Commands;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[CollectionDefinition("HardenedInstallationEnvironment", DisableParallelization = true)]
public sealed class HardenedInstallationEnvironmentCollection
{
}

[Collection("HardenedInstallationEnvironment")]
[Trait("Category", "Integration")]
public sealed class HardenedInstallationIntegrationTests
{
    private const int CancellationPayloadSize = 128 * 1024;

    public static TheoryData<string, int> NativeLimitBoundaries
    {
        get
        {
            TheoryData<string, int> cases = [];
            foreach (string limit in new[] { "compressed", "expanded", "entry", "entries", "path", "depth" })
            {
                foreach (int offset in new[] { 1, 0, -1 })
                    cases.Add(limit, offset);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(NativeLimitBoundaries))]
    public async Task DirectCache_ConfiguredLimits_BelowAtAbove_UseNativeValidation(string limit, int offset)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        PackageReference reference = new("boundary.package", "1.0.0");
        byte[] bytes = CreateBoundaryArchive(reference);
        ArchivePayload payload = await ReadArchivePayloadAsync(bytes);
        NativeQuantities quantities = await MeasureNativeArchiveAsync(bytes);
        quantities.Entries.ShouldBe(5); // Three regular files and two explicit directory entries.
        quantities.Expanded.ShouldBe(payload.Files.Values.Sum(content => content.LongLength));
        quantities.Entry.ShouldBe(4096);
        long quantity = quantities.Get(limit);
        PackageInstallLimits limits = NativeLimits();
        SetNativeLimit(limits, limit, quantity + offset);
        using DiskPackageCache cache = CreateExplicitCache(workspace.CachePath, limits);
        TreeSnapshot before = SnapshotNativeTree(workspace.CachePath);
        using MemoryStream source = new(bytes);
        InstallCacheOptions options = NativeOptions(bytes);
        WriteNativeOutput($"boundary {limit}: q={quantity}, ceiling={quantity + offset}; {quantities}");

        if (offset < 0)
        {
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => cache.ImportAsync(source, options, TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(NativeLimitError(limit));
            exception.Stage.ShouldBe(limit == "compressed"
                ? PackageInstallStage.Acquisition : PackageInstallStage.ArchiveValidation);
            AssertNativeTree(workspace.CachePath, before);
            AssertSourceOperationArtifactsClean(workspace.CachePath);
            (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken)).ShouldBeFalse();
        }
        else
        {
            PackageRecord record = await cache.ImportAsync(source, options, TestContext.Current.CancellationToken);
            AssertSourcePayload(record, reference, payload, false, true, false);
            (await cache.GetMetadataAsync(TestContext.Current.CancellationToken))
                .Packages[reference.FhirDirective].ArchiveSha256.ShouldBe(Sha256(bytes));
            AssertSourceOperationArtifactsClean(workspace.CachePath);
            TreeSnapshot installed = SnapshotNativeTree(workspace.CachePath);
            using DiskPackageCache reopened = CreateExplicitCache(workspace.CachePath, limits);
            PackageRecord read = (await reopened.GetPackageAsync(reference, TestContext.Current.CancellationToken))
                .ShouldNotBeNull();
            AssertSourcePayload(read, reference, payload, false, true, false);
            AssertNativeTree(workspace.CachePath, installed);
        }
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DirectCache_MetadataEntryBudget_BelowAtAbove_PreservesPreflight(int offset)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        PackageReference reference = new("metadata.budget", "1.0.0");
        byte[] bytes = CreateMetadataBudgetArchive(reference);
        NativeQuantities quantities = await MeasureNativeArchiveAsync(bytes);
        (int hiddenCount, long hiddenBytes) = await MeasureHiddenNativeMetadataAsync(bytes);
        hiddenCount.ShouldBe(4);
        quantities.Entries.ShouldBe(2);
        quantities.Entries.ShouldBeLessThan(hiddenCount - 1);
        PackageInstallLimits limits = NativeLimits();
        limits.MaxArchiveEntries = hiddenCount + offset;
        using DiskPackageCache cache = CreateExplicitCache(workspace.CachePath, limits);
        using MemoryStream source = new(bytes);
        TreeSnapshot before = SnapshotNativeTree(workspace.CachePath);
        WriteNativeOutput($"hidden metadata q={hiddenCount}, bytes={hiddenBytes}, ceiling={limits.MaxArchiveEntries}, yielded={quantities.Entries}; {quantities}");
        if (offset < 0)
        {
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => cache.ImportAsync(source, NativeOptions(bytes), TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ArchiveEntryCountLimitExceeded);
            exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
            exception.Message.ShouldContain("hidden metadata");
            AssertNativeTree(workspace.CachePath, before);
        }
        else
        {
            PackageRecord record = await cache.ImportAsync(
                source, NativeOptions(bytes), TestContext.Current.CancellationToken);
            AssertSourcePayload(record, reference, await ReadArchivePayloadAsync(bytes), false, true, false);
        }
        AssertSourceOperationArtifactsClean(workspace.CachePath);
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task DirectCache_DefaultDepth32And33_RetainsFiniteCeiling()
    {
        using InstallationEnvironmentScope environment = new();
        PackageInstallLimits defaults = PackageInstallLimits.FromEnvironment();
        defaults.MaxCompressedBytes.ShouldBe(PackageInstallLimits.DefaultMaxCompressedBytes);
        defaults.MaxExpandedBytes.ShouldBe(PackageInstallLimits.DefaultMaxExpandedBytes);
        defaults.MaxEntryBytes.ShouldBe(PackageInstallLimits.DefaultMaxEntryBytes);
        defaults.MaxArchiveEntries.ShouldBe(PackageInstallLimits.DefaultMaxArchiveEntries);
        defaults.MaxArchivePathLength.ShouldBe(PackageInstallLimits.DefaultMaxArchivePathLength);
        defaults.MaxArchiveDepth.ShouldBe(32);
        foreach (int depth in new[] { 32, 33 })
        {
            using TestWorkspace workspace = new(useTemporaryDirectory: true);
            PackageReference reference = new("depth.package", "1.0.0");
            string path = "package/" + string.Join("/", Enumerable.Repeat("a", depth - 2)) + "/f";
            byte[] bytes = CreateRawNativeArchive(
                new RawNativeEntry("package/package.json", ManifestBytes(reference)),
                new RawNativeEntry(path, [1, 2, 3]));
            (await MeasureNativeArchiveAsync(bytes)).Depth.ShouldBe(depth);
            using (DiskPackageCache cache = new(workspace.CachePath))
            using (MemoryStream source = new(bytes))
            {
                if (depth == 32)
                {
                    PackageRecord record = await cache.ImportAsync(
                        source, NativeOptions(bytes), TestContext.Current.CancellationToken);
                    AssertSourcePayload(record, reference, await ReadArchivePayloadAsync(bytes), false, true, false);
                }
                else
                {
                    TreeSnapshot before = SnapshotNativeTree(workspace.CachePath);
                    PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                        () => cache.ImportAsync(source, NativeOptions(bytes), TestContext.Current.CancellationToken));
                    exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ArchiveDepthLimitExceeded);
                    exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
                    AssertNativeTree(workspace.CachePath, before);
                }
                AssertSourceOperationArtifactsClean(workspace.CachePath);
            }
            if (depth == 33)
            {
                PackageInstallLimits higher = NativeLimits();
                higher.MaxArchiveDepth = 33;
                using DiskPackageCache cache = CreateExplicitCache(workspace.CachePath, higher);
                using MemoryStream source = new(bytes);
                PackageRecord record = await cache.ImportAsync(
                    source, NativeOptions(bytes), TestContext.Current.CancellationToken);
                AssertSourcePayload(record, reference, await ReadArchivePayloadAsync(bytes), false, true, false);
                AssertSourceOperationArtifactsClean(workspace.CachePath);
            }
            WriteNativeOutput($"default normalized depth={depth}; explicit depth33 accepts identical bytes");
        }
    }

    private static PackageInstallLimits NativeLimits() => new()
    {
        MaxCompressedBytes = 1024 * 1024,
        MaxExpandedBytes = 32 * 1024 * 1024,
        MaxEntryBytes = 16 * 1024 * 1024,
        MaxArchiveEntries = 100,
        MaxArchivePathLength = 512,
        MaxArchiveDepth = 64,
    };

    public static TheoryData<bool, RegistryType, string, bool> EndpointLengthCases
    {
        get
        {
            TheoryData<bool, RegistryType, string, bool> cases = [];
            foreach (bool controlled in new[] { false, true })
            {
                foreach (RegistryType type in new[] { RegistryType.FhirNpm, RegistryType.Npm })
                {
                    foreach (string report in new[]
                    {
                        "accurate", "absent", "under", "over-within", "negative",
                        "over-rejected", "accurate-rejected", "absent-excess", "under-excess",
                    })
                    {
                        foreach (bool sha256 in new[] { false, true })
                            cases.Add(controlled, type, report, sha256);
                    }
                    cases.Add(controlled, type, "bad-sha1", false);
                    cases.Add(controlled, type, "bad-sha256", true);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(EndpointLengthCases))]
    public async Task EndpointDownload_NativeImport_EnforcesReportedAndActualLength(
        bool controlled, RegistryType type, string report, bool sha256)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        PackageReference reference = new("endpoint.package", "1.0.0");
        byte[] bytes = CreateBoundaryArchive(reference);
        long q = bytes.LongLength;
        long ceiling = report switch
        {
            "over-within" => q + 1,
            "accurate-rejected" or "absent-excess" or "under-excess" => q - 1,
            _ => q,
        };
        long? reported = report switch
        {
            "absent" or "absent-excess" => null,
            "under" => q - 1,
            "under-excess" => q - 2,
            "over-within" or "over-rejected" => q + 1,
            "negative" => -1,
            _ => q,
        };
        string sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        using EndpointArchiveHandler handler = new(reference, bytes, reported,
            report == "bad-sha1" ? new string('0', 40) : sha1);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = new() { Url = "https://endpoint.example/", Type = type };
        PackageInstallLimits limits = NativeLimits();
        limits.MaxCompressedBytes = ceiling;
        IRegistryClient client = controlled
            ? RegistryClientFactory.CreateClientForEndpoint(
                endpoint, RegistryHttpTransport.CreateRedirectControlled(http, TimeSpan.FromSeconds(30), 3),
                NullLoggerFactory.Instance, null, new FileSystemRetryOptions(), limits)
            : RegistryClientFactory.CreateClientForEndpoint(
                endpoint, http, NullLoggerFactory.Instance, null, new FileSystemRetryOptions(), limits);
        ResolvedDirective selected = (await client.ResolveAsync(
            PackageDirective.Parse(reference.FhirDirective), cancellationToken: TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        selected.Reference.ShouldBe(reference);
        selected.SourceRegistry!.Type.ShouldBe(type);
        selected.ShaSum.ShouldBe(report == "bad-sha1" ? new string('0', 40) : sha1);
        // These leaf listings carry SHA-1. An independently available stronger digest
        // belongs to this same selected artifact, never to another source candidate.
        selected = selected with
        {
            Sha256Sum = sha256 ? report == "bad-sha256" ? new string('0', 64) : Sha256(bytes) : null,
        };
        using DiskPackageCache cache = CreateExplicitCache(workspace.CachePath, limits);
        TreeSnapshot before = SnapshotNativeTree(workspace.CachePath);
        TrackingNativeStream responseStream = handler.Body;
        await using (PackageDownloadResult download = (await client.DownloadAsync(
            selected, TestContext.Current.CancellationToken)).ShouldNotBeNull())
        {
            download.ContentLength.ShouldBe(reported);
            responseStream.ReadCalls.ShouldBe(0, "Both clients return a response-backed stream without a download spool.");
            responseStream.Disposed.ShouldBeFalse();
            InstallCacheOptions options = new()
            {
                Unmodified = true,
                VerifyChecksum = true,
                ReportedContentLength = download.ContentLength,
                ExpectedShaSum = selected.ShaSum,
                ExpectedSha256Sum = selected.Sha256Sum,
            };
            bool preRead = report is "negative" or "over-rejected" or "accurate-rejected";
            bool actualExcess = report is "absent-excess" or "under-excess";
            bool integrity = report is "bad-sha1" or "bad-sha256";
            if (preRead || actualExcess || integrity)
            {
                PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                    () => cache.ImportAsync(download.Content, options, TestContext.Current.CancellationToken));
                exception.ErrorCode.ShouldBe(report == "negative" ? PackageInstallErrorCode.DownloadFailed
                    : integrity ? PackageInstallErrorCode.ChecksumMismatch
                    : PackageInstallErrorCode.CompressedSizeLimitExceeded);
                exception.Stage.ShouldBe(integrity
                    ? PackageInstallStage.ChecksumValidation : PackageInstallStage.Acquisition);
                if (preRead)
                    responseStream.ReadCalls.ShouldBe(0);
                else
                    responseStream.BytesRead.ShouldBeGreaterThan(0);
                AssertNativeTree(workspace.CachePath, before);
                AssertSourceOperationArtifactsClean(workspace.CachePath);
                (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken)).ShouldBeFalse();
            }
            else
            {
                PackageRecord record = await cache.ImportAsync(
                    download.Content, options, TestContext.Current.CancellationToken);
                responseStream.BytesRead.ShouldBe(q);
                AssertSourcePayload(record, reference, await ReadArchivePayloadAsync(bytes), false, true, false);
                (await cache.GetMetadataAsync(TestContext.Current.CancellationToken))
                    .Packages[reference.FhirDirective].ArchiveSha256.ShouldBe(Sha256(bytes));
            }
            responseStream.Disposed.ShouldBeFalse("Native import borrows the endpoint stream.");
            WriteNativeOutput($"endpoint {type}/{controlled}, {report}, sha256={sha256}: q={q}, report={reported}, ceiling={ceiling}, reads={responseStream.ReadCalls}, actual={responseStream.BytesRead}");
            AssertSourceOperationArtifactsClean(workspace.CachePath);
        }
        responseStream.Disposed.ShouldBeTrue("Disposing PackageDownloadResult releases the response and its stream.");
        handler.BodyContent.Disposed.ShouldBeTrue();
        handler.ListingRequests.ShouldBe(1);
        handler.ArchiveRequests.ShouldBe(1);
    }

    private sealed class EndpointArchiveHandler : HttpMessageHandler
    {
        private readonly PackageReference _reference;
        private readonly string _sha1;
        internal EndpointArchiveHandler(PackageReference reference, byte[] bytes, long? reported, string sha1)
        {
            _reference = reference;
            _sha1 = sha1;
            Body = new TrackingNativeStream(bytes);
            BodyContent = new NativeResponseContent(Body, reported);
        }
        internal TrackingNativeStream Body { get; }
        internal NativeResponseContent BodyContent { get; }
        internal int ListingRequests { get; private set; }
        internal int ArchiveRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.ShouldBe(HttpMethod.Get);
            HttpContent content;
            if (request.RequestUri!.AbsolutePath == "/" + _reference.Name)
            {
                ListingRequests++;
                content = new StringContent($$"""
                    {"name":"{{_reference.Name}}","dist-tags":{"latest":"1.0.0"},
                     "versions":{"1.0.0":{"name":"{{_reference.Name}}","version":"1.0.0",
                     "dist":{"tarball":"https://endpoint.example/archive.tgz","shasum":"{{_sha1}}"} } } }
                    """, Encoding.UTF8, "application/json");
            }
            else
            {
                request.RequestUri.AbsoluteUri.ShouldBe("https://endpoint.example/archive.tgz");
                ArchiveRequests++;
                content = BodyContent;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class NativeResponseContent : HttpContent
    {
        private readonly TrackingNativeStream _stream;
        private readonly long? _reported;
        internal NativeResponseContent(TrackingNativeStream stream, long? reported)
        {
            _stream = stream;
            _reported = reported;
            Headers.ContentType = new("application/gzip");
        }
        internal bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length)
        {
            length = _reported ?? 0;
            return _reported.HasValue;
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(_stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Endpoint download must stay response-backed, not buffer content.");
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing)
                _stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingNativeStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _blockSize;
        private readonly Action<int>? _beforeRead;
        internal TrackingNativeStream(byte[] bytes, int blockSize = 257, Action<int>? beforeRead = null)
        {
            _inner = new(bytes);
            _blockSize = blockSize;
            _beforeRead = beforeRead;
        }
        internal int ReadCalls { get; private set; }
        internal long BytesRead { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            _beforeRead?.Invoke(ReadCalls);
            int read = _inner.Read(buffer[..Math.Min(buffer.Length, _blockSize)]);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            _beforeRead?.Invoke(ReadCalls);
            cancellationToken.ThrowIfCancellationRequested();
            int read = _inner.Read(buffer.Span[..Math.Min(buffer.Length, _blockSize)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public static TheoryData<string, bool, bool> NativeFailureCases
    {
        get
        {
            TheoryData<string, bool, bool> cases = [];
            foreach (string failure in new[]
            {
                "quota-compressed", "quota-expanded", "quota-entry", "quota-entries", "quota-path", "quota-depth",
                "sha1", "sha256", "invalid-name", "invalid-version", "mismatched-name", "mismatched-version",
                "json", "repository", "date", "traversal", "link", "truncated", "ambiguous",
                "read", "cancel-acquisition", "cancel-extraction",
            })
            {
                foreach (bool import in new[] { false, true })
                {
                    if (import && failure.StartsWith("mismatched", StringComparison.Ordinal))
                        continue; // Discovery has no expected identity to mismatch.
                    foreach (bool overwrite in new[] { false, true })
                        cases.Add(failure, import, overwrite);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(NativeFailureCases))]
    public async Task DirectCache_FailedImportOrReplacement_PreservesPriorAndNeighbourTrees(
        string failure, bool import, bool overwrite)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        NativeFailureProgress progress = new(cancellation);
        using DiskPackageCache cache = failure == "cancel-extraction"
            ? new DiskPackageCache(
                workspace.CachePath, null, null, NativeLimits(),
                new FileSystemContext(
                    new FileSystemRetryOptions(),
                    new ExtractionCancellationPrimitives(progress),
                    new FileSystemRetryExecutor()))
            : CreateExplicitCache(workspace.CachePath, NativeLimits());
        PackageReference prior = new("failure.prior", "1.0.0");
        PackageReference neighbour = new("failure.neighbour", "1.0.0");
        PackageReference target = overwrite ? prior : new("failure.cold", "1.0.0");
        foreach (PackageReference seed in new[] { prior, neighbour })
        {
            using MemoryStream archive = CreatePreservationArchive(seed, false, "valid", "old");
            await cache.ImportAsync(archive, NativeOptions(archive.ToArray()), TestContext.Current.CancellationToken);
        }
        // Snapshot includes packages.ini and both whole identity trees, not just resources.
        TreeSnapshot before = SnapshotNativeTree(workspace.CachePath);
        byte[] bytes = CreateNativeFailureArchive(target, failure);
        InstallCacheOptions options = NativeOptions(bytes);
        options.OverwriteExisting = true;
        IOException readFailure = new("controlled one-shot source failure");
        using TrackingNativeStream source = new(bytes, blockSize: 17, beforeRead: call =>
        {
            if (call == 3 && failure == "read")
                throw readFailure;
            if (call == 3 && failure == "cancel-acquisition")
                cancellation.Cancel();
        });
        options.Progress = progress;
        PackageInstallErrorCode code = PackageInstallErrorCode.InvalidArchive;
        PackageInstallStage stage = PackageInstallStage.ArchiveValidation;
        if (failure.StartsWith("quota-", StringComparison.Ordinal))
        {
            string limit = failure["quota-".Length..];
            NativeQuantities quantities = await MeasureNativeArchiveAsync(bytes);
            PackageInstallLimits tighter = NativeLimits();
            SetNativeLimit(tighter, limit, quantities.Get(limit) - 1);
            options.Limits = tighter;
            code = NativeLimitError(limit);
            stage = limit == "compressed" ? PackageInstallStage.Acquisition : PackageInstallStage.ArchiveValidation;
        }
        else if (failure is "sha1" or "sha256")
        {
            options.ExpectedSha256Sum = failure == "sha256" ? new string('0', 64) : null;
            options.ExpectedShaSum = new string('0', 40);
            code = PackageInstallErrorCode.ChecksumMismatch;
            stage = PackageInstallStage.ChecksumValidation;
        }
        else if (failure.Contains("name", StringComparison.Ordinal) || failure.Contains("version", StringComparison.Ordinal))
        {
            code = PackageInstallErrorCode.InvalidPackageIdentity;
            stage = PackageInstallStage.IdentityValidation;
        }
        else if (failure == "read")
        {
            code = PackageInstallErrorCode.DownloadFailed;
            stage = PackageInstallStage.Acquisition;
        }
        Func<Task<PackageRecord>> attempt = () => import
            ? cache.ImportAsync(source, options, cancellation.Token)
            : cache.InstallAsync(target, source, options, cancellation.Token);
        if (failure.StartsWith("cancel-", StringComparison.Ordinal))
        {
            OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(attempt);
            exception.CancellationToken.ShouldBe(cancellation.Token);
            if (failure == "cancel-acquisition")
            {
                source.ReadCalls.ShouldBe(3);
                source.BytesRead.ShouldBe(34);
                progress.Phases.ShouldNotContain(PackageProgressPhase.Extracting);
            }
            else
            {
                source.BytesRead.ShouldBe(bytes.LongLength);
                progress.PartialExtractionBytes.ShouldBe(CancellationPayloadSize);
                progress.ManifestWritten.ShouldBeTrue();
                progress.LastExtractionEntryExists.ShouldBeFalse();
                progress.StagedArchiveBytes.ShouldBe(bytes.LongLength);
                progress.Phases.ShouldContain(PackageProgressPhase.Extracting);
                progress.Phases.ShouldNotContain(PackageProgressPhase.Validating);
            }
        }
        else
        {
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(attempt);
            exception.ErrorCode.ShouldBe(code);
            exception.Stage.ShouldBe(stage);
            if (failure == "read")
            {
                exception.InnerException.ShouldBeSameAs(readFailure);
                source.ReadCalls.ShouldBe(3);
                source.BytesRead.ShouldBe(34);
            }
            if (failure is "json" or "repository" or "date")
            {
                JsonException json = exception.InnerException.ShouldBeOfType<JsonException>();
                if (failure != "json")
                    json.Path.ShouldBe("$." + failure);
                progress.Phases.ShouldContain(PackageProgressPhase.Validating);
            }
            if (stage == PackageInstallStage.IdentityValidation)
                progress.Phases.ShouldContain(PackageProgressPhase.Validating);
            if (failure == "link")
                exception.Message.ShouldContain("unsupported raw entry type 0x32");
        }
        source.Disposed.ShouldBeFalse("All failed native routes leave the caller's one-shot stream open.");
        // Must precede IsInstalled/GetPackage/metadata reads, which can recover operations.
        AssertNativeTree(workspace.CachePath, before);
        AssertSourceOperationArtifactsClean(workspace.CachePath);
        if (!overwrite)
            Directory.Exists(Path.Combine(workspace.CachePath, target.FhirDirective)).ShouldBeFalse();
        WriteNativeOutput($"failure={failure}, import={import}, overwrite={overwrite}, expected={code}/{stage}, reads={source.ReadCalls}, bytes={source.BytesRead}, extraction={progress.PartialExtractionBytes}");

        using DiskPackageCache reopened = CreateExplicitCache(workspace.CachePath, NativeLimits());
        foreach (PackageReference seed in new[] { prior, neighbour })
        {
            PackageRecord record = (await reopened.GetPackageAsync(seed, TestContext.Current.CancellationToken))
                .ShouldNotBeNull();
            record.Manifest.Description.ShouldBe("old");
            (await reopened.GetFileContentAsync(seed, "resource.json", TestContext.Current.CancellationToken))
                .ShouldNotBeNull().ShouldContain("\"id\":\"old\"");
        }
        AssertNativeTree(workspace.CachePath, before);
        AssertSourceOperationArtifactsClean(workspace.CachePath);
    }

    private static byte[] CreateNativeFailureArchive(PackageReference target, string failure)
    {
        if (failure.StartsWith("quota-", StringComparison.Ordinal))
            return CreateBoundaryArchive(target);
        if (failure == "cancel-extraction")
        {
            byte[] chunk = new byte[CancellationPayloadSize];
            return CreateRawNativeArchive(
                new("package/package.json", ManifestBytes(target, ",\"description\":\"new\"")),
                new("package/cancel.bin", chunk),
                new("package/chunk-01.bin", chunk));
        }
        byte[] manifest = failure switch
        {
            "invalid-name" => ManifestBytes(new("../escape", target.Version)),
            "invalid-version" => ManifestBytes(new(target.Name, "1.x")),
            "mismatched-name" => ManifestBytes(new("failure.unexpected", target.Version)),
            "mismatched-version" => ManifestBytes(new(target.Name, "2.0.0")),
            "json" => Encoding.UTF8.GetBytes("{\"name\":"),
            "repository" => ManifestBytes(target, ",\"repository\":{\"url\":42}"),
            "date" => ManifestBytes(target, ",\"date\":20240203e0"),
            _ => ManifestBytes(target, ",\"description\":\"new\""),
        };
        RawNativeEntry extra = failure switch
        {
            "traversal" => new("../escape.bin", [1, 2]),
            "link" => new("package/link", [], (byte)'2'),
            "truncated" => new("package/truncated.bin", [1, 2, 3], DeclaredSize: 4096),
            "ambiguous" => new("package/PACKAGE.json", manifest),
            _ => new("package/new.bin", [0, 128, 255, 42]),
        };
        return CreateRawNativeArchive(new RawNativeEntry("package/package.json", manifest), extra);
    }

    private sealed class NativeFailureProgress(CancellationTokenSource caller) : IProgress<PackageProgress>
    {
        internal List<PackageProgressPhase> Phases { get; } = [];
        internal long PartialExtractionBytes { get; private set; }
        internal long StagedArchiveBytes { get; private set; }
        internal bool ManifestWritten { get; private set; }
        internal bool LastExtractionEntryExists { get; private set; }
        public void Report(PackageProgress value) => Phases.Add(value.Phase);

        internal void CancelAfterPayloadClosed(string path)
        {
            string directory = Path.GetDirectoryName(path)!;
            PartialExtractionBytes = File.ReadAllBytes(path).LongLength;
            ManifestWritten = File.ReadAllBytes(Path.Combine(directory, "package.json")).LongLength > 0;
            LastExtractionEntryExists = File.Exists(Path.Combine(directory, "chunk-01.bin"));
            string operation = Directory.GetParent(directory)!.Parent!.FullName;
            StagedArchiveBytes = Directory.GetFiles(operation, "*.tgz")
                .Select(file => new FileInfo(file).Length).Single();
            caller.Cancel();
        }
    }

    private sealed class ExtractionCancellationPrimitives(NativeFailureProgress progress) : IFileSystemPrimitives
    {
        private readonly IFileSystemPrimitives _inner = SystemFileSystemPrimitives.Instance;

        public FileSystemPlatform Platform => _inner.Platform;
        public FileSystemReplayOutcome<string> GetCurrentDirectory() => _inner.GetCurrentDirectory();
        public FileSystemReplayOutcome<string> GetTempPath() => _inner.GetTempPath();
        public FileSystemReplayOutcome<string> GetHomeDirectory() => _inner.GetHomeDirectory();
        public FileSystemReplayOutcome<string> GetFullPath(string path) => _inner.GetFullPath(path);
        public FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false) =>
            _inner.Probe(path, followLinks);
        public FileSystemReplayOutcome<FileAttributes> GetAttributes(string path) => _inner.GetAttributes(path);
        public FileSystemReplayOutcome<long> GetLength(string path) => _inner.GetLength(path);
        public FileSystemReplayOutcome<IEnumerator<string>> CreateDirectoryEnumerator(string path) =>
            _inner.CreateDirectoryEnumerator(path);
        public FileSystemReplayOutcome<bool> MoveNext(IEnumerator<string> enumerator, string path) =>
            _inner.MoveNext(enumerator, path);
        public FileSystemReplayOutcome<string> GetCurrent(IEnumerator<string> enumerator, string path) =>
            _inner.GetCurrent(enumerator, path);
        public FileSystemReplayOutcome<bool> DisposeEnumerator(IEnumerator<string> enumerator, string path) =>
            _inner.DisposeEnumerator(enumerator, path);
        public FileSystemReplayOutcome<FileStream> OpenFile(
            string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
            _inner.OpenFile(path, mode, access, share, bufferSize, options);
        public FileSystemReplayOutcome<SafeFileHandle> OpenFileHandle(
            string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
            _inner.OpenFileHandle(path, mode, access, share, options);
        public FileSystemReplayOutcome<int> OpenOwnedDescriptor(
            string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
            _inner.OpenOwnedDescriptor(path, mode, access, share, options);
        public FileSystemReplayOutcome<bool> ConfigureFileSharing(SafeFileHandle handle, FileShare share, string path) =>
            _inner.ConfigureFileSharing(handle, share, path);
        public FileSystemReplayOutcome<bool> AdviseFileAccess(SafeFileHandle handle, FileOptions options, string path) =>
            _inner.AdviseFileAccess(handle, options, path);
        public FileSystemReplayOutcome<int> ReadAt(SafeFileHandle handle, Memory<byte> buffer, long offset, string path) =>
            _inner.ReadAt(handle, buffer, offset, path);
        public ValueTask<FileSystemReplayOutcome<int>> ReadAtAsync(
            SafeFileHandle handle, Memory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
            _inner.ReadAtAsync(handle, buffer, offset, path, cancellationToken);
        public FileSystemReplayOutcome<bool> WriteAt(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path) =>
            _inner.WriteAt(handle, buffer, offset, path);
        public ValueTask<FileSystemReplayOutcome<bool>> WriteAtAsync(
            SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
            _inner.WriteAtAsync(handle, buffer, offset, path, cancellationToken);
        public FileSystemReplayOutcome<long> GetLength(SafeFileHandle handle, string path) => _inner.GetLength(handle, path);
        public FileSystemReplayOutcome<bool> SetLength(SafeFileHandle handle, long length, string path) =>
            _inner.SetLength(handle, length, path);
        public FileSystemReplayOutcome<bool> FlushFile(SafeFileHandle handle, string path) => _inner.FlushFile(handle, path);
        public FileSystemReplayOutcome<bool> CloseFile(SafeFileHandle handle, string path)
        {
            FileSystemReplayOutcome<bool> outcome = _inner.CloseFile(handle, path);
            if (outcome.Kind == FileSystemReplayOutcomeKind.Completed
                && Path.GetFileName(path) == "cancel.bin")
            {
                // The extractor cannot advance to the next entry until this close returns.
                progress.CancelAfterPayloadClosed(path);
            }
            return outcome;
        }
        public FileSystemReplayOutcome<BorrowedFileReadState> PrepareBorrowedRead(FileStream stream) =>
            _inner.PrepareBorrowedRead(stream);
        public FileSystemReplayOutcome<long> GetPosition(FileStream stream) => _inner.GetPosition(stream);
        public FileSystemReplayOutcome<long> SetPosition(FileStream stream, long position) => _inner.SetPosition(stream, position);
        public FileSystemReplayOutcome<int> ReadBorrowed(FileStream stream, Memory<byte> buffer) =>
            _inner.ReadBorrowed(stream, buffer);
        public ValueTask<FileSystemReplayOutcome<int>> ReadBorrowedAsync(
            FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
            _inner.ReadBorrowedAsync(stream, buffer, cancellationToken);
        public FileSystemReplayOutcome<bool> CreateDirectory(string path) => _inner.CreateDirectory(path);
        public FileSystemReplayOutcome<bool> MovePath(string sourcePath, string destinationPath, bool replace) =>
            _inner.MovePath(sourcePath, destinationPath, replace);
        public FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind) => _inner.DeleteEntry(path, kind);
        public FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode) =>
            _inner.OpenDescriptor(path, flags, mode);
        public FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null) =>
            _inner.InspectDescriptor(descriptor, path);
        public FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null) =>
            _inner.DuplicateDescriptor(descriptor, command, minimum, path);
        public FileSystemReplayOutcome<int> GetDescriptorFlags(int descriptor, string? path = null) =>
            _inner.GetDescriptorFlags(descriptor, path);
        public FileSystemReplayOutcome<bool> SetDescriptorFlags(int descriptor, int flags, string? path = null) =>
            _inner.SetDescriptorFlags(descriptor, flags, path);
        public FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(
            int descriptor, bool fullSync, string? path = null) =>
            _inner.SynchronizeDescriptor(descriptor, fullSync, path);
        public FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null) =>
            _inner.CloseDescriptor(descriptor, path);
        public FileSystemReplayOutcome<bool> TryLock(FileStream stream) => _inner.TryLock(stream);
        public FileSystemReplayOutcome<bool> Unlock(FileStream stream) => _inner.Unlock(stream);
    }

    public static TheoryData<string, bool, bool, string, bool> NativeMetadataSourceModes
    {
        get
        {
            TheoryData<string, bool, bool, string, bool> cases = [];
            foreach (string mode in new[] { "expected.stream", "import.stream", "expected.uri", "import.uri" })
            {
                foreach (bool rootLayout in new[] { false, true })
                {
                    foreach (bool unmodified in new[] { false, true })
                    {
                        foreach (string repository in new[] { "shorthand", "url", "object" })
                        {
                            foreach (bool numericDate in new[] { false, true })
                                cases.Add(mode, rootLayout, unmodified, repository, numericDate);
                        }
                    }
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(NativeMetadataSourceModes))]
    public async Task ManagerSourceModes_RepositoryDateMetadata_UsesSameProjection(
        string mode, bool rootLayout, bool unmodified, string repository, bool numericDate)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        PackageReference reference = new("source.metadata", "1.0.0");
        string rawRepository = repository switch
        {
            "shorthand" => "\"github:example/repo\"",
            "url" => "\"https://example.test/repo.git\"",
            "object" => """{"type":"git","url":"https://example.test/repo.git","directory":"src"}""",
            _ => throw new ArgumentException("Unknown repository specimen.", nameof(repository)),
        };
        string rawDate = numericDate ? "20240203123456" : "\"20240203123456\"";
        string members = $$"""
            ,"repository":{{rawRepository}},"date":{{rawDate}},
            "author":{"name":"Example","email":"author@example.test","url":"https://example.test/"},
            "unknown-manifest":{"opaque":[1,true,null],"spelling":"retain  bytes"}
            """;
        using MemoryStream archive = CreatePreservationArchive(
            reference, rootLayout, "valid", "metadata", members, includeXml: true);
        byte[] bytes = archive.ToArray();
        ArchivePayload payload = await ReadArchivePayloadAsync(bytes);
        NpmRepository expectedRepository = repository switch
        {
            "shorthand" => new(null, "github:example/repo", null),
            "url" => new(null, "https://example.test/repo.git", null),
            _ => new("git", "https://example.test/repo.git", "src"),
        };
        PackageSourceInstallOptions options = new()
        {
            ExpectedSha256 = Sha256(bytes),
            ExpectedSha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
        };
        using (FhirPackageManager manager = CreateSourceManager(workspace.CachePath, unmodified))
        {
            PackageRecord record = await InstallSourceAsync(manager, reference, mode, bytes, options);
            AssertNativeMetadataProjection(record.Manifest, expectedRepository);
            AssertSourcePayload(record, reference, payload, rootLayout, unmodified, false);
            await AssertNativeMetadataResourceAsync(manager, reference);
            AssertSourcePayload(record, reference, payload, rootLayout, unmodified, false);
            AssertSourceOperationArtifactsClean(workspace.CachePath);
        }
        archive.CanRead.ShouldBeTrue();
        archive.ToArray().ShouldBe(bytes);
        Sha256(archive.ToArray()).ShouldBe(options.ExpectedSha256);
        TreeSnapshot beforeReopen = SnapshotNativeTree(workspace.CachePath);
        using DiskPackageCache cache = CreateExplicitCache(workspace.CachePath, NativeLimits());
        PackageRecord read = (await cache.GetPackageAsync(reference, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        AssertNativeMetadataProjection(read.Manifest, expectedRepository);
        AssertSourcePayload(read, reference, payload, rootLayout, unmodified, false);
        (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken)).ShouldBeTrue();
        cache.GetPackageContentPath(reference).ShouldBe(read.ContentPath);
        AssertNativeMetadataProjection(
            (await cache.ReadManifestAsync(reference, TestContext.Current.CancellationToken)).ShouldNotBeNull(),
            expectedRepository);
        foreach (PackageRecord listed in await cache.ListPackagesAsync(ct: TestContext.Current.CancellationToken))
            AssertNativeMetadataProjection(listed.Manifest, expectedRepository);
        (await cache.ListPackagesAsync(ct: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        CacheMetadataEntry metadata = (await cache.GetMetadataAsync(TestContext.Current.CancellationToken))
            .Packages[reference.FhirDirective];
        metadata.ArchiveSha256.ShouldBe(options.ExpectedSha256);
        metadata.SizeBytes.ShouldBe(payload.Files.Values.Sum(content => content.LongLength));
        string prefix = rootLayout ? "" : "package/";
        (await cache.GetFileContentAsync(reference, "resource.xml", TestContext.Current.CancellationToken))
            .ShouldBe(Encoding.UTF8.GetString(payload.Files[prefix + "resource.xml"]));
        using (FhirPackageManager reopened = CreateSourceManager(workspace.CachePath, unmodified))
            await AssertNativeMetadataResourceAsync(reopened, reference);
        AssertNativeTree(workspace.CachePath, beforeReopen);
        AssertSourceOperationArtifactsClean(workspace.CachePath);
        WriteNativeOutput($"metadata native route={mode}, root={rootLayout}, unmodified={unmodified}, repository={repository}, numericDate={numericDate}, compressed={bytes.Length}, files={payload.Files.Count}, dirs={payload.Directories.Count}");
    }

    private static void AssertNativeMetadataProjection(PackageManifest manifest, NpmRepository repository)
    {
        manifest.Repository.ShouldBe(repository);
        manifest.Date.ShouldBe("20240203123456");
        manifest.Author.ShouldBe("Example <author@example.test> (https://example.test/)");
    }

    private static async Task AssertNativeMetadataResourceAsync(FhirPackageManager manager, PackageReference reference)
    {
        ResourceInfo resource = (await manager.FindByCanonicalUrlAsync(
            $"https://example.test/{reference.Name}/resource", reference.FhirDirective,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        resource.FilePath.ShouldBe("resource.json");
        resource.PackageName.ShouldBe(reference.Name);
        resource.PackageVersion.ShouldBe(reference.Version);
        (await manager.ReadResourceAsync(resource, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()["id"]!.GetValue<string>().ShouldBe("metadata");
    }

    private static DiskPackageCache CreateExplicitCache(string path, PackageInstallLimits limits) =>
        new(path, null, null, new FileSystemRetryOptions(), limits);

    private static InstallCacheOptions NativeOptions(byte[] bytes) => new()
    {
        Unmodified = true,
        VerifyChecksum = true,
        ExpectedSha256Sum = Sha256(bytes),
        ExpectedShaSum = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteNativeOutput(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    private static PackageInstallErrorCode NativeLimitError(string limit) => limit switch
    {
        "compressed" => PackageInstallErrorCode.CompressedSizeLimitExceeded,
        "expanded" => PackageInstallErrorCode.ExpandedSizeLimitExceeded,
        "entry" => PackageInstallErrorCode.EntrySizeLimitExceeded,
        "entries" => PackageInstallErrorCode.ArchiveEntryCountLimitExceeded,
        "path" => PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
        "depth" => PackageInstallErrorCode.ArchiveDepthLimitExceeded,
        _ => throw new ArgumentException("Unknown measured limit.", nameof(limit)),
    };

    private static void SetNativeLimit(PackageInstallLimits limits, string limit, long ceiling)
    {
        switch (limit)
        {
            case "compressed": limits.MaxCompressedBytes = ceiling; break;
            case "expanded": limits.MaxExpandedBytes = ceiling; break;
            case "entry": limits.MaxEntryBytes = ceiling; break;
            case "entries": limits.MaxArchiveEntries = checked((int)ceiling); break;
            case "path": limits.MaxArchivePathLength = checked((int)ceiling); break;
            case "depth": limits.MaxArchiveDepth = checked((int)ceiling); break;
            default: throw new ArgumentException("Unknown measured limit.", nameof(limit));
        }
        // The other fields stay explicitly assigned and nonbinding, including entry <= expanded.
        if (limit == "expanded")
            limits.MaxEntryBytes = ceiling;
        limits.Validate();
    }

    private sealed record NativeQuantities(long Compressed, long Expanded, long Entry, int Entries, int Path, int Depth)
    {
        internal long Get(string limit) => limit switch
        {
            "compressed" => Compressed, "expanded" => Expanded, "entry" => Entry,
            "entries" => Entries, "path" => Path, "depth" => Depth,
            _ => throw new ArgumentException("Unknown measured limit.", nameof(limit)),
        };
    }

    private static async Task<NativeQuantities> MeasureNativeArchiveAsync(byte[] bytes)
    {
        long expanded = 0;
        long largest = 0;
        int entries = 0;
        int pathLength = 0;
        int depth = 0;
        using MemoryStream source = new(bytes);
        using GZipStream gzip = new(source, CompressionMode.Decompress);
        using TarReader reader = new(gzip);
        while (await reader.GetNextEntryAsync(cancellationToken: TestContext.Current.CancellationToken) is TarEntry entry)
        {
            entries++;
            string path = entry.Name.Replace('\\', '/').TrimEnd('/');
            pathLength = Math.Max(pathLength, Math.Max(path.Length, path.Normalize(NormalizationForm.FormC).Length));
            depth = Math.Max(depth, path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
            if (entry.EntryType == TarEntryType.Directory)
                continue;
            entry.EntryType.ShouldBe(TarEntryType.RegularFile);
            using MemoryStream content = new();
            await entry.DataStream!.CopyToAsync(content, TestContext.Current.CancellationToken);
            expanded += content.Length;
            largest = Math.Max(largest, content.Length);
        }
        return new(bytes.LongLength, expanded, largest, entries, pathLength, depth);
    }

    private sealed record RawNativeEntry(
        string Name, byte[] Content, byte Type = (byte)'0', long? DeclaredSize = null, bool Gnu = false);

    private static byte[] ManifestBytes(PackageReference reference, string extra = "") =>
        Encoding.UTF8.GetBytes($$"""{"name":"{{reference.Name}}","version":"{{reference.Version}}"{{extra}}}""");

    private static byte[] CreateBoundaryArchive(PackageReference reference)
    {
        byte[] content = new byte[4096];
        new Random(111304).NextBytes(content);
        return CreateRawNativeArchive(
            new RawNativeEntry("package/", [], (byte)'5'),
            new RawNativeEntry("package/a/b/empty/", [], (byte)'5'),
            new RawNativeEntry("package/package.json", ManifestBytes(reference)),
            new RawNativeEntry("package/a/b/cccccccccccccccccccccccccccccccc/entry.bin", content),
            new RawNativeEntry("README.bin", [0, 255, 128, 13, 10]));
    }

    // Long-name plus empty long-link metadata leaves each yielded entry a normal
    // regular file, while independently consuming two hidden preflight entries.
    private static byte[] CreateMetadataBudgetArchive(PackageReference reference) =>
        CreateRawNativeArchive(
            new RawNativeEntry("././@LongLink", Encoding.ASCII.GetBytes("package/package.json\0"), (byte)'L', Gnu: true),
            new RawNativeEntry("././@LongLink", [0], (byte)'K', Gnu: true),
            new RawNativeEntry("package/package.json", ManifestBytes(reference), Gnu: true),
            new RawNativeEntry("././@LongLink", Encoding.ASCII.GetBytes("package/one.bin\0"), (byte)'L', Gnu: true),
            new RawNativeEntry("././@LongLink", [0], (byte)'K', Gnu: true),
            new RawNativeEntry("package/one.bin", [1, 2], Gnu: true));

    private static async Task<(int Count, long Bytes)> MeasureHiddenNativeMetadataAsync(byte[] bytes)
    {
        using MemoryStream source = new(bytes);
        using GZipStream gzip = new(source, CompressionMode.Decompress);
        using MemoryStream tar = new();
        await gzip.CopyToAsync(tar, TestContext.Current.CancellationToken);
        byte[] raw = tar.ToArray();
        int count = 0;
        long metadataBytes = 0;
        for (int offset = 0; offset < raw.Length; )
        {
            ReadOnlySpan<byte> header = raw.AsSpan(offset, 512);
            if (header.IndexOfAnyExcept((byte)0) < 0)
                break;
            long size = Convert.ToInt64(Encoding.ASCII.GetString(header.Slice(124, 12)).Trim('\0', ' '), 8);
            if (header[156] is (byte)'L' or (byte)'K' or (byte)'x' or (byte)'g')
            {
                count++;
                metadataBytes += size;
            }
            offset = checked(offset + 512 + (int)((size + 511) / 512 * 512));
        }
        return (count, metadataBytes);
    }

    private static byte[] CreateRawNativeArchive(params RawNativeEntry[] entries)
    {
        using MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (RawNativeEntry entry in entries)
            {
                byte[] header = new byte[512];
                Encoding.ASCII.GetBytes(entry.Name).CopyTo(header, 0);
                WriteNativeOctal(header, 100, 8, 420);
                WriteNativeOctal(header, 108, 8, 0);
                WriteNativeOctal(header, 116, 8, 0);
                WriteNativeOctal(header, 124, 12, entry.DeclaredSize ?? entry.Content.LongLength);
                WriteNativeOctal(header, 136, 12, 0);
                header.AsSpan(148, 8).Fill((byte)' ');
                header[156] = entry.Type;
                if (entry.Type == (byte)'2')
                    Encoding.ASCII.GetBytes("package.json").CopyTo(header, 157);
                Encoding.ASCII.GetBytes(entry.Gnu ? "ustar " : "ustar").CopyTo(header, 257);
                Encoding.ASCII.GetBytes(entry.Gnu ? " " : "00").CopyTo(header, 263);
                string checksum = Convert.ToString(header.Sum(value => (int)value), 8)!.PadLeft(6, '0');
                Encoding.ASCII.GetBytes(checksum).CopyTo(header, 148);
                header[154] = 0;
                header[155] = (byte)' ';
                gzip.Write(header);
                gzip.Write(entry.Content);
                if (entry.DeclaredSize is not null && entry.DeclaredSize != entry.Content.LongLength)
                    break;
                gzip.Write(new byte[(512 - entry.Content.Length % 512) % 512]);
            }
            gzip.Write(new byte[1024]);
        }
        return archive.ToArray();
    }

    private static void WriteNativeOctal(byte[] header, int offset, int length, long value) =>
        Encoding.ASCII.GetBytes(Convert.ToString(value, 8)!.PadLeft(length - 1, '0')).CopyTo(header, offset);

    private sealed record TreeSnapshot(IReadOnlyDictionary<string, byte[]> Files, IReadOnlySet<string> Directories);

    private static TreeSnapshot SnapshotNativeTree(string root)
    {
        bool Included(string path) => !Path.GetRelativePath(root, path)
            .StartsWith(".fhirpkg" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Path.GetRelativePath(root, path) != ".fhirpkg";
        return new(
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(Included).ToDictionary(
                path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal),
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(Included)
                .Select(path => Path.GetRelativePath(root, path)).ToHashSet(StringComparer.Ordinal));
    }

    private static void AssertNativeTree(string root, TreeSnapshot before)
    {
        TreeSnapshot after = SnapshotNativeTree(root);
        after.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(before.Files.Keys.Order(StringComparer.Ordinal));
        after.Directories.Order(StringComparer.Ordinal).ShouldBe(before.Directories.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in before.Files)
            after.Files[path].ShouldBe(bytes, path);
    }

    private sealed class InstallationEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);
        internal InstallationEnvironmentScope()
        {
            foreach (string name in new[]
            {
                PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
                PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
                PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
                PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
                PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
                PackageInstallLimits.MaxArchiveDepthEnvironmentVariable,
            })
            {
                _original.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, null);
            }
        }
        public void Dispose()
        {
            foreach ((string name, string? value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private const string RetainedArchiveDirectoryVariable =
        "FHIRPKG_RETAINED_CORE_ARCHIVES";
    private const string RetainedCliCacheVariable =
        "FHIRPKG_RETAINED_CORE_CLI_CACHE";
    private const string PrimaryArchiveFileName = "primary-r4-4.0.1.tgz";
    private const string PrimaryArchiveSha256 =
        "ebd7731df7d36b5b7d39d5fb6c9d77b44bb7fe5742f1a2e87f164738c3289d44";
    private const string SecondaryArchiveFileName = "secondary-r4-4.0.1.tgz";
    private const string SecondaryArchiveSha256 =
        "b090bf929e1f665cf2c91583720849695bc38d2892a7c5037c56cb00817fb091";
    private const string PatientCanonical =
        "http://hl7.org/fhir/StructureDefinition/Patient";
    private const string OpenApiCanonical =
        "https://example.test/StructureDefinition/OpenApiOnly";
    private const string OtherCanonical =
        "https://example.test/StructureDefinition/OtherOnly";
    private static readonly PackageReference s_coreReference =
        new("hl7.fhir.r4.core", "4.0.1");

    public static TheoryData<bool, bool, string> SourceLayoutModes
    {
        get
        {
            TheoryData<bool, bool, string> cases = [];
            foreach (bool rootLayout in new[] { false, true })
            {
                foreach (bool unmodified in new[] { false, true })
                {
                    foreach (string state in new[] { "valid", "missing", "invalid", "unusable", "directory" })
                        cases.Add(rootLayout, unmodified, state);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SourceLayoutModes))]
    public async Task ManagerSourceModes_InstallThroughOneValidatedContract(
        bool rootLayout,
        bool unmodified,
        string indexState)
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = CreateSourceManager(workspace.CachePath, unmodified);
        using DiskPackageCache cache = new(workspace.CachePath);
        foreach (string sourceMode in new[] { "expected.stream", "import.stream", "expected.uri", "import.uri" })
        {
            PackageReference reference = new($"source.{sourceMode}", "1.0.0");
            using MemoryStream archive = CreatePreservationArchive(reference, rootLayout, indexState, "source");
            byte[] original = archive.ToArray();
            ArchivePayload payload = await ReadArchivePayloadAsync(original);
            PackageSourceInstallOptions options = new()
            {
                ExpectedSha256 = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(),
                ExpectedSha1 = Convert.ToHexString(SHA1.HashData(original)).ToLowerInvariant(),
            };

            PackageRecord record = await InstallSourceAsync(manager, reference, sourceMode, original, options);

            AssertSourcePayload(record, reference, payload, rootLayout, unmodified,
                allowDerivedIndex: !unmodified && indexState is not "valid" and not "directory");
            CacheMetadataEntry metadata = (await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken)).Packages[reference.FhirDirective];
            metadata.ArchiveSha256.ShouldBe(options.ExpectedSha256);
            metadata.SizeBytes.ShouldBe(payload.Files.Values.Sum(bytes => bytes.LongLength));
            metadata.ContentGeneration.ShouldNotBeNullOrWhiteSpace();
            string canonical = $"https://example.test/{reference.Name}/resource";
            if (!unmodified && indexState == "directory")
            {
                record.Index.ShouldBeNull();
                await Should.ThrowAsync<IOException>(() => manager.FindByCanonicalUrlAsync(
                    canonical, reference.FhirDirective, TestContext.Current.CancellationToken));
                await Should.ThrowAsync<IOException>(() => manager.IndexPackageAsync(
                    reference, new IndexingOptions { ForceReindex = true },
                    TestContext.Current.CancellationToken));
            }
            else
            {
                record.Index.ShouldNotBeNull();
                ResourceInfo resource = (await manager.FindByCanonicalUrlAsync(
                    canonical, reference.FhirDirective, TestContext.Current.CancellationToken))
                    .ShouldNotBeNull();
                resource.PackageName.ShouldBe(reference.Name);
                resource.PackageVersion.ShouldBe(reference.Version);
                resource.FilePath.ShouldBe("resource.json");
                (await manager.ReadResourceAsync(resource, TestContext.Current.CancellationToken))!
                    ["id"]!.GetValue<string>().ShouldBe("source");
                (await manager.FindResourcesAsync(
                    new ResourceSearchCriteria { PackageScope = reference.FhirDirective },
                    TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
                PackageIndex forced = (await manager.IndexPackageAsync(
                    reference, new IndexingOptions { ForceReindex = true },
                    TestContext.Current.CancellationToken)).ShouldNotBeNull();
                forced.Files.ShouldHaveSingleItem().Filename.ShouldBe("resource.json");
            }

            AssertSourcePayload(record, reference, payload, rootLayout, unmodified,
                allowDerivedIndex: !unmodified && indexState != "directory");
            PackageIndex? onDisk = await cache.GetIndexAsync(reference, TestContext.Current.CancellationToken);
            if ((!unmodified && indexState != "directory") || indexState == "valid")
                onDisk.ShouldNotBeNull();
            else
                onDisk.ShouldBeNull();
            (await cache.GetMetadataAsync(TestContext.Current.CancellationToken))
                .Packages[reference.FhirDirective].ShouldBe(metadata);
            AssertSourceOperationArtifactsClean(workspace.CachePath);
        }

        (await cache.ListPackagesAsync(ct: TestContext.Current.CancellationToken)).Count.ShouldBe(4);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagerSourceModes_ChecksumFailure_PreservesPriorTreeAndCallerStream(
        bool rootLayout,
        bool unmodified)
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = CreateSourceManager(workspace.CachePath, unmodified);
        PackageReference reference = new("checksum.package", "1.0.0");
        using MemoryStream initial = CreatePreservationArchive(reference, rootLayout, "valid", "old");
        byte[] original = initial.ToArray();
        ArchivePayload payload = await ReadArchivePayloadAsync(original);
        PackageRecord prior = await manager.InstallAsync(
            reference, initial, options: null, TestContext.Current.CancellationToken);
        initial.CanRead.ShouldBeTrue();
        AssertSourcePayload(prior, reference, payload, rootLayout, unmodified, allowDerivedIndex: false);
        byte[] metadataBefore = File.ReadAllBytes(Path.Combine(workspace.CachePath, "packages.ini"));
        using MemoryStream replacement = CreatePreservationArchive(reference, rootLayout, "valid", "new");
        byte[] replacementBytes = replacement.ToArray();

        foreach (string sourceMode in new[] { "expected.stream", "import.stream", "expected.uri", "import.uri" })
        {
            foreach (bool sha256 in new[] { false, true })
            {
                PackageSourceInstallOptions options = new()
                {
                    OverwriteExisting = true,
                    ExpectedSha256 = sha256 ? new string('0', 64) : null,
                    ExpectedSha1 = sha256 ? null : new string('0', 40),
                };
                PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                    () => InstallSourceAsync(manager, reference, sourceMode, replacementBytes, options));
                exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ChecksumMismatch);
                exception.Stage.ShouldBe(PackageInstallStage.ChecksumValidation);

                // No SDK read may recover or clean a failed operation before these assertions.
                AssertSourcePayload(prior, reference, payload, rootLayout, unmodified, allowDerivedIndex: false);
                File.ReadAllBytes(Path.Combine(workspace.CachePath, "packages.ini")).ShouldBe(metadataBefore);
                AssertSourceOperationArtifactsClean(workspace.CachePath);
            }
        }
    }

    [Fact]
    public async Task ManagerExpectedUri_AncillaryLayout_IsUsableAfterReopen()
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await using MemoryStream archive = CreateAncillaryCoreArchive();
        CoreArchiveInventory inventory = await InspectCoreArchiveAsync(
            archive,
            hasAncillaryRoots: true);
        await using SingleResponseHttpServer server =
            await SingleResponseHttpServer.StartAsync(
                archive.ToArray(),
                TestContext.Current.CancellationToken);

        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            PackageRecord record = await manager.InstallAsync(
                s_coreReference,
                server.Uri,
                options: null,
                TestContext.Current.CancellationToken);

            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(ManagerExpectedUri_AncillaryLayout_IsUsableAfterReopen)}");
    }

    [Fact]
    public async Task ManagerImportStream_AncillaryLayout_IsUsableAfterReopen()
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await using MemoryStream archive = CreateAncillaryCoreArchive();
        CoreArchiveInventory inventory = await InspectCoreArchiveAsync(
            archive,
            hasAncillaryRoots: true);
        archive.Position = 0;

        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            PackageRecord record = await manager.ImportAsync(
                archive,
                options: null,
                TestContext.Current.CancellationToken);

            archive.CanRead.ShouldBeTrue();
            archive.Position.ShouldBe(archive.Length);
            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(ManagerImportStream_AncillaryLayout_IsUsableAfterReopen)}");
    }

    // These acceptance replays only read retained local files; they never download.
    // An absent opt-in skips explicitly, while an enabled but invalid setup fails.
    [Fact]
    public async Task RetainedR4CorePrimary_ExpectedStreamInstall_IsUsable()
    {
        await AssertRetainedCoreInstallAsync(
            PrimaryArchiveFileName,
            PrimaryArchiveSha256,
            hasAncillaryRoots: true);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CorePrimary_ExpectedStreamInstall_IsUsable)}");
    }

    [Fact]
    public async Task RetainedR4CoreSecondary_ExpectedStreamInstall_IsUsable()
    {
        await AssertRetainedCoreInstallAsync(
            SecondaryArchiveFileName,
            SecondaryArchiveSha256,
            hasAncillaryRoots: false);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CoreSecondary_ExpectedStreamInstall_IsUsable)}");
    }

    [Fact]
    public async Task RetainedR4CorePrimary_CliCache_IsUsable()
    {
        string? configuredCachePath =
            Environment.GetEnvironmentVariable(RetainedCliCacheVariable);
        Assert.SkipWhen(
            configuredCachePath is null,
            $"Set {RetainedCliCacheVariable} to an existing disposable CLI cache and " +
            $"{RetainedArchiveDirectoryVariable} to its retained archive inputs; " +
            "this verifier never installs or downloads content.");
        string cachePath = AssertCliCachePath(configuredCachePath);
        string retainedDirectory = AssertRetainedCoreArchiveDirectory(
            Environment.GetEnvironmentVariable(RetainedArchiveDirectoryVariable));
        string archivePath = Path.Combine(retainedDirectory, PrimaryArchiveFileName);
        CoreArchiveInventory inventory;
        long compressedBytes;
        await using (FileStream inspectionStream = File.OpenRead(archivePath))
        {
            compressedBytes = inspectionStream.Length;
            await AssertRetainedArchiveHashAsync(inspectionStream, PrimaryArchiveSha256);
            inspectionStream.Position = 0;
            inventory = await InspectCoreArchiveAsync(
                inspectionStream,
                hasAncillaryRoots: true);
        }

        await using (FileStream secondaryStream = File.OpenRead(
            Path.Combine(retainedDirectory, SecondaryArchiveFileName)))
        {
            await AssertRetainedArchiveHashAsync(secondaryStream, SecondaryArchiveSha256);
        }

        // The executor establishes freshness, CLI/configuration attribution and exclusive use.
        // This must be the first SDK reader: even listing can recover or clean cache state.
        AssertCliCacheEvidenceQuiescent(cachePath);
        string indexPath = Path.Combine(
            cachePath, s_coreReference.FhirDirective, "package", ".index.json");
        byte[] indexBefore = await File.ReadAllBytesAsync(
            indexPath,
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = new(cachePath);
        CacheMetadata metadataBefore = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadataBefore.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entryBefore =
            metadataBefore.Packages[s_coreReference.FhirDirective];
        entryBefore.ArchiveSha256.ShouldBe(PrimaryArchiveSha256);
        entryBefore.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));
        entryBefore.ContentGeneration.ShouldNotBeNullOrWhiteSpace();

        // Listing must expose a usable persisted index before lookup can request an index.
        await AssertCoreUsableAfterReopenAsync(cachePath, inventory);
        AssertCliCacheEvidenceQuiescent(cachePath);
        PackageRecord record = (await cache.GetPackageAsync(
            s_coreReference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        await AssertCorePayloadsAsync(record, cachePath, inventory);
        (await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken))
            .SequenceEqual(indexBefore).ShouldBeTrue(
                "The existing CLI index must not be regenerated or rewritten by verification.");
        CacheMetadata metadataAfter = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadataAfter.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entryAfter =
            metadataAfter.Packages[s_coreReference.FhirDirective];
        entryAfter.ArchiveSha256.ShouldBe(entryBefore.ArchiveSha256);
        entryAfter.ContentGeneration.ShouldBe(entryBefore.ContentGeneration);
        entryAfter.SizeBytes.ShouldBe(entryBefore.SizeBytes);
        AssertCliCacheEvidenceQuiescent(cachePath);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CorePrimary_CliCache_IsUsable)}; " +
            $"existing CLI cache: {cachePath}; retained primary: {archivePath}; " +
            $"compressed bytes: {compressedBytes}; SHA-256: {entryBefore.ArchiveSha256}; " +
            $"generation: {entryBefore.ContentGeneration}; expanded bytes: {entryBefore.SizeBytes}. " +
            "Verified complete payloads, persisted index and scoped Patient API read without " +
            "changing payload/index bytes or metadata hash/generation. The external cache is retained; " +
            "CLI freshness, effective settings and acquisition attribution require separate evidence.");
    }

    [Theory]
    [InlineData("transactions", false)]
    [InlineData("staging", false)]
    [InlineData("transactions", true)]
    [InlineData("staging", true)]
    public async Task CliCacheEvidencePreflight_PendingState_IsRejectedWithoutMutation(
        string stateDirectory,
        bool statePathIsFile)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.CachePath, "packages.ini"),
            "[cache]\nversion = 3\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.CachePath, "untouched-empty"));
        string statePath = Path.Combine(workspace.CachePath, ".fhirpkg", stateDirectory);
        string markerPath = statePathIsFile
            ? statePath
            : stateDirectory == "transactions"
                ? Path.Combine(statePath, "pending-journal.json")
                : Path.Combine(statePath, "abandoned-operation", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        byte[] markerBytes = Encoding.UTF8.GetBytes("pending evidence must not be recovered");
        await File.WriteAllBytesAsync(
            markerPath,
            markerBytes,
            TestContext.Current.CancellationToken);
        CacheEvidenceSnapshot before =
            await SnapshotCliCacheEvidenceFixtureAsync(workspace.RootPath);

        Should.Throw<ShouldAssertException>(() =>
            AssertCliCacheEvidenceQuiescent(workspace.CachePath));

        (await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken))
            .SequenceEqual(markerBytes).ShouldBeTrue();
        await AssertCliCacheEvidenceFixtureUnchangedAsync(workspace.RootPath, before);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CliCacheEvidencePreflight_AbsentOrEmptyState_IsAccepted(
        bool hasControlDirectory,
        bool hasTransactionsDirectory,
        bool hasStagingDirectory)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.CachePath, "packages.ini"),
            "[cache]\nversion = 3\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.CachePath, "untouched-empty"));
        string controlPath = Path.Combine(workspace.CachePath, ".fhirpkg");
        if (hasControlDirectory)
        {
            string lockPath = Path.Combine(controlPath, "locks");
            Directory.CreateDirectory(lockPath);
            await File.WriteAllTextAsync(
                Path.Combine(lockPath, "metadata.lock"),
                "coordination bookkeeping is not pending state",
                TestContext.Current.CancellationToken);
        }

        if (hasTransactionsDirectory)
        {
            Directory.CreateDirectory(Path.Combine(controlPath, "transactions"));
        }

        if (hasStagingDirectory)
        {
            Directory.CreateDirectory(Path.Combine(controlPath, "staging"));
        }

        CacheEvidenceSnapshot before =
            await SnapshotCliCacheEvidenceFixtureAsync(workspace.RootPath);

        AssertCliCacheEvidenceQuiescent(workspace.CachePath);

        await AssertCliCacheEvidenceFixtureUnchangedAsync(workspace.RootPath, before);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$feature")]
    public async Task MutableAlias_OverwritePublishesSecondGeneration(
        string alias)
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });
        PackageReference aliasReference =
            new("alias.package", alias);
        await using MemoryStream first =
            CreateArchive(
                "alias.package",
                "1.0.0",
                "generation.txt",
                "first");
        await manager.InstallAsync(
            aliasReference,
            first,
            options: null,
            TestContext.Current.CancellationToken);
        await using MemoryStream second =
            CreateArchive(
                "alias.package",
                "2.0.0",
                "generation.txt",
                "second");

        PackageRecord record = await manager.InstallAsync(
            aliasReference,
            second,
            new PackageSourceInstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(aliasReference);
        record.Manifest.Version.ShouldBe("2.0.0");
        string generation = await File.ReadAllTextAsync(
            Path.Combine(
                record.ContentPath,
                "generation.txt"),
            TestContext.Current.CancellationToken);
        generation.ShouldBe("second");
    }

    [Fact]
    public async Task DevDirective_RemainsLocalAuthoritativeWithOverwrite()
    {
        using TestWorkspace workspace = new();
        DiskPackageCache cache = new(workspace.CachePath);
        await using MemoryStream archive =
            CreateArchive("dev.package", "9.9.9");
        await cache.InstallAsync(
            new PackageReference("dev.package", "dev"),
            archive,
            ct:
                TestContext.Current.CancellationToken);
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });

        PackageRecord? record = await manager.InstallAsync(
            "dev.package#dev",
            new InstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record!.Reference.Version.ShouldBe("dev");
        record.Manifest.Version.ShouldBe("9.9.9");
    }

    [Fact]
    public async Task Clear_RemovesMetadataOnlyManagedEntryAndPreservesUnknownSection()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        PackageReference reference =
            new("metadata.only", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 42,
                SourcePublicationDate =
                    new DateTimeOffset(
                        2026,
                        7,
                        17,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                ArchiveSha256 = new string('a', 64)
            },
            TestContext.Current.CancellationToken);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.AppendAllTextAsync(
            metadataPath,
            $"{Environment.NewLine}[external]{Environment.NewLine}keep = value{Environment.NewLine}",
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-sizes"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-source-publication-dates"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-archive-sha256"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public async Task Clear_RemovesMalformedLegacyAndNonCanonicalManagedKeys()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.WriteAllTextAsync(
            metadataPath,
            """
            [cache]
            version = 3

            [packages]
            legacy.package@1.0.0 = 20260717120000
            UPPER.package#1.0.0 = 20260717120000
            ../malformed#1.0.0 = 20260717120000
            canonical.package#1.0.0 = 20260717120000

            [package-sizes]
            legacy.package@1.0.0 = 1
            @scope\package#1.0.0 = 2

            [package-source-publication-dates]
            UPPER.package#1.0.0 = 2026-07-17T12:00:00Z

            [package-archive-sha256]
            ../malformed#1.0.0 = abc

            [external]
            keep = value
            """,
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ShouldBeEmpty();
        sections["package-sizes"].ShouldBeEmpty();
        sections["package-source-publication-dates"].ShouldBeEmpty();
        sections["package-archive-sha256"].ShouldBeEmpty();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public void InstallExitCode_NotFoundRequiresNotFoundStatus()
    {
        int notFound = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "installed.package#1.0.0",
                Status = PackageInstallStatus.Installed
            },
            new PackageInstallResult
            {
                Directive = "missing.package#1.0.0",
                Status = PackageInstallStatus.NotFound,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);
        int failedResolution = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "network.package#1.0.0",
                Status = PackageInstallStatus.Failed,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);

        notFound.ShouldBe(ExitCodes.NotFound);
        failedResolution.ShouldBe(ExitCodes.NetworkError);
    }

    [Fact]
    public void InstallExitCode_MixedResultsUseStablePrecedence()
    {
        PackageInstallResult notFound = new()
        {
            Directive = "missing.package#1.0.0",
            Status = PackageInstallStatus.NotFound,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult networkFailure = new()
        {
            Directive = "network.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult checksumFailure = new()
        {
            Directive = "checksum.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ChecksumMismatch,
            ErrorStage = PackageInstallStage.ChecksumValidation
        };
        PackageInstallResult dependencyFailure = new()
        {
            Directive = "root.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode =
                PackageInstallErrorCode.DependencyInstallationFailed,
            ErrorStage = PackageInstallStage.DependencyInstallation,
            DependencyFailures =
            [
                new PackageInstallResult
                {
                    Directive = "dependency.package#1.0.0",
                    Status = PackageInstallStatus.NotFound,
                    ErrorCode =
                        PackageInstallErrorCode.ResolutionFailed,
                    ErrorStage = PackageInstallStage.Resolution,
                }
            ],
        };

        InstallCommand.GetExitCode(
            [notFound, networkFailure])
            .ShouldBe(ExitCodes.NetworkError);
        InstallCommand.GetExitCode(
            [networkFailure, checksumFailure])
            .ShouldBe(ExitCodes.ChecksumFail);
        InstallCommand.GetExitCode(
            [networkFailure, dependencyFailure])
            .ShouldBe(ExitCodes.DependencyResolutionFail);
    }

    private static FhirPackageManager CreateSourceManager(string cachePath, bool unmodified) =>
        new(new FhirPackageManagerOptions
        {
            CachePath = cachePath,
            Unmodified = unmodified,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            Registries = [],
        });

    private static async Task<PackageRecord> InstallSourceAsync(
        FhirPackageManager manager,
        PackageReference reference,
        string sourceMode,
        byte[] archive,
        PackageSourceInstallOptions options)
    {
        bool import = sourceMode.StartsWith("import", StringComparison.Ordinal);
        if (sourceMode.EndsWith("stream", StringComparison.Ordinal))
        {
            using MemoryStream source = new([42, 43, 44, .. archive]);
            source.Position = 3;
            try
            {
                return import
                    ? await manager.ImportAsync(source, options, TestContext.Current.CancellationToken)
                    : await manager.InstallAsync(reference, source, options, TestContext.Current.CancellationToken);
            }
            finally
            {
                source.CanRead.ShouldBeTrue("The supplied stream remains caller-owned, including on failure.");
                source.Position.ShouldBe(source.Length);
                source.Position = 3;
                source.ReadByte().ShouldBe(archive[0]);
            }
        }

        await using SingleResponseHttpServer server = await SingleResponseHttpServer.StartAsync(
            archive, TestContext.Current.CancellationToken);
        return import
            ? await manager.ImportAsync(server.Uri, options, TestContext.Current.CancellationToken)
            : await manager.InstallAsync(reference, server.Uri, options, TestContext.Current.CancellationToken);
    }

    private sealed record ArchivePayload(
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlySet<string> Directories);

    private static async Task<ArchivePayload> ReadArchivePayloadAsync(byte[] archiveBytes)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        using MemoryStream archive = new(archiveBytes);
        await using GZipStream gzip = new(archive, CompressionMode.Decompress);
        await using TarReader reader = new(gzip);
        while (await reader.GetNextEntryAsync(
            cancellationToken: TestContext.Current.CancellationToken) is TarEntry entry)
        {
            string path = entry.Name.TrimEnd('/');
            string[] segments = path.Split('/');
            int directoryDepth = entry.EntryType == TarEntryType.Directory
                ? segments.Length
                : segments.Length - 1;
            for (int depth = 1; depth <= directoryDepth; depth++)
                directories.Add(string.Join("/", segments.Take(depth)));
            if (entry.EntryType == TarEntryType.Directory)
                continue;

            entry.EntryType.ShouldBe(TarEntryType.RegularFile);
            using MemoryStream content = new();
            await entry.DataStream!.CopyToAsync(content, TestContext.Current.CancellationToken);
            files.Add(path, content.ToArray());
        }

        return new ArchivePayload(files, directories);
    }

    private static void AssertSourcePayload(
        PackageRecord record,
        PackageReference reference,
        ArchivePayload payload,
        bool rootLayout,
        bool unmodified,
        bool allowDerivedIndex)
    {
        if (unmodified)
            allowDerivedIndex.ShouldBeFalse();
        record.Reference.ShouldBe(reference);
        record.Manifest.Name.ShouldBe(reference.Name);
        record.Manifest.Version.ShouldBe(reference.Version);
        record.ContentPath.ShouldBe(rootLayout && unmodified
            ? record.DirectoryPath
            : Path.Combine(record.DirectoryPath, "package"));
        record.SizeBytes.ShouldBe(payload.Files.Values.Sum(bytes => bytes.LongLength));
        string wrapper = rootLayout && !unmodified ? "package/" : string.Empty;
        Dictionary<string, byte[]> expected = payload.Files.ToDictionary(
            file => wrapper + file.Key, file => file.Value, StringComparer.Ordinal);
        HashSet<string> directories = payload.Directories
            .Select(path => wrapper + path).ToHashSet(StringComparer.Ordinal);
        if (wrapper.Length != 0)
            directories.Add("package");
        string indexPath = Path.GetRelativePath(
            record.DirectoryPath, Path.Combine(record.ContentPath, ".index.json")).Replace('\\', '/');
        IEnumerable<string> expectedFiles = allowDerivedIndex
            ? expected.Keys.Append(indexPath).Distinct(StringComparer.Ordinal)
            : expected.Keys;
        Directory.GetFiles(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedFiles.OrderBy(path => path, StringComparer.Ordinal));
        Directory.GetDirectories(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(directories.OrderBy(path => path, StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expected)
        {
            if (!allowDerivedIndex || path != indexPath)
                File.ReadAllBytes(Path.Combine(record.DirectoryPath, path)).ShouldBe(bytes, path);
        }
    }

    private static void AssertSourceOperationArtifactsClean(string cachePath)
    {
        foreach (string name in new[] { "staging", "transactions", "backup", "quarantine" })
        {
            string path = Path.Combine(cachePath, ".fhirpkg", name);
            if (Directory.Exists(path))
                Directory.GetFileSystemEntries(path).ShouldBeEmpty();
        }
    }

    private static MemoryStream CreatePreservationArchive(
        PackageReference reference,
        bool rootLayout,
        string indexState,
        string generation,
        string manifestMembers = "",
        bool includeXml = false)
    {
        string prefix = rootLayout ? string.Empty : "package/";
        string canonical = $"https://example.test/{reference.Name}/resource";
        MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax))
        {
            WriteEntry(writer, $"{prefix}package.json",
                $$"""{"name":"{{reference.Name}}","version":"{{reference.Version}}","description":"{{generation}}"{{manifestMembers}}}""");
            WriteEntry(writer, $"{prefix}resource.json",
                $$"""{"resourceType":"StructureDefinition","id":"{{generation}}","url":"{{canonical}}","kind":"resource","derivation":"constraint","type":"Patient"}""");
            if (includeXml)
                WriteEntry(writer, $"{prefix}resource.xml",
                    $"""<Patient xmlns="http://hl7.org/fhir"><id value="{generation}"/></Patient>""");
            WriteEntry(writer, "README.txt", $"{generation} distributed root payload");
            WriteEntry(writer, "packages.ini", $"{generation} distributed metadata");
            WriteEntry(writer, ".fhirpkg/payload.bin", [0, 128, 255, 13, 10]);
            WriteEntry(writer, $"unknown/{generation}-source.yaml", "../other/sentinel.txt");
            WriteEntry(writer, "unknown/package.json", "{opaque ancillary manifest");
            WriteEntry(writer, "unknown/resource.json",
                $$"""{"resourceType":"Patient","id":"unknown","url":"{{canonical}}/unknown"}""");
            WriteEntry(writer, "other/sentinel.txt", $"{generation} sibling");
            WriteEntry(writer, "other/.index.json", $"{generation} ancillary index");
            WriteEntry(writer, "package/other/sentinel.txt", $"{generation} content subtree");
            WriteEntry(writer, "package/other/.index.json", $"{generation} content ancillary index");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "unknown/empty/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{prefix}content/empty/"));
            if (!rootLayout)
                WriteEntry(writer, ".index.json", "opaque root index");
            if (indexState == "directory")
                WriteEntry(writer, $"{prefix}.index.json/payload.txt", "never delete this directory");
            else if (indexState != "missing")
            {
                string index = indexState switch
                {
                    "valid" => $$"""
                        {
                          "publisher-extension": "retain formatting and bytes",
                          "files": [ { "filename": "resource.json", "resourceType": "StructureDefinition",
                            "id": "{{generation}}", "url": "{{canonical}}" } ],
                          "index-version": 2
                        }
                        """,
                    "invalid" => "{unusable JSON",
                    "unusable" => """{"index-version":2,"files":[{"filename":"missing.json","resourceType":"Patient"}]}""",
                    _ => throw new ArgumentException("Unknown index fixture state.", nameof(indexState)),
                };
                WriteEntry(writer, $"{prefix}.index.json", index);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static FhirPackageManager CreateCoreManager(string cachePath) =>
        new(new FhirPackageManagerOptions
        {
            CachePath = cachePath,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            VerifyChecksums = true
        });

    private static string AssertCliCachePath(string? cachePath)
    {
        string existingCachePath = AssertExistingEvidenceDirectory(cachePath);
        string packagePath = Path.Combine(existingCachePath, s_coreReference.FhirDirective);
        AssertExistingEvidenceDirectory(packagePath);
        string contentPath = Path.Combine(packagePath, "package");
        AssertExistingEvidenceDirectory(contentPath);
        AssertExistingEvidenceFile(Path.Combine(existingCachePath, "packages.ini"));
        AssertExistingEvidenceFile(Path.Combine(contentPath, "package.json"));
        AssertExistingEvidenceFile(Path.Combine(contentPath, ".index.json"));
        return existingCachePath;
    }

    private static string AssertRetainedCoreArchiveDirectory(string? retainedDirectory)
    {
        string existingDirectory = AssertExistingEvidenceDirectory(retainedDirectory);
        foreach (string requiredFile in new[] { PrimaryArchiveFileName, SecondaryArchiveFileName })
        {
            AssertExistingEvidenceFile(Path.Combine(existingDirectory, requiredFile));
        }

        return existingDirectory;
    }

    private static string AssertExistingEvidenceDirectory(string? path)
    {
        path.ShouldNotBeNullOrWhiteSpace();
        Path.IsPathFullyQualified(path).ShouldBeTrue("Evidence paths must be absolute.");
        string fullPath = Path.GetFullPath(path);
        File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
            $"Evidence directory must already exist: {fullPath}");
        return fullPath;
    }

    private static void AssertExistingEvidenceFile(string path)
    {
        File.GetAttributes(path).HasFlag(FileAttributes.Directory).ShouldBeFalse(
            $"Evidence must be an existing readable file: {path}");
        using FileStream stream = File.OpenRead(path);
        stream.Length.ShouldBeGreaterThan(0L, $"Evidence file must not be empty: {path}");
    }

    private static void AssertCliCacheEvidenceQuiescent(string cachePath)
    {
        // Enumerate instead of Exists: inaccessible/error states must fail, never look absent.
        EnumerationOptions options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        string? controlPath = Directory.EnumerateFileSystemEntries(cachePath, "*", options)
            .SingleOrDefault(path => string.Equals(
                Path.GetFileName(path), ".fhirpkg", StringComparison.OrdinalIgnoreCase));
        if (controlPath is null)
        {
            return;
        }

        File.GetAttributes(controlPath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
            $"CLI cache control path must be a directory: {controlPath}");
        string[] controlEntries =
            Directory.EnumerateFileSystemEntries(controlPath, "*", options).ToArray();
        foreach (string stateDirectory in new[] { "transactions", "staging" })
        {
            string? statePath = controlEntries.SingleOrDefault(path => string.Equals(
                Path.GetFileName(path), stateDirectory, StringComparison.OrdinalIgnoreCase));
            if (statePath is null)
            {
                continue;
            }

            File.GetAttributes(statePath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
                $"CLI cache state path must be absent or a directory: {statePath}");
            Directory.EnumerateFileSystemEntries(statePath, "*", options).ShouldBeEmpty(
                $"CLI cache evidence must not need recovery or staging cleanup: {statePath}");
        }
    }

    private static async Task<CacheEvidenceSnapshot> SnapshotCliCacheEvidenceFixtureAsync(
        string rootPath)
    {
        string[] directories = Directory.EnumerateDirectories(
                rootPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(
            rootPath, "*", SearchOption.AllDirectories))
        {
            files.Add(
                Path.GetRelativePath(rootPath, path),
                await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }

        return new CacheEvidenceSnapshot(files, directories);
    }

    private static async Task AssertCliCacheEvidenceFixtureUnchangedAsync(
        string rootPath,
        CacheEvidenceSnapshot before)
    {
        CacheEvidenceSnapshot after = await SnapshotCliCacheEvidenceFixtureAsync(rootPath);
        after.Directories.ShouldBe(before.Directories);
        after.Files.Keys.OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(before.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (KeyValuePair<string, byte[]> file in before.Files)
        {
            after.Files[file.Key].SequenceEqual(file.Value).ShouldBeTrue(
                $"Evidence fixture changed: {file.Key}");
        }
    }

    private sealed record CacheEvidenceSnapshot(
        IReadOnlyDictionary<string, byte[]> Files,
        string[] Directories);

    private static async Task AssertRetainedArchiveHashAsync(Stream archive, string pinnedHash)
    {
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(archive, TestContext.Current.CancellationToken))
            .ToLowerInvariant();
        actualHash.ShouldBe(pinnedHash);
    }

    private static async Task AssertRetainedCoreInstallAsync(
        string archiveFileName,
        string pinnedHash,
        bool hasAncillaryRoots)
    {
        string? retainedDirectory =
            Environment.GetEnvironmentVariable(RetainedArchiveDirectoryVariable);
        Assert.SkipWhen(
            retainedDirectory is null,
            $"Set {RetainedArchiveDirectoryVariable} to a local directory containing " +
            "primary-r4-4.0.1.tgz and secondary-r4-4.0.1.tgz to run retained-byte acceptance; " +
            "these tests never download archives.");
        retainedDirectory = AssertRetainedCoreArchiveDirectory(retainedDirectory);

        PackageInstallLimits limits = PackageInstallLimits.FromEnvironment();
        limits.MaxCompressedBytes.ShouldBe(PackageInstallLimits.DefaultMaxCompressedBytes);
        limits.MaxExpandedBytes.ShouldBe(PackageInstallLimits.DefaultMaxExpandedBytes);
        limits.MaxEntryBytes.ShouldBe(PackageInstallLimits.DefaultMaxEntryBytes);
        limits.MaxArchiveEntries.ShouldBe(PackageInstallLimits.DefaultMaxArchiveEntries);
        limits.MaxArchivePathLength.ShouldBe(PackageInstallLimits.DefaultMaxArchivePathLength);
        limits.MaxArchiveDepth.ShouldBe(PackageInstallLimits.DefaultMaxArchiveDepth);

        string archivePath = Path.Combine(retainedDirectory, archiveFileName);
        CoreArchiveInventory inventory;
        long compressedBytes;
        await using (FileStream inspectionStream = File.OpenRead(archivePath))
        {
            compressedBytes = inspectionStream.Length;
            await AssertRetainedArchiveHashAsync(inspectionStream, pinnedHash);
            inspectionStream.Position = 0;
            inventory = await InspectCoreArchiveAsync(
                inspectionStream,
                hasAncillaryRoots);
        }

        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        Directory.EnumerateFileSystemEntries(workspace.CachePath).ShouldBeEmpty();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Retained input: {archivePath}; compressed bytes: {compressedBytes}; " +
            $"SHA-256: {pinnedHash}; fresh cache: {workspace.CachePath}");
        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();

            // Reopen the same original file, not an extracted or repacked substitute.
            await using FileStream installStream = File.OpenRead(archivePath);
            PackageRecord record = await manager.InstallAsync(
                new PackageReference("hl7.fhir.r4.core", "4.0.1"),
                installStream,
                new PackageSourceInstallOptions
                {
                    ExpectedSha256 = pinnedHash,
                    IncludeDependencies = false,
                    OverwriteExisting = false
                },
                TestContext.Current.CancellationToken);

            installStream.CanRead.ShouldBeTrue();
            installStream.Length.ShouldBe(compressedBytes);
            installStream.Position.ShouldBe(compressedBytes);
            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        using DiskPackageCache cache = new(workspace.CachePath);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadata.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entry = metadata.Packages[s_coreReference.FhirDirective];
        entry.ArchiveSha256.ShouldBe(pinnedHash);
        entry.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Verified original payloads, metadata hash and {entry.SizeBytes} expanded bytes, " +
            "reopened exact listing/index, Patient canonical lookup/read, and no ancillary indexing. " +
            "The disposable test cache is removed on exit.");
    }

    private static async Task<CoreArchiveInventory> InspectCoreArchiveAsync(
        Stream archive,
        bool hasAncillaryRoots)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        HashSet<string> explicitPaths = new(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        await using GZipStream gzip = new(
            archive,
            CompressionMode.Decompress,
            leaveOpen: true);
        await using TarReader reader = new(gzip, leaveOpen: true);
        while (await reader.GetNextEntryAsync(
            cancellationToken: TestContext.Current.CancellationToken) is TarEntry entry)
        {
            entryCount++;
            (entry.EntryType is TarEntryType.RegularFile
                or TarEntryType.V7RegularFile
                or TarEntryType.Directory)
                .ShouldBeTrue($"Unexpected tar entry type for {entry.Name}");
            string path = entry.Name.Replace('\\', '/').TrimEnd('/');
            string[] segments = path.Split('/');
            segments.ShouldAllBe(segment =>
                segment.Length > 0 && segment != "." && segment != "..");
            explicitPaths.Add(path.Normalize()).ShouldBeTrue(
                $"Duplicate or case/Unicode-colliding entry: {path}");
            for (int depth = 1; depth < segments.Length; depth++)
            {
                directories.Add(string.Join("/", segments.Take(depth)));
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                directories.Add(path);
                continue;
            }

            using MemoryStream content = new();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(
                    content,
                    TestContext.Current.CancellationToken);
            }

            content.Length.ShouldBe(entry.Length);
            files.Add(path, content.ToArray());
        }

        files.ShouldNotBeEmpty();
        string[] allPaths = files.Keys.Concat(directories).ToArray();
        allPaths.Select(path => path.Normalize())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ShouldBe(allPaths.Length, "Inventory paths must not collide.");
        string[] expectedRoots = hasAncillaryRoots
            ? ["openapi", "other", "package"]
            : ["package"];
        directories.Where(path => !path.Contains('/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedRoots);
        files.Keys.ShouldAllBe(path => path.Contains('/'));
        files.Keys.Where(path => string.Equals(
                path.Split('/')[^1],
                "package.json",
                StringComparison.OrdinalIgnoreCase))
            .ShouldHaveSingleItem().ShouldBe("package/package.json");
        JsonNode? manifest = JsonNode.Parse(files["package/package.json"]);
        manifest.ShouldNotBeNull();
        manifest["name"]!.GetValue<string>().ShouldBe(s_coreReference.Name);
        manifest["version"]!.GetValue<string>().ShouldBe(s_coreReference.Version);
        files.ContainsKey("package/StructureDefinition-Patient.json").ShouldBeTrue();
        AssertPatientJson(JsonNode.Parse(files["package/StructureDefinition-Patient.json"]));

        foreach (string root in expectedRoots.Where(root => root != "package"))
        {
            files.Keys.Any(path => path.StartsWith(root + "/", StringComparison.Ordinal))
                .ShouldBeTrue($"The retained {root}/ tree must contain payloads.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Inspected complete inventory: {entryCount} entries, {files.Count} files, " +
            $"{files.Values.Sum(bytes => bytes.LongLength)} expanded bytes; " +
            $"roots: {string.Join(", ", expectedRoots)}; one exact manifest.");
        return new CoreArchiveInventory(files, directories);
    }

    private static async Task AssertCorePayloadsAsync(
        PackageRecord record,
        string cachePath,
        CoreArchiveInventory inventory)
    {
        record.Reference.ShouldBe(s_coreReference);
        record.Manifest.Name.ShouldBe(s_coreReference.Name);
        record.Manifest.Version.ShouldBe(s_coreReference.Version);
        record.DirectoryPath.ShouldBe(Path.Combine(cachePath, s_coreReference.FhirDirective));
        record.ContentPath.ShouldBe(Path.Combine(record.DirectoryPath, "package"));
        record.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));

        Directory.EnumerateDirectories(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(inventory.Directories.OrderBy(path => path, StringComparer.Ordinal));

        foreach (KeyValuePair<string, byte[]> file in inventory.Files)
        {
            // The SDK may regenerate only this derivative file, never a payload.
            if (file.Key == "package/.index.json")
            {
                continue;
            }

            byte[] installedBytes = await File.ReadAllBytesAsync(
                Path.Combine(record.DirectoryPath, file.Key),
                TestContext.Current.CancellationToken);
            installedBytes.SequenceEqual(file.Value).ShouldBeTrue(
                $"Original payload changed: {file.Key}");
        }

        string[] expectedFiles = inventory.Files.Keys
            .Append("package/.index.json")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Directory.EnumerateFiles(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedFiles);

        record.Index.ShouldNotBeNull();
        record.Index.IndexVersion.ShouldBe(2);
        record.Index.Files.ShouldNotBeEmpty();
        ResourceIndexEntry patient = record.Index.Files
            .Where(file => file.Url == PatientCanonical).ShouldHaveSingleItem();
        patient.Filename.ShouldBe("StructureDefinition-Patient.json");
        patient.ResourceType.ShouldBe("StructureDefinition");
        patient.Id.ShouldBe("Patient");
        record.Index.Files.ShouldAllBe(file =>
            !IsAncillaryResourcePath(file.Filename)
            && file.Url != OpenApiCanonical
            && file.Url != OtherCanonical);
    }

    private static async Task AssertCoreUsableAfterReopenAsync(
        string cachePath,
        CoreArchiveInventory inventory)
    {
        using FhirPackageManager reopened = CreateCoreManager(cachePath);
        IReadOnlyList<PackageRecord> cached = await reopened.ListCachedAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        PackageRecord record = cached.ShouldHaveSingleItem();
        await AssertCorePayloadsAsync(record, cachePath, inventory);
        ResourceInfo? patient = await reopened.FindByCanonicalUrlAsync(
            PatientCanonical,
            s_coreReference.FhirDirective,
            TestContext.Current.CancellationToken);
        patient.ShouldNotBeNull();
        patient.PackageName.ShouldBe(s_coreReference.Name);
        patient.PackageVersion.ShouldBe(s_coreReference.Version);
        string patientFilePath = patient.FilePath.ShouldNotBeNull();
        patientFilePath.ShouldBe("StructureDefinition-Patient.json");
        patient.ResourceType.ShouldBe("StructureDefinition");
        patient.Id.ShouldBe("Patient");
        patient.Url.ShouldBe(PatientCanonical);
        JsonNode? resource = await reopened.ReadResourceAsync(
            patient,
            TestContext.Current.CancellationToken);
        AssertPatientJson(resource);
        byte[] patientBytes = inventory.Files["package/StructureDefinition-Patient.json"];
        JsonNode.DeepEquals(resource, JsonNode.Parse(patientBytes)).ShouldBeTrue();
        (await File.ReadAllBytesAsync(
            Path.Combine(record.ContentPath, patientFilePath),
            TestContext.Current.CancellationToken))
            .SequenceEqual(patientBytes).ShouldBeTrue();

        foreach (string ancillaryCanonical in new[] { OpenApiCanonical, OtherCanonical })
        {
            (await reopened.FindByCanonicalUrlAsync(
                ancillaryCanonical,
                s_coreReference.FhirDirective,
                TestContext.Current.CancellationToken))
                .ShouldBeNull();
        }

        IReadOnlyList<ResourceInfo> indexed = await reopened.FindResourcesAsync(
            new ResourceSearchCriteria { PackageScope = s_coreReference.FhirDirective },
            TestContext.Current.CancellationToken);
        indexed.Count.ShouldBe(record.Index!.Files.Count);
        indexed.ShouldAllBe(resourceInfo =>
            resourceInfo.FilePath != null && !IsAncillaryResourcePath(resourceInfo.FilePath));
    }

    private static void AssertPatientJson(JsonNode? resource)
    {
        resource.ShouldNotBeNull();
        resource["resourceType"]!.GetValue<string>().ShouldBe("StructureDefinition");
        resource["id"]!.GetValue<string>().ShouldBe("Patient");
        resource["url"]!.GetValue<string>().ShouldBe(PatientCanonical);
        resource["type"]!.GetValue<string>().ShouldBe("Patient");
    }

    private static bool IsAncillaryResourcePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("openapi/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("other/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("package/openapi/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("package/other/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CoreArchiveInventory(
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlySet<string> Directories);

    private static MemoryStream CreateAncillaryCoreArchive()
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteEntry(
                writer,
                "package/package.json",
                """{"name":"hl7.fhir.r4.core","version":"4.0.1","fhirVersions":["4.0.1"],"type":"fhir.core"}""");
            WriteEntry(
                writer,
                "package/StructureDefinition-Patient.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"Patient","url":"{{PatientCanonical}}",
                 "version":"4.0.1","name":"Patient","status":"active","kind":"resource","abstract":false,
                 "type":"Patient","baseDefinition":"http://hl7.org/fhir/StructureDefinition/DomainResource",
                 "derivation":"specialization"}
                """);
            WriteEntry(
                writer,
                "openapi/StructureDefinition-OpenApiOnly.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"OpenApiOnly","url":"{{OpenApiCanonical}}",
                 "name":"OpenApiOnly","status":"draft","kind":"resource","abstract":false,"type":"Patient",
                 "baseDefinition":"{{PatientCanonical}}","derivation":"constraint"}
                """);
            WriteEntry(
                writer,
                "other/StructureDefinition-OtherOnly.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"OtherOnly","url":"{{OtherCanonical}}",
                 "name":"OtherOnly","status":"draft","kind":"resource","abstract":false,"type":"Patient",
                 "baseDefinition":"{{PatientCanonical}}","derivation":"constraint"}
                """);
        }

        archive.Position = 0;
        return archive;
    }

    private static MemoryStream CreateArchive(
        string name,
        string version,
        string? extraName = null,
        string? extraContent = null)
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(
            archive,
            CompressionLevel.Fastest,
            leaveOpen: true))
        using (TarWriter writer = new(
            gzip,
            TarEntryFormat.Pax,
            leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.Directory,
                "package/"));
            WriteEntry(
                writer,
                "package/package.json",
                $$"""{"name":"{{name}}","version":"{{version}}"}""");
            if (extraName is not null)
            {
                WriteEntry(
                    writer,
                    $"package/{extraName}",
                    extraContent ?? string.Empty);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static void WriteEntry(
        TarWriter writer,
        string path,
        string content) =>
        WriteEntry(writer, path, Encoding.UTF8.GetBytes(content));

    private static void WriteEntry(
        TarWriter writer,
        string path,
        byte[] content)
    {
        writer.WriteEntry(new PaxTarEntry(
            TarEntryType.RegularFile,
            path)
        {
            DataStream = new MemoryStream(content)
        });
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace(bool useTemporaryDirectory = false)
        {
            RootPath = Path.Combine(
                useTemporaryDirectory ? Path.GetTempPath() : AppContext.BaseDirectory,
                useTemporaryDirectory ? "hp" : "phase5-hardened-integration",
                useTemporaryDirectory ? Guid.NewGuid().ToString("N")[..12] : Guid.NewGuid().ToString("N"));
            CachePath = Path.Combine(RootPath, "cache");
            Directory.CreateDirectory(CachePath);
        }

        internal string RootPath { get; }
        internal string CachePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class SingleResponseHttpServer :
        IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        private SingleResponseHttpServer(
            TcpListener listener,
            Task serverTask,
            Uri uri)
        {
            _listener = listener;
            _serverTask = serverTask;
            Uri = uri;
        }

        internal Uri Uri { get; }

        internal static Task<SingleResponseHttpServer> StartAsync(
            byte[] content,
            CancellationToken cancellationToken)
        {
            TcpListener listener = new(
                IPAddress.Loopback,
                port: 0);
            listener.Start();
            int port =
                ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = ServeAsync(
                listener,
                content,
                cancellationToken);
            SingleResponseHttpServer server = new(
                listener,
                serverTask,
                new Uri($"http://127.0.0.1:{port}/package.tgz"));
            return Task.FromResult(server);
        }

        private static async Task ServeAsync(
            TcpListener listener,
            byte[] content,
            CancellationToken cancellationToken)
        {
            using TcpClient client =
                await listener.AcceptTcpClientAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            await using NetworkStream stream =
                client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int total = 0;
            while (total < requestBuffer.Length)
            {
                int read = await stream.ReadAsync(
                        requestBuffer.AsMemory(total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (Encoding.ASCII
                    .GetString(requestBuffer, 0, total)
                    .Contains(
                        "\r\n\r\n",
                        StringComparison.Ordinal))
                {
                    break;
                }
            }

            byte[] headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/gzip\r\n" +
                $"Content-Length: {content.LongLength}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(
                    headers,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException exception)
                when (exception.SocketErrorCode
                    == SocketError.OperationAborted)
            {
            }
        }
    }
}
