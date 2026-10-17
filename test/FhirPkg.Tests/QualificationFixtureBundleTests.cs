// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirPkg.Qualification;
using FhirPkg.Qualification.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class QualificationFixtureBundleTests : IDisposable
{
    private const string SummaryTitle = "fixture-drift";
    private const string ReportFileName = "fixture-drift-report.json";
    private const string R6Name = "hl7.fhir.r6.core";
    private const string CoreName = "example.fixture.core";
    private const string CoreVersion = "1.0.0";
    private const string CoreId = "example.fixture.core#1.0.0";
    private const string CoreUri =
        "https://packages2.fhir.org/packages/example.fixture.core/1.0.0";
    private const string GuideName = "example.fixture.guide";
    private const string GuideVersion = "2.0.0";
    private const string GuideId = "example.fixture.guide#2.0.0";
    private const string GuideUri =
        "https://packages2.fhir.org/packages/example.fixture.guide/2.0.0";
    private const string CurrentId = "r6-current";
    private const string CurrentUri =
        "https://build.fhir.org/hl7.fhir.r6.core.tgz";
    private const string CurrentMasterId = "r6-current-master";
    private const string CurrentMasterUri =
        "https://build.fhir.org/branches/master/hl7.fhir.r6.core.tgz";

    private static readonly string s_corpusSha256 = new('c', 64);
    private static readonly DateTimeOffset s_coreLastModified =
        new(2026, 7, 1, 12, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset s_guideLastModified =
        new(2026, 7, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_currentLastModified =
        new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_coreArchive =
        DeterministicPackageArchive.Create(CoreName, CoreVersion, "core");
    private static readonly byte[] s_guideArchive =
        DeterministicPackageArchive.Create(GuideName, GuideVersion, "guide");
    private static readonly byte[] s_currentArchive =
        DeterministicPackageArchive.Create(R6Name, "6.0.0-cibuild", "current");
    private static readonly Func<string, string?> s_noEnvironment =
        static _ => null;

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-qualification-bundle-{Guid.NewGuid():N}");
    private readonly string _cacheRoot;
    private readonly string _bundleRoot;

    public QualificationFixtureBundleTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _cacheRoot = Path.Combine(_tempRoot, "cache");
        _bundleRoot = Path.Combine(_tempRoot, "bundle");
    }

    [Fact]
    public void BundleFormatAndFailureCodes_AreStableStrings()
    {
        FixtureBundle.ManifestFileName.ShouldBe("bundle-manifest.json");
        FixtureBundle.BlobDirectoryName.ShouldBe("blobs");
        FixtureBundle.ArtifactNamePrefix.ShouldBe("fixture-bundle-v1-");
        FixtureBundle.SchemaVersion.ShouldBe(1);
        QualificationFailureCodes.BundleFreezeBlocked
            .ShouldBe("BundleFreezeBlocked");
        QualificationFailureCodes.BundleVerificationFailed
            .ShouldBe("BundleVerificationFailed");
    }

    [Theory]
    [InlineData(QualificationFailureCodes.BundleFreezeBlocked)]
    [InlineData(QualificationFailureCodes.BundleVerificationFailed)]
    public void Classify_FixtureBundleException_UsesItsCode(string code)
    {
        QualificationFailureCodes.Classify(
                new FixtureBundleException(code, "The bundle was refused."))
            .ShouldBe(code);
    }

    [Fact]
    public async Task FreezeAsync_EveryArtifactVerified_WritesHashAddressedBundle()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        using FixtureHttpMessageHandler handler = CreateHandler();

        QualificationReport report = await CreateRunner(corpus, handler)
            .FreezeAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeTrue();
        QualificationCaseResult freeze = report.Cases.ShouldHaveSingleItem();
        freeze.Id.ShouldBe("bundle-freeze");
        freeze.Success.ShouldBeTrue();
        QualificationDetail detail = freeze.Details.ShouldHaveSingleItem();
        detail.Name.ShouldBe("bundleSha256");
        byte[] manifestBytes = File.ReadAllBytes(
            Path.Combine(_bundleRoot, "bundle-manifest.json"));
        detail.Value.ShouldBe(Sha256(manifestBytes));
        handler.Requests.ToArray().ShouldBe(
        [
            $"GET {CoreUri}",
            $"GET {GuideUri}",
            $"GET {CurrentUri}",
            $"GET {CurrentMasterUri}"
        ]);

        GetEntryNames(_bundleRoot).ShouldBe(
            ["blobs", "bundle-manifest.json"]);
        string[] blobNames = GetEntryNames(
            Path.Combine(_bundleRoot, "blobs"));
        blobNames.ShouldBe(
            new[] { s_coreArchive, s_guideArchive, s_currentArchive }
                .Select(archive => $"{Sha256(archive)}.tgz")
                .Order(StringComparer.Ordinal)
                .ToArray());

        FixtureBundleManifest manifest = ReadManifest(manifestBytes);
        manifest.SchemaVersion.ShouldBe(1);
        manifest.CorpusSha256.ShouldBe(s_corpusSha256);
        manifest.CorpusHashAlgorithm.ShouldBe("canonical-json-sha256-v1");
        manifest.Artifacts
            .Select(artifact => $"{artifact.Kind} {artifact.Id}")
            .ToArray()
            .ShouldBe(
            [
                $"immutable {CoreId}",
                $"immutable {GuideId}",
                $"mutable {CurrentId}",
                $"mutable {CurrentMasterId}"
            ]);
        foreach (FixtureBundleArtifact entry in manifest.Artifacts)
        {
            QualificationArtifactResult result = report.Artifacts.Single(
                artifact => artifact.Id == entry.Id);
            result.Success.ShouldBeTrue();
            entry.Uri.ShouldBe(result.SourceUri);
            entry.Sha256.ShouldBe(result.ActualSha256);
            result.CompressedBytes.ShouldBe(entry.CompressedBytes);
            result.ExpandedBytes.ShouldBe(entry.ExpandedBytes);
            result.LargestEntryBytes.ShouldBe(entry.LargestEntryBytes);
            result.EntryCount.ShouldBe(entry.EntryCount);
            File.ReadAllBytes(
                    Path.Combine(_bundleRoot, "blobs", $"{entry.Sha256}.tgz"))
                .SequenceEqual(GetServedArchive(entry.Id))
                .ShouldBeTrue();
        }

        manifest.Artifacts.Single(artifact => artifact.Id == CoreId)
            .PublicationUtc
            .ShouldBe(s_coreLastModified);
        manifest.Artifacts.Single(artifact => artifact.Id == CurrentId)
            .Sha256
            .ShouldBe(manifest.Artifacts
                .Single(artifact => artifact.Id == CurrentMasterId)
                .Sha256);

        string localId = corpus.LocalFixture.Id;
        report.Artifacts.Single(artifact => artifact.Id == localId)
            .Success
            .ShouldBeTrue();
        manifest.Artifacts.ShouldNotContain(
            artifact => artifact.Id == localId);
        blobNames.ShouldNotContain($"{corpus.LocalFixture.Sha256}.tgz");
        QualificationSchemaValidator.ValidateJson(
            JsonSerializer.Serialize(
                report,
                QualificationJson.SerializerOptions),
            QualificationSchemaPaths.Report);
    }

    [Fact]
    public async Task FreezeAsync_ImmutableHashMismatch_BlocksFreezeAndWritesNothing()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        using FixtureHttpMessageHandler handler = CreateHandler();
        handler.Serve(
            CoreUri,
            DeterministicPackageArchive.Create(
                CoreName,
                CoreVersion,
                "drifted"),
            s_coreLastModified);

        QualificationReport report = await CreateRunner(corpus, handler)
            .FreezeAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        QualificationArtifactResult core = report.Artifacts.Single(
            artifact => artifact.Id == CoreId);
        core.Success.ShouldBeFalse();
        core.Failure.ShouldNotBeNull();
        core.Failure!.Code.ShouldBe(
            QualificationFailureCodes.ImmutableHashMismatch);
        report.Artifacts
            .Where(artifact => artifact.Id != CoreId)
            .ShouldAllBe(artifact => artifact.Success);
        QualificationCaseResult freeze = report.Cases.ShouldHaveSingleItem();
        freeze.Id.ShouldBe("bundle-freeze");
        freeze.Success.ShouldBeFalse();
        QualificationFailure failure = freeze.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(QualificationFailureCodes.BundleFreezeBlocked);
        failure.ExceptionType.ShouldBe(nameof(FixtureBundleException));
        failure.Message.ShouldBe(
            $"No bundle was frozen because 1 artifact(s) failed: {CoreId} (ImmutableHashMismatch).");
        QualificationDetail detail = freeze.Details.ShouldHaveSingleItem();
        detail.Name.ShouldBe("blockedArtifacts");
        detail.Value.ShouldBe(CoreId);
        Directory.Exists(_bundleRoot).ShouldBeFalse();
        QualificationSchemaValidator.ValidateJson(
            JsonSerializer.Serialize(
                report,
                QualificationJson.SerializerOptions),
            QualificationSchemaPaths.Report);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        string pinned = Sha256(s_coreArchive);
        content.Markdown.ShouldStartWith(
            "### fixture-drift \u2014 FAILED\n");
        content.Markdown.ShouldContain(
            $"| {CoreId} | immutable | {pinned} | {core.ActualSha256} | 2026-07-01T12:30:15.0000000+00:00 | ");
        content.Markdown.ShouldContain(" | **changed** |\n");
        content.Markdown.ShouldEndWith(
            "\n1 of 2 pinned artifacts changed \u00b7 no bundle frozen\n");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Fixture changed {CoreId}::pinned {pinned} observed {core.ActualSha256}"
        ]);
    }

    [Fact]
    public async Task FreezeAsync_SourceUnreachable_BlocksFreezeAndWritesNothing()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        using FixtureHttpMessageHandler handler = CreateHandler();
        handler.Respond(GuideUri, HttpStatusCode.NotFound);

        QualificationReport report = await CreateRunner(corpus, handler)
            .FreezeAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        QualificationArtifactResult guide = report.Artifacts.Single(
            artifact => artifact.Id == GuideId);
        guide.Success.ShouldBeFalse();
        guide.Failure.ShouldNotBeNull();
        guide.Failure!.Code.ShouldBe(
            QualificationFailureCodes.SourceUnreachable);
        QualificationCaseResult freeze = report.Cases.ShouldHaveSingleItem();
        QualificationFailure failure = freeze.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(QualificationFailureCodes.BundleFreezeBlocked);
        failure.Message.ShouldBe(
            $"No bundle was frozen because 1 artifact(s) failed: {GuideId} (SourceUnreachable).");
        freeze.Details.ShouldHaveSingleItem().Value.ShouldBe(GuideId);
        Directory.Exists(_bundleRoot).ShouldBeFalse();

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldContain(
            $"| {GuideId} | immutable | {Sha256(s_guideArchive)} | none | none | none | none | **unreachable** |\n");
        content.Markdown.ShouldEndWith(
            "\n0 of 2 pinned artifacts changed \u00b7 no bundle frozen\n");
        string annotation = content.Annotations.ShouldHaveSingleItem();
        annotation.ShouldStartWith(
            $"::error title=Fixture source unreachable {GuideId}::");
        annotation.ShouldContain("404");
    }

    [Fact]
    public async Task WriteFrozenAsync_GitHubRunVariablesSet_RecordsTheRun()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        Dictionary<string, DownloadedArtifact> artifacts =
            await CreateDownloadedArtifactsAsync();
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["GITHUB_REPOSITORY"] = "GinoCanessa/dotnet-fhir-packages",
            ["GITHUB_RUN_ID"] = "38954982341",
            ["GITHUB_SHA"] = new string('e', 40)
        };

        string digest = await FixtureBundleWriter.WriteFrozenAsync(
            _bundleRoot,
            corpus,
            s_corpusSha256,
            artifacts,
            name => environment.GetValueOrDefault(name),
            TestContext.Current.CancellationToken);

        byte[] manifestBytes = File.ReadAllBytes(
            Path.Combine(_bundleRoot, "bundle-manifest.json"));
        digest.ShouldBe(Sha256(manifestBytes));
        FixtureBundleRecorder? recordedBy =
            ReadManifest(manifestBytes).RecordedBy;
        recordedBy.ShouldNotBeNull();
        recordedBy!.Repository.ShouldBe("GinoCanessa/dotnet-fhir-packages");
        recordedBy.RunId.ShouldBe("38954982341");
        recordedBy.Commit.ShouldBe(new string('e', 40));
    }

    [Theory]
    [InlineData("GITHUB_REPOSITORY")]
    [InlineData("GITHUB_RUN_ID")]
    [InlineData("GITHUB_SHA")]
    public async Task WriteFrozenAsync_AnyGitHubRunVariableMissing_OmitsRecordedBy(
        string missingVariable)
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        Dictionary<string, DownloadedArtifact> artifacts =
            await CreateDownloadedArtifactsAsync();
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["GITHUB_REPOSITORY"] = "GinoCanessa/dotnet-fhir-packages",
            ["GITHUB_RUN_ID"] = "38954982341",
            ["GITHUB_SHA"] = new string('e', 40)
        };
        environment.Remove(missingVariable);

        await FixtureBundleWriter.WriteFrozenAsync(
            _bundleRoot,
            corpus,
            s_corpusSha256,
            artifacts,
            name => environment.GetValueOrDefault(name),
            TestContext.Current.CancellationToken);

        byte[] manifestBytes = File.ReadAllBytes(
            Path.Combine(_bundleRoot, "bundle-manifest.json"));
        ReadManifest(manifestBytes).RecordedBy.ShouldBeNull();
        Encoding.UTF8.GetString(manifestBytes)
            .ShouldNotContain("recordedBy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFrozenAsync_FailureAfterWriting_RemovesWhatItWrote(
        bool outputDirectoryExists)
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        Dictionary<string, DownloadedArtifact> artifacts =
            await CreateDownloadedArtifactsAsync();
        await File.WriteAllBytesAsync(
            artifacts[GuideId].FilePath,
            s_currentArchive,
            TestContext.Current.CancellationToken);
        if (outputDirectoryExists)
            Directory.CreateDirectory(_bundleRoot);

        FixtureBundleException exception =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleWriter.WriteFrozenAsync(
                    _bundleRoot,
                    corpus,
                    s_corpusSha256,
                    artifacts,
                    s_noEnvironment,
                    TestContext.Current.CancellationToken));

        exception.Code.ShouldBe(QualificationFailureCodes.BundleFreezeBlocked);
        exception.Message.ShouldContain(GuideId);
        exception.Message.ShouldContain(Sha256(s_currentArchive));
        if (outputDirectoryExists)
            GetEntryNames(_bundleRoot).ShouldBeEmpty();
        else
            Directory.Exists(_bundleRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteFrozenAsync_NonEmptyOutput_IsRefusedAndLeftUntouched()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        Dictionary<string, DownloadedArtifact> artifacts =
            await CreateDownloadedArtifactsAsync();
        Directory.CreateDirectory(_bundleRoot);
        string existing = Path.Combine(_bundleRoot, "existing.txt");
        await File.WriteAllTextAsync(
            existing,
            "keep",
            TestContext.Current.CancellationToken);

        FixtureBundleException exception =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleWriter.WriteFrozenAsync(
                    _bundleRoot,
                    corpus,
                    s_corpusSha256,
                    artifacts,
                    s_noEnvironment,
                    TestContext.Current.CancellationToken));

        exception.Code.ShouldBe(QualificationFailureCodes.BundleFreezeBlocked);
        exception.Message.ShouldBe(
            "The bundle output directory must be absent or empty.");
        GetEntryNames(_bundleRoot).ShouldBe(["existing.txt"]);
        File.ReadAllText(existing).ShouldBe("keep");
    }

    [Fact]
    public async Task VerifyAsync_FrozenBundle_RoundTrips()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();

        VerifiedFixtureBundle verified =
            await FixtureBundleVerifier.VerifyAsync(
                _bundleRoot,
                frozen.Digest,
                frozen.Corpus,
                s_corpusSha256,
                TestContext.Current.CancellationToken);

        verified.Digest.ShouldBe(frozen.Digest);
        verified.EffectiveCorpus.ShouldBeSameAs(frozen.Corpus);
        verified.Manifest.Artifacts
            .Select(artifact => artifact.Id)
            .ToArray()
            .ShouldBe([CoreId, GuideId, CurrentId, CurrentMasterId]);
        verified.BlobPaths.Keys
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe([CoreId, GuideId, CurrentId, CurrentMasterId]);
        verified.BlobPaths[CoreId].ShouldBe(
            Path.Combine(
                Path.GetFullPath(_bundleRoot),
                "blobs",
                $"{Sha256(s_coreArchive)}.tgz"));
        verified.BlobPaths[CurrentId]
            .ShouldBe(verified.BlobPaths[CurrentMasterId]);
        foreach (string id in new[] { CoreId, GuideId, CurrentId })
        {
            File.ReadAllBytes(verified.BlobPaths[id])
                .SequenceEqual(GetServedArchive(id))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task VerifyAsync_TamperedBlob_Fails()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string blobName = $"{Sha256(s_guideArchive)}.tgz";
        string blobPath = Path.Combine(_bundleRoot, "blobs", blobName);
        byte[] tampered = File.ReadAllBytes(blobPath);
        tampered[^1] ^= 0xff;
        File.WriteAllBytes(blobPath, tampered);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, frozen.Digest);

        exception.Message.ShouldBe(
            $"Bundle blob '{blobName}' hashes to {Sha256(tampered)}.");
    }

    [Fact]
    public async Task VerifyAsync_TamperedManifest_Fails()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string tamperedDigest = RewriteManifest(
            manifest => GetArtifactNode(manifest, CoreId)["entryCount"] =
                99);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, frozen.Digest);

        exception.Message.ShouldBe(
            $"The bundle manifest SHA-256 was {tamperedDigest}, not the expected {frozen.Digest}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_ExtraFile_Fails(bool inBlobDirectory)
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string extraName = inBlobDirectory
            ? $"{new string('f', 64)}.tgz"
            : "notes.txt";
        string directory = inBlobDirectory
            ? Path.Combine(_bundleRoot, "blobs")
            : _bundleRoot;
        await File.WriteAllBytesAsync(
            Path.Combine(directory, extraName),
            s_coreArchive,
            TestContext.Current.CancellationToken);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, frozen.Digest);

        exception.Message.ShouldContain(
            $"contains the unexpected entry '{extraName}'");
    }

    [Fact]
    public async Task VerifyAsync_MissingArtifact_Fails()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string digest = RewriteManifest(
            manifest => manifest["artifacts"]!
                .AsArray()
                .Remove(GetArtifactNode(manifest, GuideId)));
        File.Delete(
            Path.Combine(
                _bundleRoot,
                "blobs",
                $"{Sha256(s_guideArchive)}.tgz"));

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, digest);

        exception.Message.ShouldBe(
            $"The bundle's immutable artifacts differ from the corpus; missing=[{GuideId}], unexpected=[].");
    }

    [Theory]
    [InlineData(
        CoreId,
        "https://packages2.fhir.org/packages/example.fixture.core/1.0.1")]
    [InlineData(
        CurrentMasterId,
        "https://build.fhir.org/branches/other/hl7.fhir.r6.core.tgz")]
    public async Task VerifyAsync_WrongUri_Fails(
        string id,
        string uri)
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string digest = RewriteManifest(
            manifest => GetArtifactNode(manifest, id)["uri"] = uri);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, digest);

        exception.Message.ShouldStartWith(
            $"Bundle artifact '{id}' records URI '{uri}', not the corpus URI ");
    }

    [Fact]
    public async Task VerifyAsync_ImmutableBytesOtherThanThePin_Fail()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string guideSha256 = Sha256(s_guideArchive);
        string digest = RewriteManifest(
            manifest => GetArtifactNode(manifest, CoreId)["sha256"] =
                guideSha256);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, digest);

        exception.Message.ShouldBe(
            $"Bundle artifact '{CoreId}' records SHA-256 {guideSha256}, not the corpus pin {Sha256(s_coreArchive)}.");
    }

    [Fact]
    public async Task VerifyAsync_DuplicateArtifactId_Fails()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string digest = RewriteManifest(
            manifest => manifest["artifacts"]!
                .AsArray()
                .Add(GetArtifactNode(manifest, CoreId).DeepClone()));

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, digest);

        exception.Message.ShouldBe(
            $"The bundle lists artifact '{CoreId}' more than once.");
    }

    [Fact]
    public async Task VerifyAsync_CorpusHashMismatch_Fails()
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string otherCorpusSha256 = new('d', 64);

        FixtureBundleException exception =
            await VerifyShouldFailAsync(
                frozen,
                frozen.Digest,
                otherCorpusSha256);

        exception.Message.ShouldBe(
            $"The bundle was frozen for corpus {s_corpusSha256}, not for corpus {otherCorpusSha256}.");
    }

    [Theory]
    [InlineData("approvals")]
    [InlineData("baseBundleSha256")]
    public async Task VerifyAsync_ApprovalData_IsRejected(string propertyName)
    {
        FrozenBundle frozen = await FreezeSyntheticBundleAsync();
        string digest = RewriteManifest(
            manifest => manifest[propertyName] =
                propertyName == "approvals"
                    ? new JsonArray()
                    : JsonValue.Create(new string('a', 64)));

        FixtureBundleException exception =
            await VerifyShouldFailAsync(frozen, digest);

        exception.Message.ShouldBe(
            "The bundle manifest carries approval data, which this harness does not accept.");
    }

    [Fact]
    public void Render_DriftReport_ShowsChangedUnreachableAndCapturedRows()
    {
        string pinned = new('a', 64);
        string observed = new('b', 64);
        string local = new('d', 64);
        string captured = new('e', 64);
        const string ipsId = "example.fixture.ips#1.1.0";
        const string notFound =
            "Response status code does not indicate success: 404 (Not Found).";
        QualificationReport report = CreateDriftReport(
            [
                CreateArtifact(
                    "immutable",
                    CoreId,
                    pinned,
                    pinned,
                    2048,
                    3,
                    "2026-07-01T12:30:15.0000000+00:00"),
                CreateArtifact(
                    "immutable",
                    GuideId,
                    pinned,
                    observed,
                    4096,
                    5,
                    "2026-10-15T08:00:00.0000000+00:00",
                    CreateFailure(
                        QualificationFailureCodes.ImmutableHashMismatch,
                        $"Immutable artifact SHA-256 was {observed}.",
                        nameof(QualificationInvariantException))),
                CreateArtifact(
                    "immutable",
                    ipsId,
                    pinned,
                    actualSha256: null,
                    compressedBytes: null,
                    entryCount: null,
                    publicationUtc: null,
                    CreateFailure(
                        QualificationFailureCodes.SourceUnreachable,
                        notFound,
                        nameof(HttpRequestException))),
                CreateArtifact(
                    "local",
                    "local.qualification.dev#dev",
                    local,
                    local,
                    512,
                    3,
                    publicationUtc: null),
                CreateArtifact(
                    "mutable",
                    CurrentId,
                    expectedSha256: null,
                    captured,
                    8192,
                    7,
                    "2026-10-16T05:00:00.0000000+00:00"),
                CreateArtifact(
                    "mutable",
                    CurrentMasterId,
                    expectedSha256: null,
                    captured,
                    8192,
                    7,
                    "2026-10-16T05:00:00.0000000+00:00")
            ],
            CreateFreezeCase(
                [
                    new QualificationDetail
                    {
                        Name = "blockedArtifacts",
                        Value = $"{GuideId},{ipsId}"
                    }
                ],
                [
                    CreateFailure(
                        QualificationFailureCodes.BundleFreezeBlocked,
                        $"No bundle was frozen because 2 artifact(s) failed: {GuideId} (ImmutableHashMismatch), {ipsId} (SourceUnreachable).",
                        nameof(FixtureBundleException))
                ]));

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            "### fixture-drift \u2014 FAILED\n"
            + "\n"
            + "| Artifact | Kind | Pinned SHA-256 | Observed SHA-256 | Upstream Last-Modified | Size | Entries | Result |\n"
            + "|-|-|-|-|-|-|-|-|\n"
            + $"| {CoreId} | immutable | {pinned} | {pinned} | 2026-07-01T12:30:15.0000000+00:00 | 2048 | 3 | verified |\n"
            + $"| {GuideId} | immutable | {pinned} | {observed} | 2026-10-15T08:00:00.0000000+00:00 | 4096 | 5 | **changed** |\n"
            + $"| {ipsId} | immutable | {pinned} | none | none | none | none | **unreachable** |\n"
            + $"| local.qualification.dev#dev | local | {local} | {local} | none | 512 | 3 | verified |\n"
            + $"| r6-current | mutable | none | {captured} | 2026-10-16T05:00:00.0000000+00:00 | 8192 | 7 | captured |\n"
            + $"| r6-current-master | mutable | none | {captured} | 2026-10-16T05:00:00.0000000+00:00 | 8192 | 7 | captured |\n"
            + "\n"
            + "1 of 3 pinned artifacts changed \u00b7 no bundle frozen\n");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Fixture changed {GuideId}::pinned {pinned} observed {observed}",
            $"::error title=Fixture source unreachable {ipsId}::{notFound}"
        ]);
    }

    [Fact]
    public void Render_DriftReport_FrozenBundle_NamesTheDigest()
    {
        string pinned = new('a', 64);
        string captured = new('e', 64);
        string digest = new('f', 64);
        QualificationReport report = CreateDriftReport(
            [
                CreateArtifact(
                    "immutable",
                    CoreId,
                    pinned,
                    pinned,
                    2048,
                    3,
                    publicationUtc: null),
                CreateArtifact(
                    "immutable",
                    GuideId,
                    pinned,
                    pinned,
                    4096,
                    5,
                    publicationUtc: null),
                CreateArtifact(
                    "mutable",
                    CurrentId,
                    expectedSha256: null,
                    captured,
                    8192,
                    7,
                    publicationUtc: null)
            ],
            CreateFreezeCase(
                [
                    new QualificationDetail
                    {
                        Name = "bundleSha256",
                        Value = digest
                    }
                ],
                []));

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldStartWith(
            "### fixture-drift \u2014 PASSED\n\n| Artifact | Kind |");
        content.Markdown.ShouldEndWith(
            $"| r6-current | mutable | none | {captured} | none | 8192 | 7 | captured |\n"
            + "\n"
            + $"0 of 2 pinned artifacts changed \u00b7 bundle sha256:{digest} frozen\n");
        content.Annotations.ShouldBeEmpty();
    }

    [Fact]
    public void Render_DriftReport_OtherArtifactFailure_IsFailedAndAnnotated()
    {
        string pinned = new('a', 64);
        const string limit =
            "Artifact 'r6-current' exceeds the qualification download limit.";
        QualificationReport report = CreateDriftReport(
            [
                CreateArtifact(
                    "immutable",
                    CoreId,
                    pinned,
                    pinned,
                    2048,
                    3,
                    publicationUtc: null),
                CreateArtifact(
                    "mutable",
                    CurrentId,
                    expectedSha256: null,
                    actualSha256: null,
                    compressedBytes: null,
                    entryCount: null,
                    publicationUtc: null,
                    CreateFailure(
                        code: null,
                        limit,
                        nameof(InvalidDataException)))
            ],
            CreateFreezeCase(
                [],
                [
                    CreateFailure(
                        QualificationFailureCodes.BundleFreezeBlocked,
                        $"No bundle was frozen because 1 artifact(s) failed: {CurrentId} (unclassified).",
                        nameof(FixtureBundleException))
                ]));

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldContain(
            "| r6-current | mutable | none | none | none | none | none | **failed** |\n");
        content.Markdown.ShouldEndWith(
            "\n0 of 1 pinned artifacts changed \u00b7 no bundle frozen\n");
        content.Annotations.ToArray().ShouldBe(
            [$"::error title=Fixture failed r6-current::{limit}"]);
    }

    [Fact]
    public void Render_DriftReport_FreezeFailureWithoutArtifactFailure_IsShownAndAnnotated()
    {
        string pinned = new('a', 64);
        QualificationReport report = CreateDriftReport(
            [
                CreateArtifact(
                    "immutable",
                    CoreId,
                    pinned,
                    pinned,
                    2048,
                    3,
                    publicationUtc: null)
            ],
            CreateFreezeCase(
                [],
                [
                    CreateFailure(
                        code: null,
                        "There is not enough space on the disk.",
                        nameof(IOException))
                ]));

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldEndWith(
            " | verified |\n"
            + "\n"
            + "bundle-freeze failed: There is not enough space on the disk.\n"
            + "\n"
            + "0 of 1 pinned artifacts changed \u00b7 no bundle frozen\n");
        content.Annotations.ToArray().ShouldBe(
        [
            "::error title=Qualification case bundle-freeze failed::There is not enough space on the disk."
        ]);
    }

    [Fact]
    public void Parse_FreezeBundle_IsExposedAsFullPath()
    {
        QualificationArguments freeze = QualificationArguments.Parse(
        [
            "--cache",
            _cacheRoot,
            "--output",
            Path.Combine(_tempRoot, ReportFileName),
            "--freeze-bundle",
            "fixture-bundle"
        ]);
        QualificationArguments live = QualificationArguments.Parse(
        [
            "--cache",
            _cacheRoot,
            "--output",
            Path.Combine(_tempRoot, ReportFileName)
        ]);

        freeze.FreezeBundlePath.ShouldBe(
            Path.GetFullPath("fixture-bundle"));
        freeze.ValidateOnly.ShouldBeFalse();
        live.FreezeBundlePath.ShouldBeNull();
    }

    [Theory]
    [InlineData(
        "true",
        "fixture-bundle",
        "Option '--freeze-bundle' cannot be combined with '--validate-only true'.")]
    [InlineData(
        "false",
        " ",
        "Option '--freeze-bundle' requires a value.")]
    public void Parse_InvalidFreezeBundle_IsRejected(
        string validateOnly,
        string freezeBundle,
        string message)
    {
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => QualificationArguments.Parse(
            [
                "--cache",
                _cacheRoot,
                "--output",
                Path.Combine(_tempRoot, ReportFileName),
                "--validate-only",
                validateOnly,
                "--freeze-bundle",
                freezeBundle
            ]));

        exception.Message.ShouldBe(message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    private static string[] GetEntryNames(string directory) =>
        new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static byte[] GetServedArchive(string id) =>
        id switch
        {
            CoreId => s_coreArchive,
            GuideId => s_guideArchive,
            CurrentId or CurrentMasterId => s_currentArchive,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };

    private static FixtureBundleManifest ReadManifest(byte[] manifestBytes) =>
        JsonSerializer.Deserialize<FixtureBundleManifest>(
            manifestBytes,
            QualificationJson.SerializerOptions)
        ?? throw new InvalidDataException("The manifest was empty.");

    private static async Task<QualificationCorpus> CreateCorpusAsync()
    {
        QualificationCorpus repositoryCorpus =
            await QualificationCorpus.LoadAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "qualification-corpus.json"),
                TestContext.Current.CancellationToken);
        return new QualificationCorpus
        {
            SchemaVersion = 1,
            ImmutableArtifacts =
            [
                new ImmutableArtifactDefinition
                {
                    Name = CoreName,
                    Version = CoreVersion,
                    Uri = CoreUri,
                    Sha256 = Sha256(s_coreArchive)
                },
                new ImmutableArtifactDefinition
                {
                    Name = GuideName,
                    Version = GuideVersion,
                    Uri = GuideUri,
                    Sha256 = Sha256(s_guideArchive)
                }
            ],
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
            DownstreamClosure = new DownstreamClosureDefinition
            {
                Roots = [],
                ResolverMembers = [],
                RequiredMembers = []
            }
        };
    }

    private static FixtureHttpMessageHandler CreateHandler()
    {
        FixtureHttpMessageHandler handler = new();
        handler.Serve(CoreUri, s_coreArchive, s_coreLastModified);
        handler.Serve(GuideUri, s_guideArchive, s_guideLastModified);
        handler.Serve(CurrentUri, s_currentArchive, s_currentLastModified);
        handler.Serve(
            CurrentMasterUri,
            s_currentArchive,
            s_currentLastModified);
        return handler;
    }

    private QualificationRunner CreateRunner(
        QualificationCorpus corpus,
        HttpMessageHandler handler) =>
        new(
            QualificationArguments.Parse(
            [
                "--cache",
                _cacheRoot,
                "--output",
                Path.Combine(_tempRoot, ReportFileName),
                "--freeze-bundle",
                _bundleRoot
            ]),
            corpus,
            new QualificationBuildSnapshot(
                "source",
                RequestedPackageVersion: null,
                PackageVersion: null,
                FhirPkgAssemblyVersion: "1.0.0.0",
                FhirPkgInformationalVersion: null),
            new ReportSanitizer([_tempRoot]),
            DateTimeOffset.UnixEpoch,
            s_corpusSha256,
            handler);

    private async Task<Dictionary<string, DownloadedArtifact>>
        CreateDownloadedArtifactsAsync()
    {
        Dictionary<string, DownloadedArtifact> artifacts =
            new(StringComparer.Ordinal);
        artifacts[CoreId] = await CreateDownloadedArtifactAsync(
            CoreId,
            s_coreArchive,
            s_coreLastModified);
        artifacts[GuideId] = await CreateDownloadedArtifactAsync(
            GuideId,
            s_guideArchive,
            s_guideLastModified);
        artifacts[CurrentId] = await CreateDownloadedArtifactAsync(
            CurrentId,
            s_currentArchive,
            s_currentLastModified);
        artifacts[CurrentMasterId] = await CreateDownloadedArtifactAsync(
            CurrentMasterId,
            s_currentArchive,
            s_currentLastModified);
        return artifacts;
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

    private async Task<FrozenBundle> FreezeSyntheticBundleAsync()
    {
        QualificationCorpus corpus = await CreateCorpusAsync();
        Dictionary<string, DownloadedArtifact> artifacts =
            await CreateDownloadedArtifactsAsync();
        string digest = await FixtureBundleWriter.WriteFrozenAsync(
            _bundleRoot,
            corpus,
            s_corpusSha256,
            artifacts,
            s_noEnvironment,
            TestContext.Current.CancellationToken);
        return new FrozenBundle(corpus, digest);
    }

    private async Task<FixtureBundleException> VerifyShouldFailAsync(
        FrozenBundle frozen,
        string expectedSha256,
        string? corpusSha256 = null)
    {
        FixtureBundleException exception =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleVerifier.VerifyAsync(
                    _bundleRoot,
                    expectedSha256,
                    frozen.Corpus,
                    corpusSha256 ?? s_corpusSha256,
                    TestContext.Current.CancellationToken));
        exception.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        return exception;
    }

    private string RewriteManifest(Action<JsonObject> change)
    {
        string path = Path.Combine(_bundleRoot, "bundle-manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(path))!
            .AsObject();
        change(manifest);
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(path, bytes);
        return Sha256(bytes);
    }

    private static JsonObject GetArtifactNode(
        JsonObject manifest,
        string id) =>
        manifest["artifacts"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(
                (string?)node["id"],
                id,
                StringComparison.Ordinal));

    private static QualificationReport CreateDriftReport(
        List<QualificationArtifactResult> artifacts,
        QualificationCaseResult freeze)
    {
        int artifactFailures = artifacts.Count(result => !result.Success);
        int caseFailures = freeze.Success ? 0 : 1;
        return new QualificationReport
        {
            Mode = "source",
            ValidationOnly = false,
            FhirPkgAssemblyVersion = "1.0.0.0",
            CorpusSha256 = s_corpusSha256,
            CorpusHashAlgorithm = "canonical-json-sha256-v1",
            Framework = ".NET",
            OperatingSystem = "test",
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            Success = artifactFailures == 0 && caseFailures == 0,
            Artifacts = artifacts,
            Cases = [freeze],
            Summary = new QualificationSummary
            {
                ArtifactCount = artifacts.Count,
                ArtifactFailures = artifactFailures,
                CaseCount = 1,
                CaseFailures = caseFailures
            }
        };
    }

    private static QualificationArtifactResult CreateArtifact(
        string kind,
        string id,
        string? expectedSha256,
        string? actualSha256,
        long? compressedBytes,
        int? entryCount,
        string? publicationUtc,
        QualificationFailure? failure = null) =>
        new()
        {
            Kind = kind,
            Id = id,
            SourceUri = $"https://packages.example.test/{id}",
            ExpectedSha256 = expectedSha256,
            ActualSha256 = actualSha256,
            CompressedBytes = compressedBytes,
            EntryCount = entryCount,
            PublicationUtc = publicationUtc,
            Success = failure is null,
            Failure = failure
        };

    private static QualificationCaseResult CreateFreezeCase(
        List<QualificationDetail> details,
        List<QualificationFailure> failures) =>
        new()
        {
            Id = "bundle-freeze",
            Success = failures.Count == 0,
            DurationMilliseconds = 0,
            Details = details,
            Failures = failures
        };

    private static QualificationFailure CreateFailure(
        string? code,
        string message,
        string exceptionType) =>
        new()
        {
            Code = code,
            ExceptionType = exceptionType,
            Message = message
        };

    private sealed record FrozenBundle(
        QualificationCorpus Corpus,
        string Digest);

    private sealed record FixtureResponse(
        HttpStatusCode StatusCode,
        byte[] Content,
        DateTimeOffset? LastModified);

    private sealed class FixtureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, FixtureResponse> _responses =
            new(StringComparer.Ordinal);
        private readonly List<string> _requests = [];

        internal IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToArray();
            }
        }

        internal void Serve(
            string uri,
            byte[] content,
            DateTimeOffset lastModified) =>
            _responses[new Uri(uri).AbsoluteUri] = new FixtureResponse(
                HttpStatusCode.OK,
                content,
                lastModified);

        internal void Respond(
            string uri,
            HttpStatusCode statusCode) =>
            _responses[new Uri(uri).AbsoluteUri] = new FixtureResponse(
                statusCode,
                [],
                LastModified: null);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri!.AbsoluteUri;
            lock (_requests)
                _requests.Add($"{request.Method} {uri}");

            FixtureResponse response = _responses.TryGetValue(
                uri,
                out FixtureResponse? routed)
                ? routed
                : new FixtureResponse(
                    HttpStatusCode.NotFound,
                    [],
                    LastModified: null);
            HttpResponseMessage message = new(response.StatusCode)
            {
                Content = new ByteArrayContent(response.Content),
                RequestMessage = request
            };
            message.Content.Headers.LastModified = response.LastModified;
            return Task.FromResult(message);
        }
    }
}
