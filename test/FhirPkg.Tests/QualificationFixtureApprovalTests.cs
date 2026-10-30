// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirPkg.Qualification;
using FhirPkg.Qualification.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class QualificationFixtureApprovalTests : IDisposable
{
    private const string ReportFileName = "qualification-report.json";
    private const string EntryPointTitle = "recover-entrypoint-derive";
    private const string Release = "v2026.1016.1417";
    private const string OtherRelease = "v2026.1016.1500";
    private const string ApprovalCommit =
        "0123456789abcdef0123456789abcdef01234567";
    private const string ApprovedBy = "GinoCanessa";
    private const string Reason =
        "packages2.fhir.org now serves re-published bytes for this version.";
    private const string ValidDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Arrow = "\u2192";
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
    private const string R4CoreId = "hl7.fhir.r4.core#4.0.1";
    private const string UsCoreId = "hl7.fhir.us.core#6.0.0";
    private const string IpsId = "hl7.fhir.uv.ips#1.1.0";

    private static readonly string s_corpusSha256 = new('c', 64);
    private static readonly DateTimeOffset s_frozenUtc =
        new(2026, 7, 1, 12, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset s_capturedUtc =
        new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_republishedUtc =
        new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly Func<string, string?> s_noEnvironment =
        static _ => null;
    private static readonly byte[] s_coreArchive =
        DeterministicPackageArchive.Create(CoreName, CoreVersion, "core");
    private static readonly byte[] s_republishedCoreArchive =
        DeterministicPackageArchive.Create(
            CoreName,
            CoreVersion,
            "republished");
    private static readonly byte[] s_guideArchive =
        DeterministicPackageArchive.Create(GuideName, GuideVersion, "guide");
    private static readonly byte[] s_currentArchive =
        DeterministicPackageArchive.Create(R6Name, "6.0.0-cibuild", "current");
    private static readonly byte[] s_harnessR6Archive = CreateManifestArchive(
        R6Name,
        "6.0.0-ballot3",
        "6.0.0-ballot3",
        "captured");
    private static readonly SyntheticPackage[] s_harnessPackages =
    [
        CreateSyntheticPackage("hl7.fhir.r4.core", "4.0.1", "4.0.1", "pinned"),
        CreateSyntheticPackage("hl7.fhir.r4b.core", "4.3.0", "4.3.0", "pinned"),
        CreateSyntheticPackage("hl7.fhir.r5.core", "5.0.0", "5.0.0", "pinned"),
        CreateSyntheticPackage("hl7.terminology", "7.2.0", "4.0.1", "pinned"),
        CreateSyntheticPackage(
            "hl7.fhir.us.core",
            "6.0.0",
            "4.0.1",
            "pinned",
            ("hl7.fhir.r4.core", "4.0.1")),
        CreateSyntheticPackage(
            "hl7.fhir.uv.ips",
            "1.1.0",
            "4.0.1",
            "pinned",
            ("hl7.fhir.r4.core", "4.0.1"))
    ];

    // Re-published bytes for the artifacts cases 01, 03, 04, and 06 check.
    private static readonly SyntheticPackage[] s_republishedPackages =
    [
        CreateSyntheticPackage(
            "hl7.fhir.r4b.core",
            "4.3.0",
            "4.3.0",
            "republished"),
        CreateSyntheticPackage(
            "hl7.fhir.r5.core",
            "5.0.0",
            "5.0.0",
            "republished"),
        CreateSyntheticPackage(
            "hl7.fhir.us.core",
            "6.0.0",
            "4.0.1",
            "republished",
            ("hl7.fhir.r4.core", "4.0.1"))
    ];

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-qualification-approval-{Guid.NewGuid():N}");
    private readonly string _baseRoot;
    private readonly string _derivedRoot;
    private readonly string _approvalPath;

    public QualificationFixtureApprovalTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _baseRoot = Path.Combine(_tempRoot, "base-bundle");
        _derivedRoot = Path.Combine(_tempRoot, "derived-bundle");
        _approvalPath = Path.Combine(
            _tempRoot,
            "approvals",
            $"{Release}.json");
    }

    [Fact]
    public void ApprovalRecords_InRepository_AreWellFormed()
    {
        AssertApprovalRecordsAreWellFormed(
            Path.Combine(
                AppContext.BaseDirectory,
                "ReleaseContracts",
                "approvals"));
    }

    [Fact]
    public void ApprovalRecordCheck_RefusesMalformedAndMisnamedRecords()
    {
        string directory = Path.Combine(_tempRoot, "records");
        WriteApproval(
            Path.Combine(directory, $"{Release}.json"),
            CreateApproval());

        AssertApprovalRecordsAreWellFormed(directory);

        WriteApproval(
            Path.Combine(directory, $"{OtherRelease}.json"),
            CreateApproval());
        Should.Throw<ShouldAssertException>(
                () => AssertApprovalRecordsAreWellFormed(directory))
            .Message.ShouldContain(
                $"Approval record '{OtherRelease}.json' must be named for its release.");

        File.Delete(Path.Combine(directory, $"{OtherRelease}.json"));
        File.WriteAllText(
            Path.Combine(directory, "v2026.1016.1600.json"),
            "{\"schemaVersion\":1}");
        Should.Throw<FixtureBundleException>(
                () => AssertApprovalRecordsAreWellFormed(directory))
            .Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
    }

    [Fact]
    public void ApprovalFailureCodes_AreStableStrings()
    {
        QualificationFailureCodes.ApprovalInvalid
            .ShouldBe("ApprovalInvalid");
        QualificationFailureCodes.ApprovedBytesUnavailable
            .ShouldBe("ApprovedBytesUnavailable");
        QualificationFailureCodes.Classify(
                new FixtureBundleException(
                    QualificationFailureCodes.ApprovedBytesUnavailable,
                    "upstream no longer serves the approved bytes."))
            .ShouldBe(QualificationFailureCodes.ApprovedBytesUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_ApprovalFile_ReadsEveryField(bool byteOrderMark)
    {
        string pinned = Sha256(s_coreArchive);
        string approved = Sha256(s_republishedCoreArchive);
        string json = $$"""
            {
              "schemaVersion": 1,
              "release": "{{Release}}",
              "approvedBy": "{{ApprovedBy}}",
              "reason": "{{Reason}}",
              "replacements": [
                {
                  "artifact": "{{CoreId}}",
                  "pinnedSha256": "{{pinned}}",
                  "approvedSha256": "{{approved}}",
                  "observedLastModified": "2026-10-15T08:00:00+00:00"
                }
              ]
            }
            """;
        byte[] content = Encoding.UTF8.GetBytes(json);
        if (byteOrderMark)
            content = [0xEF, 0xBB, 0xBF, .. content];

        FixtureApproval approval = FixtureApproval.Parse(content);

        approval.SchemaVersion.ShouldBe(1);
        approval.Release.ShouldBe(Release);
        approval.ApprovedBy.ShouldBe(ApprovedBy);
        approval.Reason.ShouldBe(Reason);
        FixtureApprovalReplacement replacement =
            approval.Replacements.ShouldHaveSingleItem();
        replacement.Artifact.ShouldBe(CoreId);
        replacement.PinnedSha256.ShouldBe(pinned);
        replacement.ApprovedSha256.ShouldBe(approved);
        replacement.ObservedLastModified.ShouldBe(s_republishedUtc);
        approval.ValidateStructure();
    }

    [Theory]
    [InlineData("unknown-property")]
    [InlineData("unknown-replacement-property")]
    [InlineData("missing-reason")]
    [InlineData("comment")]
    [InlineData("trailing-comma")]
    [InlineData("array")]
    [InlineData("truncated")]
    public void Parse_MalformedApproval_IsApprovalInvalid(string shape)
    {
        string replacement = $$"""
            {"artifact":"{{CoreId}}","pinnedSha256":"{{new string('a', 64)}}","approvedSha256":"{{new string('b', 64)}}"}
            """;
        string json = shape switch
        {
            "unknown-property" =>
                $$"""{"schemaVersion":1,"release":"{{Release}}","approvedBy":"a","reason":"r","replacements":[{{replacement}}],"expires":"never"}""",
            "unknown-replacement-property" =>
                $$"""{"schemaVersion":1,"release":"{{Release}}","approvedBy":"a","reason":"r","replacements":[{{replacement.TrimEnd('}')}},"glob":"*"}]}""",
            "missing-reason" =>
                $$"""{"schemaVersion":1,"release":"{{Release}}","approvedBy":"a","replacements":[{{replacement}}]}""",
            "comment" =>
                $$"""{"schemaVersion":1,/* note */"release":"{{Release}}","approvedBy":"a","reason":"r","replacements":[{{replacement}}]}""",
            "trailing-comma" =>
                $$"""{"schemaVersion":1,"release":"{{Release}}","approvedBy":"a","reason":"r","replacements":[{{replacement}}],}""",
            "array" => $"[{replacement}]",
            "truncated" =>
                $$"""{"schemaVersion":1,"release":"{{Release}}""",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        FixtureBundleException exception = Should.Throw<FixtureBundleException>(
            () => FixtureApproval.Parse(Encoding.UTF8.GetBytes(json)));

        exception.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        exception.Message.ShouldStartWith(
            "The approval is not a valid schema version 1 approval: ");
    }

    [Fact]
    public void Parse_JsonNull_IsApprovalInvalid()
    {
        FixtureBundleException exception = Should.Throw<FixtureBundleException>(
            () => FixtureApproval.Parse("null"u8));

        exception.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        exception.Message.ShouldBe("The approval is not a JSON object.");
    }

    [Theory]
    [InlineData("schema-version")]
    [InlineData("release-without-v")]
    [InlineData("release-with-capital-v")]
    [InlineData("release-with-two-components")]
    [InlineData("release-with-suffix")]
    [InlineData("release-with-trailing-newline")]
    [InlineData("release-with-non-ascii-digit")]
    [InlineData("blank-approver")]
    [InlineData("blank-reason")]
    [InlineData("no-replacements")]
    [InlineData("null-replacement")]
    [InlineData("artifact-without-version")]
    [InlineData("artifact-with-empty-name")]
    [InlineData("artifact-with-blank-version")]
    [InlineData("artifact-with-two-separators")]
    [InlineData("artifact-with-star")]
    [InlineData("artifact-with-question-mark")]
    [InlineData("artifact-with-brackets")]
    [InlineData("uppercase-pinned-hash")]
    [InlineData("short-approved-hash")]
    [InlineData("approved-equals-pinned")]
    [InlineData("duplicate-artifact")]
    public void ValidateStructure_EachRule_RejectsTheApproval(string rule)
    {
        (FixtureApproval approval, string message) =
            CreateStructureViolation(rule);

        FixtureBundleException exception = Should.Throw<FixtureBundleException>(
            () => approval.ValidateStructure());

        exception.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        exception.Message.ShouldBe(message);
    }

    [Fact]
    public async Task ValidateAgainst_MatchingReleaseAndPins_Passes()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        FixtureApproval approval = CreateApproval(
        [
            CreateCoreReplacement(),
            new FixtureApprovalReplacement
            {
                Artifact = GuideId,
                PinnedSha256 = Sha256(s_guideArchive),
                ApprovedSha256 = new string('e', 64)
            }
        ]);

        approval.ValidateStructure();
        approval.ValidateAgainst(corpus, Release);
    }

    [Theory]
    [InlineData("other-release")]
    [InlineData("mutable-id")]
    [InlineData("mutable-current-directive")]
    [InlineData("mutable-master-directive")]
    [InlineData("not-in-corpus")]
    [InlineData("local-fixture")]
    [InlineData("pin-mismatch")]
    public async Task ValidateAgainst_EachRule_RejectsTheApproval(string rule)
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        FixtureApprovalReplacement core = CreateCoreReplacement();
        string localId = corpus.LocalFixture.Id;
        (FixtureApprovalReplacement replacement, string releaseTag, string message) =
            rule switch
            {
                "other-release" => (
                    core,
                    OtherRelease,
                    $"The approval is for release {Release}, not release {OtherRelease}."),
                "mutable-id" => (
                    core with { Artifact = CurrentId },
                    Release,
                    $"Approval artifact '{CurrentId}' is a mutable corpus artifact; approvals name immutable artifacts only."),
                "mutable-current-directive" => (
                    core with { Artifact = "hl7.fhir.r6.core#current" },
                    Release,
                    "Approval artifact 'hl7.fhir.r6.core#current' is a mutable corpus artifact; approvals name immutable artifacts only."),
                "mutable-master-directive" => (
                    core with { Artifact = "hl7.fhir.r6.core#current$master" },
                    Release,
                    "Approval artifact 'hl7.fhir.r6.core#current$master' is a mutable corpus artifact; approvals name immutable artifacts only."),
                "not-in-corpus" => (
                    core with { Artifact = "example.fixture.other#1.0.0" },
                    Release,
                    "Approval artifact 'example.fixture.other#1.0.0' is not an immutable corpus artifact."),
                "local-fixture" => (
                    core with { Artifact = localId },
                    Release,
                    $"Approval artifact '{localId}' is not an immutable corpus artifact."),
                "pin-mismatch" => (
                    core with { PinnedSha256 = Sha256(s_guideArchive) },
                    Release,
                    $"Approval for '{CoreId}' records pinned SHA-256 {Sha256(s_guideArchive)}, not the corpus pin {Sha256(s_coreArchive)}."),
                _ => throw new ArgumentOutOfRangeException(nameof(rule))
            };
        FixtureApproval approval = CreateApproval([replacement]);

        FixtureBundleException exception = Should.Throw<FixtureBundleException>(
            () => approval.ValidateAgainst(corpus, releaseTag));

        exception.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        exception.Message.ShouldBe(message);
    }

    [Fact]
    public async Task DeriveAsync_ApprovedBytesServed_WritesAVerifiedRecoveryBundle()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        FixtureBundleManifest baseManifest = ReadManifest(_baseRoot);
        WriteApproval(_approvalPath, CreateApproval());
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, s_republishedCoreArchive, s_republishedUtc);
        string pinned = Sha256(s_coreArchive);
        string approved = Sha256(s_republishedCoreArchive);

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeTrue();
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        derive.Id.ShouldBe("bundle-derive");
        string derivedDigest = Sha256(
            File.ReadAllBytes(
                Path.Combine(_derivedRoot, "bundle-manifest.json")));
        DescribeDetails(derive).ShouldBe(
        [
            $"approval.{CoreId}={DescribeApproval(pinned, approved)}",
            $"baseBundleSha256={baseDigest}",
            $"derivedBundleSha256={derivedDigest}"
        ]);
        upstream.Requests.ToArray().ShouldBe([$"GET {CoreUri}"]);
        QualificationArtifactResult fetched =
            report.Artifacts.ShouldHaveSingleItem();
        fetched.Kind.ShouldBe("immutable");
        fetched.Id.ShouldBe(CoreId);
        fetched.SourceUri.ShouldBe(CoreUri);
        fetched.ExpectedSha256.ShouldBe(approved);
        fetched.ActualSha256.ShouldBe(approved);
        fetched.HashMatch.ShouldBe(true);
        fetched.PublicationUtc.ShouldBe(s_republishedUtc.ToString("O"));
        fetched.Success.ShouldBeTrue();
        AssertReportMatchesSchema(report);

        GetEntryNames(_derivedRoot).ShouldBe(
            ["blobs", "bundle-manifest.json"]);
        GetEntryNames(Path.Combine(_derivedRoot, "blobs")).ShouldBe(
            new[] { approved, Sha256(s_guideArchive), Sha256(s_currentArchive) }
                .Select(sha256 => $"{sha256}.tgz")
                .Order(StringComparer.Ordinal)
                .ToArray());
        FixtureBundleManifest manifest = ReadManifest(_derivedRoot);
        manifest.SchemaVersion.ShouldBe(1);
        manifest.CorpusSha256.ShouldBe(s_corpusSha256);
        manifest.BaseBundleSha256.ShouldBe(baseDigest);
        manifest.Approvals.ShouldNotBeNull();
        FixtureBundleApproval recorded =
            manifest.Approvals!.ShouldHaveSingleItem();
        recorded.ShouldBe(new FixtureBundleApproval
        {
            Artifact = CoreId,
            PinnedSha256 = pinned,
            ApprovedSha256 = approved,
            Release = Release,
            ApprovedBy = ApprovedBy,
            Reason = Reason,
            ApprovalCommit = ApprovalCommit,
            ObservedLastModified = s_republishedUtc
        });
        await using MemoryStream republished = new(
            s_republishedCoreArchive,
            writable: false);
        ArchiveMetrics metrics = await ArchiveMetricsInspector.InspectAsync(
            republished,
            TestContext.Current.CancellationToken);
        manifest.Artifacts.Single(artifact => artifact.Id == CoreId)
            .ShouldBe(
                baseManifest.Artifacts.Single(artifact => artifact.Id == CoreId)
                    with
                    {
                        Sha256 = approved,
                        CompressedBytes = s_republishedCoreArchive.LongLength,
                        ExpandedBytes = metrics.ExpandedBytes,
                        LargestEntryBytes = metrics.LargestEntryBytes,
                        EntryCount = metrics.EntryCount,
                        PublicationUtc = s_republishedUtc
                    });
        manifest.Artifacts
            .Where(artifact => artifact.Id != CoreId)
            .ToArray()
            .ShouldBe(
                baseManifest.Artifacts
                    .Where(artifact => artifact.Id != CoreId)
                    .ToArray());

        VerifiedFixtureBundle verified =
            await FixtureBundleVerifier.VerifyAsync(
                _derivedRoot,
                derivedDigest,
                corpus,
                s_corpusSha256,
                Release,
                TestContext.Current.CancellationToken);
        verified.Approvals.ShouldHaveSingleItem().ShouldBe(recorded);
        verified.EffectiveCorpus.GetImmutable(CoreId).Sha256
            .ShouldBe(approved);
        verified.EffectiveCorpus.GetImmutable(GuideId)
            .ShouldBeSameAs(corpus.GetImmutable(GuideId));
        corpus.GetImmutable(CoreId).Sha256.ShouldBe(pinned);
        File.ReadAllBytes(verified.BlobPaths[CoreId])
            .SequenceEqual(s_republishedCoreArchive)
            .ShouldBeTrue();

        FixtureBundleException withoutTag =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleVerifier.VerifyAsync(
                    _derivedRoot,
                    derivedDigest,
                    corpus,
                    s_corpusSha256,
                    TestContext.Current.CancellationToken));
        withoutTag.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        withoutTag.Message.ShouldBe(
            "The bundle manifest carries approval data, which this harness does not accept.");
        FixtureBundleException otherTag =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleVerifier.VerifyAsync(
                    _derivedRoot,
                    derivedDigest,
                    corpus,
                    s_corpusSha256,
                    OtherRelease,
                    TestContext.Current.CancellationToken));
        otherTag.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        otherTag.Message.ShouldBe(
            $"The bundle's approval for '{CoreId}' is invalid: The approval is for release {Release}, not release {OtherRelease}.");

        (await FixtureBundleVerifier.VerifyAsync(
                _baseRoot,
                baseDigest,
                corpus,
                s_corpusSha256,
                TestContext.Current.CancellationToken))
            .EffectiveCorpus.ShouldBeSameAs(corpus);
        QualificationSummaryWriter.Render(
                report,
                "recover-derive",
                ReportFileName)
            .Markdown.ShouldBe(
                "### recover-derive \u2014 PASSED\n"
                + "\n"
                + "| Artifact | Pinned SHA-256 | Approved SHA-256 | Observed SHA-256 | Result |\n"
                + "|-|-|-|-|-|\n"
                + $"| {CoreId} | {pinned} | {approved} | {approved} | verified |\n"
                + "\n"
                + $"derived bundle sha256:{derivedDigest} from base sha256:{baseDigest}\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryPoint_DeriveWithBaseBundle_DerivesInsteadOfQualifying(
        bool deriveFirst)
    {
        EntryPointFixture fixture = await CreateEntryPointFixtureAsync();
        ImmutableArtifactDefinition definition =
            fixture.Corpus.GetImmutable(UsCoreId);
        FixtureBundleManifest baseManifest = ReadManifest(_baseRoot);
        string summaryPath = Path.Combine(_tempRoot, "entrypoint-summary.md");
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(
            definition.Uri,
            fixture.ApprovedPackage.Archive,
            s_republishedUtc);

        int exitCode = await FhirPkg.Qualification.Program.RunAsync(
            CreateEntryPointArguments(fixture, summaryPath, deriveFirst),
            acquisitionHandler: upstream);

        exitCode.ShouldBe(0);
        QualificationReport report = await ReadReportAsync(
            Path.Combine(_tempRoot, ReportFileName));
        report.Success.ShouldBeTrue();
        report.ValidationOnly.ShouldBeFalse();
        report.CorpusSha256.ShouldBe(fixture.CorpusSha256);
        report.Summary.ShouldBe(new QualificationSummary
        {
            ArtifactCount = 1,
            ArtifactFailures = 0,
            CaseCount = 1,
            CaseFailures = 0
        });
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        derive.Id.ShouldBe("bundle-derive");
        derive.Success.ShouldBeTrue();
        derive.Failures.ShouldBeEmpty();
        string approvedSha256 = Sha256(fixture.ApprovedPackage.Archive);
        QualificationArtifactResult fetched =
            report.Artifacts.ShouldHaveSingleItem();
        fetched.Id.ShouldBe(UsCoreId);
        fetched.Kind.ShouldBe("immutable");
        fetched.SourceUri.ShouldBe(definition.Uri);
        fetched.ExpectedSha256.ShouldBe(approvedSha256);
        fetched.ActualSha256.ShouldBe(approvedSha256);
        fetched.HashMatch.ShouldBe(true);
        fetched.Success.ShouldBeTrue();
        fetched.Failure.ShouldBeNull();
        upstream.Requests.ToArray().ShouldBe([$"GET {definition.Uri}"]);

        string derivedDigest = Sha256(File.ReadAllBytes(
            Path.Combine(_derivedRoot, "bundle-manifest.json")));
        DescribeDetails(derive).ShouldBe(
        [
            $"approval.{UsCoreId}={DescribeApproval(definition.Sha256, approvedSha256)}",
            $"baseBundleSha256={fixture.BaseDigest}",
            $"derivedBundleSha256={derivedDigest}"
        ]);
        FixtureBundleManifest manifest = ReadManifest(_derivedRoot);
        manifest.CorpusSha256.ShouldBe(fixture.CorpusSha256);
        manifest.BaseBundleSha256.ShouldBe(fixture.BaseDigest);
        manifest.Artifacts.Count.ShouldBe(17);
        manifest.Approvals.ShouldNotBeNull();
        FixtureBundleApproval recorded =
            manifest.Approvals!.ShouldHaveSingleItem();
        recorded.ShouldBe(new FixtureBundleApproval
        {
            Artifact = UsCoreId,
            PinnedSha256 = definition.Sha256,
            ApprovedSha256 = approvedSha256,
            Release = Release,
            ApprovedBy = ApprovedBy,
            Reason = Reason,
            ApprovalCommit = ApprovalCommit,
            ObservedLastModified = s_republishedUtc
        });
        manifest.Artifacts
            .Where(artifact => artifact.Id != UsCoreId)
            .ToArray()
            .ShouldBe(baseManifest.Artifacts
                .Where(artifact => artifact.Id != UsCoreId)
                .ToArray());
        GetEntryNames(_derivedRoot).ShouldBe(
            ["blobs", "bundle-manifest.json"]);
        GetEntryNames(Path.Combine(_derivedRoot, "blobs")).ShouldBe(
            manifest.Artifacts
                .Select(artifact => $"{artifact.Sha256}.tgz")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
        VerifiedFixtureBundle verified =
            await FixtureBundleVerifier.VerifyAsync(
                _derivedRoot,
                derivedDigest,
                fixture.Corpus,
                fixture.CorpusSha256,
                Release,
                TestContext.Current.CancellationToken);
        verified.Digest.ShouldBe(derivedDigest);
        verified.Approvals.ShouldHaveSingleItem().ShouldBe(recorded);
        verified.EffectiveCorpus.GetImmutable(UsCoreId).Sha256
            .ShouldBe(approvedSha256);
        File.ReadAllBytes(verified.GetBlobPath(UsCoreId))
            .SequenceEqual(fixture.ApprovedPackage.Archive)
            .ShouldBeTrue();
        File.ReadAllText(summaryPath).ShouldContain(
            $"derived bundle sha256:{derivedDigest} from base sha256:{fixture.BaseDigest}");
    }

    [Fact]
    public async Task EntryPoint_DeriveWithUnapprovedObservedBytes_FailsWithoutDerivedBundle()
    {
        EntryPointFixture fixture = await CreateEntryPointFixtureAsync();
        ImmutableArtifactDefinition definition =
            fixture.Corpus.GetImmutable(UsCoreId);
        byte[] baseManifestBytes = File.ReadAllBytes(
            Path.Combine(_baseRoot, "bundle-manifest.json"));
        VerifiedFixtureBundle original =
            await FixtureBundleVerifier.VerifyAsync(
                _baseRoot,
                fixture.BaseDigest,
                fixture.Corpus,
                fixture.CorpusSha256,
                TestContext.Current.CancellationToken);
        byte[] unapprovedArchive = CreateManifestArchive(
            definition.Name,
            definition.Version,
            "4.0.1",
            "republished-again");
        string approvedSha256 = Sha256(fixture.ApprovedPackage.Archive);
        string observedSha256 = Sha256(unapprovedArchive);
        observedSha256.ShouldNotBe(approvedSha256);
        string summaryPath = Path.Combine(_tempRoot, "entrypoint-summary.md");
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(definition.Uri, unapprovedArchive, s_republishedUtc);

        int exitCode = await FhirPkg.Qualification.Program.RunAsync(
            CreateEntryPointArguments(fixture, summaryPath),
            acquisitionHandler: upstream);

        exitCode.ShouldBe(1);
        QualificationReport report = await ReadReportAsync(
            Path.Combine(_tempRoot, ReportFileName));
        report.Success.ShouldBeFalse();
        report.CorpusSha256.ShouldBe(fixture.CorpusSha256);
        report.Summary.ShouldBe(new QualificationSummary
        {
            ArtifactCount = 1,
            ArtifactFailures = 1,
            CaseCount = 1,
            CaseFailures = 1
        });
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        derive.Id.ShouldBe("bundle-derive");
        derive.Success.ShouldBeFalse();
        DescribeDetails(derive).ShouldBe(
        [
            $"approval.{UsCoreId}={DescribeApproval(definition.Sha256, approvedSha256)}",
            $"baseBundleSha256={fixture.BaseDigest}"
        ]);
        QualificationFailure failure = derive.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.ApprovedBytesUnavailable);
        failure.ExceptionType.ShouldBe(nameof(FixtureBundleException));
        failure.Message.ShouldBe(
            $"upstream no longer serves the approved bytes for {UsCoreId}: observed {observedSha256}");
        QualificationArtifactResult fetched =
            report.Artifacts.ShouldHaveSingleItem();
        fetched.Id.ShouldBe(UsCoreId);
        fetched.ExpectedSha256.ShouldBe(approvedSha256);
        fetched.ActualSha256.ShouldBe(observedSha256);
        fetched.HashMatch.ShouldBe(false);
        fetched.Success.ShouldBeFalse();
        fetched.Failure.ShouldBe(failure);
        upstream.Requests.ToArray().ShouldBe([$"GET {definition.Uri}"]);
        File.Exists(Path.Combine(_derivedRoot, "bundle-manifest.json"))
            .ShouldBeFalse();
        Directory.Exists(_derivedRoot).ShouldBeFalse();
        File.ReadAllText(summaryPath).ShouldContain("no bundle derived");
        File.ReadAllBytes(Path.Combine(_baseRoot, "bundle-manifest.json"))
            .SequenceEqual(baseManifestBytes)
            .ShouldBeTrue();
        VerifiedFixtureBundle preserved =
            await FixtureBundleVerifier.VerifyAsync(
                _baseRoot,
                fixture.BaseDigest,
                fixture.Corpus,
                fixture.CorpusSha256,
                TestContext.Current.CancellationToken);
        preserved.Digest.ShouldBe(fixture.BaseDigest);
        preserved.Manifest.Artifacts.ToArray()
            .ShouldBe(original.Manifest.Artifacts.ToArray());
        preserved.EffectiveCorpus.GetImmutable(UsCoreId).Sha256
            .ShouldBe(definition.Sha256);
    }

    [Theory]
    [InlineData("wrong-release")]
    [InlineData("pin-differs-from-corpus")]
    [InlineData("artifact-not-in-corpus")]
    [InlineData("mutable-artifact")]
    [InlineData("duplicate-artifact")]
    [InlineData("approved-equals-pinned")]
    public async Task DeriveAsync_InvalidApproval_RefusesBeforeFetching(
        string rule)
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        FixtureApprovalReplacement core = CreateCoreReplacement();
        string otherPin = new('d', 64);
        (FixtureApproval approval, string message) = rule switch
        {
            "wrong-release" => (
                CreateApproval() with { Release = OtherRelease },
                $"The approval is for release {OtherRelease}, not release {Release}."),
            "pin-differs-from-corpus" => (
                CreateApproval([core with { PinnedSha256 = otherPin }]),
                $"Approval for '{CoreId}' records pinned SHA-256 {otherPin}, not the corpus pin {core.PinnedSha256}."),
            "artifact-not-in-corpus" => (
                CreateApproval(
                    [core with { Artifact = "example.fixture.other#1.0.0" }]),
                "Approval artifact 'example.fixture.other#1.0.0' is not an immutable corpus artifact."),
            "mutable-artifact" => (
                CreateApproval(
                    [core with { Artifact = "hl7.fhir.r6.core#current" }]),
                "Approval artifact 'hl7.fhir.r6.core#current' is a mutable corpus artifact; approvals name immutable artifacts only."),
            "duplicate-artifact" => (
                CreateApproval(
                [
                    core,
                    core with { ApprovedSha256 = new string('e', 64) }
                ]),
                $"The approval lists artifact '{CoreId}' more than once."),
            "approved-equals-pinned" => (
                CreateApproval(
                    [core with { ApprovedSha256 = core.PinnedSha256 }]),
                $"Approval for '{CoreId}' approves its pinned bytes; the approved SHA-256 must differ from the pin."),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
        WriteApproval(_approvalPath, approval);
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, s_republishedCoreArchive, s_republishedUtc);

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        report.Artifacts.ShouldBeEmpty();
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        derive.Id.ShouldBe("bundle-derive");
        DescribeDetails(derive).ShouldBe([$"baseBundleSha256={baseDigest}"]);
        QualificationFailure failure = derive.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        failure.ExceptionType.ShouldBe(nameof(FixtureBundleException));
        failure.Message.ShouldBe(message);
        upstream.Requests.ShouldBeEmpty();
        Directory.Exists(_derivedRoot).ShouldBeFalse();
        AssertReportMatchesSchema(report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeriveAsync_UnreadableOrMalformedApprovalFile_IsApprovalInvalid(
        bool fileExists)
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        if (fileExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_approvalPath)!);
            File.WriteAllText(_approvalPath, "{\"schemaVersion\":1}");
        }

        using FixtureHttpMessageHandler upstream = new();

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        QualificationFailure failure = report.Cases.ShouldHaveSingleItem()
            .Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(QualificationFailureCodes.ApprovalInvalid);
        failure.Message.ShouldStartWith(
            fileExists
                ? "The approval is not a valid schema version 1 approval: "
                : "The approval file could not be read: ");
        failure.Message.ShouldNotContain(_tempRoot);
        upstream.Requests.ShouldBeEmpty();
        Directory.Exists(_derivedRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task DeriveAsync_UpstreamServesOtherBytes_IsApprovedBytesUnavailable()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        WriteApproval(_approvalPath, CreateApproval());
        byte[] otherBytes = DeterministicPackageArchive.Create(
            CoreName,
            CoreVersion,
            "republished-again");
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, otherBytes, s_republishedUtc);
        string pinned = Sha256(s_coreArchive);
        string approved = Sha256(s_republishedCoreArchive);
        string observed = Sha256(otherBytes);
        string message =
            $"upstream no longer serves the approved bytes for {CoreId}: observed {observed}";

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        upstream.Requests.ToArray().ShouldBe([$"GET {CoreUri}"]);
        QualificationArtifactResult fetched =
            report.Artifacts.ShouldHaveSingleItem();
        fetched.Kind.ShouldBe("immutable");
        fetched.Id.ShouldBe(CoreId);
        fetched.ExpectedSha256.ShouldBe(approved);
        fetched.ActualSha256.ShouldBe(observed);
        fetched.HashMatch.ShouldBe(false);
        fetched.Success.ShouldBeFalse();
        fetched.Failure.ShouldNotBeNull();
        fetched.Failure!.Code.ShouldBe(
            QualificationFailureCodes.ApprovedBytesUnavailable);
        fetched.Failure.Message.ShouldBe(message);
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        DescribeDetails(derive).ShouldBe(
        [
            $"approval.{CoreId}={DescribeApproval(pinned, approved)}",
            $"baseBundleSha256={baseDigest}"
        ]);
        QualificationFailure failure = derive.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.ApprovedBytesUnavailable);
        failure.ExceptionType.ShouldBe(nameof(FixtureBundleException));
        failure.Message.ShouldBe(message);
        Directory.Exists(_derivedRoot).ShouldBeFalse();
        AssertReportMatchesSchema(report);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                "recover-derive",
                ReportFileName);
        content.Markdown.ShouldBe(
            "### recover-derive \u2014 FAILED\n"
            + "\n"
            + "| Artifact | Pinned SHA-256 | Approved SHA-256 | Observed SHA-256 | Result |\n"
            + "|-|-|-|-|-|\n"
            + $"| {CoreId} | {pinned} | {approved} | {observed} | **unavailable** |\n"
            + "\n"
            + "no bundle derived\n");
        content.Annotations.ToArray().ShouldBe(
            [$"::error title=Approved bytes unavailable {CoreId}::{message}"]);
    }

    [Fact]
    public async Task DeriveAsync_UpstreamUnreachable_IsApprovedBytesUnavailable()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        WriteApproval(_approvalPath, CreateApproval());
        using FixtureHttpMessageHandler upstream = new();
        upstream.Respond(CoreUri, HttpStatusCode.NotFound);

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        QualificationArtifactResult fetched =
            report.Artifacts.ShouldHaveSingleItem();
        fetched.Success.ShouldBeFalse();
        fetched.ActualSha256.ShouldBeNull();
        fetched.Failure.ShouldNotBeNull();
        fetched.Failure!.Code.ShouldBe(
            QualificationFailureCodes.SourceUnreachable);
        QualificationFailure failure = report.Cases.ShouldHaveSingleItem()
            .Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.ApprovedBytesUnavailable);
        failure.Message.ShouldBe(
            $"upstream no longer serves the approved bytes for {CoreId}: {fetched.Failure.Message}");
        failure.Message.ShouldContain("404");
        Directory.Exists(_derivedRoot).ShouldBeFalse();
        AssertReportMatchesSchema(report);
    }

    [Fact]
    public async Task DeriveAsync_BaseAlreadyCarriesApprovals_IsRefused()
    {
        DerivedFixture derived = await DeriveSyntheticBundleAsync();
        string rederivedRoot = Path.Combine(_tempRoot, "rederived-bundle");
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, s_republishedCoreArchive, s_republishedUtc);

        QualificationReport report = await CreateDeriveRunner(
                derived.Corpus,
                derived.DerivedDigest,
                upstream,
                "rederive-cache",
                _derivedRoot,
                rederivedRoot)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        report.Artifacts.ShouldBeEmpty();
        QualificationCaseResult derive = report.Cases.ShouldHaveSingleItem();
        derive.Details.ShouldBeEmpty();
        QualificationFailure failure = derive.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        failure.Message.ShouldBe(
            "The bundle manifest carries approval data, which this harness does not accept.");
        upstream.Requests.ShouldBeEmpty();
        Directory.Exists(rederivedRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task DeriveAsync_NonEmptyOutput_IsRefusedAndLeftUntouched()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        WriteApproval(_approvalPath, CreateApproval());
        Directory.CreateDirectory(_derivedRoot);
        string existing = Path.Combine(_derivedRoot, "existing.txt");
        File.WriteAllText(existing, "keep");
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, s_republishedCoreArchive, s_republishedUtc);

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        QualificationFailure failure = report.Cases.ShouldHaveSingleItem()
            .Failures.ShouldHaveSingleItem();
        failure.Message.ShouldBe(
            "The bundle output directory must be absent or empty.");
        GetEntryNames(_derivedRoot).ShouldBe(["existing.txt"]);
        File.ReadAllText(existing).ShouldBe("keep");
    }

    [Theory]
    [InlineData("approved-entry-holds-other-bytes")]
    [InlineData("approvals-without-base")]
    [InlineData("base-without-approvals")]
    [InlineData("empty-approvals")]
    [InlineData("invalid-base")]
    [InlineData("invalid-approval-commit")]
    [InlineData("duplicate-approval")]
    [InlineData("pin-other-than-corpus-pin")]
    [InlineData("approves-pinned-bytes")]
    [InlineData("mutable-approval")]
    [InlineData("blank-approver")]
    public async Task VerifyAsync_DerivedBundle_EachApprovalRule_Fails(
        string rule)
    {
        DerivedFixture derived = await DeriveSyntheticBundleAsync();
        (Action<JsonObject> change, string message) =
            CreateManifestApprovalViolation(rule);
        string digest = RewriteManifest(_derivedRoot, change);

        FixtureBundleException exception =
            await Should.ThrowAsync<FixtureBundleException>(
                () => FixtureBundleVerifier.VerifyAsync(
                    _derivedRoot,
                    digest,
                    derived.Corpus,
                    s_corpusSha256,
                    Release,
                    TestContext.Current.CancellationToken));

        exception.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        exception.Message.ShouldBe(message);
    }

    [Fact]
    public async Task RunBundleAsync_DerivedBundle_QualifiesEveryCaseAgainstTheApprovedPins()
    {
        QualificationCorpus corpus = await CreateHarnessCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateHarnessArchives());
        WriteApproval(
            _approvalPath,
            CreateApproval(
                s_republishedPackages
                    .Select(package => new FixtureApprovalReplacement
                    {
                        Artifact = package.Id,
                        PinnedSha256 = corpus.GetImmutable(package.Id).Sha256,
                        ApprovedSha256 = Sha256(package.Archive),
                        ObservedLastModified = s_republishedUtc
                    })
                    .ToArray()));
        using FixtureHttpMessageHandler upstream = new();
        foreach (SyntheticPackage package in s_republishedPackages)
            upstream.Serve(package.SourceUri, package.Archive, s_republishedUtc);

        QualificationReport derived = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);
        DescribeFailedCases(derived).ShouldBeEmpty();
        string derivedDigest = GetDetail(
            derived.Cases.ShouldHaveSingleItem(),
            "derivedBundleSha256");
        upstream.Requests.ToArray().ShouldBe(
        [
            "GET https://packages2.fhir.org/packages/hl7.fhir.r4b.core/4.3.0",
            "GET https://packages2.fhir.org/packages/hl7.fhir.r5.core/5.0.0",
            "GET https://packages2.fhir.org/packages/hl7.fhir.us.core/6.0.0"
        ]);

        QualificationReport report = await CreateBundleRunner(
                corpus,
                "bundle-cache",
                _derivedRoot,
                derivedDigest,
                Release)
            .RunBundleAsync(
                _derivedRoot,
                derivedDigest,
                Release,
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
        string[] approvals = s_republishedPackages
            .Select(package =>
                $"approval.{package.Id}={DescribeApproval(corpus.GetImmutable(package.Id).Sha256, Sha256(package.Archive))}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        DescribeDetails(report.Cases[0]).ShouldBe(
        [
            .. approvals,
            "approvals=3",
            $"bundleSha256={derivedDigest}",
            $"corpusSha256={s_corpusSha256}"
        ]);
        DescribeFailedCases(report, "12-process-host").ShouldBeEmpty();
        report.Cases.Single(result => result.Id == "12-process-host")
            .Failures.ShouldHaveSingleItem()
            .Message.ShouldBe("The configured process host does not exist.");
        foreach (SyntheticPackage package in s_harnessPackages)
        {
            SyntheticPackage? republished = s_republishedPackages
                .SingleOrDefault(candidate => candidate.Id == package.Id);
            string expected = Sha256((republished ?? package).Archive);
            QualificationArtifactResult artifact = report.Artifacts.Single(
                result => result.Id == package.Id);
            artifact.ExpectedSha256.ShouldBe(expected);
            artifact.ActualSha256.ShouldBe(expected);
            artifact.Success.ShouldBeTrue();
        }

        report.Artifacts.ShouldAllBe(artifact => artifact.Success);
        AssertReportMatchesSchema(report);
        string listed = string.Join(
            ", ",
            s_republishedPackages
                .OrderBy(package => package.Id, StringComparer.Ordinal)
                .Select(package =>
                    $"{package.Id} {corpus.GetImmutable(package.Id).Sha256} {Arrow} {Sha256(package.Archive)}"));
        QualificationSummaryWriter.Render(
                report,
                "qualify-windows-latest-net10.0",
                ReportFileName)
            .Markdown.ShouldContain(
                $"\nQualification inputs: bundle sha256:{derivedDigest} \u00b7 corpus {s_corpusSha256} \u00b7 approvals: {listed}\n");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OtherRelease)]
    public async Task RunBundleAsync_DerivedBundleForAnotherRelease_StopsBeforeAcquisition(
        string? releaseTag)
    {
        DerivedFixture derived = await DeriveSyntheticBundleAsync();

        QualificationReport report = await CreateBundleRunner(
                derived.Corpus,
                "bundle-cache",
                _derivedRoot,
                derived.DerivedDigest,
                releaseTag)
            .RunBundleAsync(
                _derivedRoot,
                derived.DerivedDigest,
                TestContext.Current.CancellationToken);

        report.Success.ShouldBeFalse();
        report.Artifacts.ShouldBeEmpty();
        QualificationCaseResult verify = report.Cases.ShouldHaveSingleItem();
        verify.Id.ShouldBe("bundle-verify");
        verify.Details.ShouldBeEmpty();
        QualificationFailure failure = verify.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            QualificationFailureCodes.BundleVerificationFailed);
        failure.Message.ShouldBe(
            releaseTag is null
                ? "The bundle manifest carries approval data, which this harness does not accept."
                : $"The bundle's approval for '{CoreId}' is invalid: The approval is for release {Release}, not release {OtherRelease}.");
        Directory.Exists(Path.Combine(_tempRoot, "bundle-cache", ".qualification"))
            .ShouldBeFalse();
    }

    [Fact]
    public void Parse_DeriveOptions_AreExposed()
    {
        QualificationArguments derive = QualificationArguments.Parse(
        [
            .. CreateRequiredArguments(),
            .. CreateDeriveOptions()
        ]);
        QualificationArguments live = QualificationArguments.Parse(
            CreateRequiredArguments());

        derive.DeriveBundlePath.ShouldBe(Path.GetFullPath("derived-bundle"));
        derive.ApprovalPath.ShouldBe(
            Path.GetFullPath($"approvals/{Release}.json"));
        derive.ApprovalCommit.ShouldBe(ApprovalCommit);
        derive.ReleaseTag.ShouldBe(Release);
        derive.BundlePath.ShouldBe(Path.GetFullPath("fixture-bundle"));
        derive.BundleSha256.ShouldBe(ValidDigest);
        derive.ValidateOnly.ShouldBeFalse();
        derive.FreezeBundlePath.ShouldBeNull();
        live.DeriveBundlePath.ShouldBeNull();
        live.ApprovalPath.ShouldBeNull();
        live.ApprovalCommit.ShouldBeNull();
        live.ReleaseTag.ShouldBeNull();
    }

    [Fact]
    public void Parse_ReleaseTagWithBundle_IsAcceptedWithoutDerive()
    {
        QualificationArguments bundle = QualificationArguments.Parse(
        [
            .. CreateRequiredArguments(),
            "--bundle",
            "fixture-bundle",
            "--bundle-sha256",
            ValidDigest,
            "--release-tag",
            Release
        ]);

        bundle.ReleaseTag.ShouldBe(Release);
        bundle.BundlePath.ShouldBe(Path.GetFullPath("fixture-bundle"));
        bundle.DeriveBundlePath.ShouldBeNull();
        bundle.ApprovalPath.ShouldBeNull();
        bundle.ApprovalCommit.ShouldBeNull();
    }

    [Theory]
    [InlineData(
        "--bundle",
        "Option '--bundle' is required with '--derive-bundle'.")]
    [InlineData(
        "--bundle-sha256",
        "Option '--bundle-sha256' is required with '--derive-bundle'.")]
    [InlineData(
        "--approval",
        "Option '--approval' is required with '--derive-bundle'.")]
    [InlineData(
        "--approval-commit",
        "Option '--approval-commit' is required with '--derive-bundle'.")]
    [InlineData(
        "--release-tag",
        "Option '--release-tag' is required with '--derive-bundle'.")]
    public void Parse_DeriveWithoutARequiredOption_IsRejected(
        string missing,
        string message)
    {
        string[] options = CreateDeriveOptions(without: missing);

        ArgumentException exception = Should.Throw<ArgumentException>(
            () => QualificationArguments.Parse(
                [.. CreateRequiredArguments(), .. options]));

        exception.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData(
        new[] { "--derive-bundle", " " },
        "Option '--derive-bundle' requires a value.")]
    [InlineData(
        new[] { "--validate-only", "true" },
        "Option '--derive-bundle' cannot be combined with '--validate-only true'.")]
    [InlineData(
        new[] { "--freeze-bundle", "frozen-bundle" },
        "Option '--derive-bundle' cannot be combined with '--freeze-bundle'.")]
    [InlineData(
        new[] { "--approval", " " },
        "Option '--approval' requires a value.")]
    [InlineData(
        new[] { "--approval-commit", "0123456789ABCDEF0123456789ABCDEF01234567" },
        "Option '--approval-commit' must be exactly 40 lowercase hexadecimal characters.")]
    [InlineData(
        new[] { "--approval-commit", ValidDigest },
        "Option '--approval-commit' must be exactly 40 lowercase hexadecimal characters.")]
    [InlineData(
        new[] { "--release-tag", " " },
        "Option '--release-tag' requires a value.")]
    [InlineData(
        new[] { "--bundle-sha256", "ABC" },
        "Option '--bundle-sha256' must be exactly 64 lowercase hexadecimal characters.")]
    public void Parse_DeriveWithAnInvalidOption_IsRejected(
        string[] replacement,
        string message)
    {
        string[] options =
        [
            .. CreateDeriveOptions(without: replacement[0]),
            .. replacement
        ];

        ArgumentException exception = Should.Throw<ArgumentException>(
            () => QualificationArguments.Parse(
                [.. CreateRequiredArguments(), .. options]));

        exception.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", ValidDigest, "--approval", "approval.json" },
        "Option '--approval' requires '--derive-bundle'.")]
    [InlineData(
        new[] { "--bundle", "fixture-bundle", "--bundle-sha256", ValidDigest, "--approval-commit", ApprovalCommit },
        "Option '--approval-commit' requires '--derive-bundle'.")]
    [InlineData(
        new[] { "--release-tag", Release },
        "Option '--release-tag' requires '--bundle'.")]
    public void Parse_DeriveOptionWithoutItsMode_IsRejected(
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

    private static FixtureBundleManifest ReadManifest(string bundleRoot) =>
        JsonSerializer.Deserialize<FixtureBundleManifest>(
            File.ReadAllBytes(
                Path.Combine(bundleRoot, "bundle-manifest.json")),
            QualificationJson.SerializerOptions)
        ?? throw new InvalidDataException("The manifest was empty.");

    private static string DescribeApproval(
        string pinned,
        string approved) =>
        $"{pinned} {Arrow} {approved} by {ApprovedBy} at {ApprovalCommit}";

    private static string[] DescribeDetails(QualificationCaseResult result) =>
        result.Details
            .Select(detail => $"{detail.Name}={detail.Value}")
            .ToArray();

    private static string GetDetail(
        QualificationCaseResult result,
        string name) =>
        result.Details.Single(detail => detail.Name == name).Value;

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

    private static async Task<QualificationReport> ReadReportAsync(string path)
    {
        string json = await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken);
        QualificationSchemaValidator.ValidateJson(
            json,
            QualificationSchemaPaths.Report);
        return JsonSerializer.Deserialize<QualificationReport>(
                json,
                QualificationJson.SerializerOptions)
            ?? throw new InvalidDataException("The report was empty.");
    }

    // The same check source CI applies to every committed approval record.
    private static void AssertApprovalRecordsAreWellFormed(string directory)
    {
        // No approval exists until a recovery needs one.
        if (!Directory.Exists(directory))
            return;

        foreach (string path in Directory
            .EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal))
        {
            FixtureApproval approval =
                FixtureApproval.Parse(File.ReadAllBytes(path));
            approval.ValidateStructure();
            approval.Release.ShouldBe(
                Path.GetFileNameWithoutExtension(path),
                $"Approval record '{Path.GetFileName(path)}' must be named for its release.");
        }
    }

    private static FixtureApprovalReplacement CreateCoreReplacement() =>
        new()
        {
            Artifact = CoreId,
            PinnedSha256 = Sha256(s_coreArchive),
            ApprovedSha256 = Sha256(s_republishedCoreArchive),
            ObservedLastModified = s_republishedUtc
        };

    private static FixtureApproval CreateApproval(
        IReadOnlyList<FixtureApprovalReplacement>? replacements = null) =>
        new()
        {
            SchemaVersion = 1,
            Release = Release,
            ApprovedBy = ApprovedBy,
            Reason = Reason,
            Replacements = replacements ?? [CreateCoreReplacement()]
        };

    private static void WriteApproval(
        string path,
        FixtureApproval approval)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                approval,
                QualificationJson.SerializerOptions));
    }

    private static byte[] CreateManifestArchive(
        string name,
        string version,
        string fhirVersion,
        string marker,
        params (string Name, string Version)[] dependencies)
    {
        string manifest = JsonSerializer.Serialize(new
        {
            name,
            version,
            fhirVersions = new[] { fhirVersion },
            dependencies = dependencies.ToDictionary(
                dependency => dependency.Name,
                dependency => dependency.Version,
                StringComparer.Ordinal)
        });
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", manifest),
            ArbitraryTarBuilder.File("package/marker.txt", marker));
        return archive.ToArray();
    }

    private static SyntheticPackage CreateSyntheticPackage(
        string name,
        string version,
        string fhirVersion,
        string marker,
        params (string Name, string Version)[] dependencies) =>
        new(
            name,
            version,
            CreateManifestArchive(
                name,
                version,
                fhirVersion,
                marker,
                dependencies));

    private static async Task<QualificationCorpus> CreateCorpusAsync(
        IReadOnlyList<ImmutableArtifactDefinition> immutableArtifacts,
        DownstreamClosureDefinition closure)
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
            ImmutableArtifacts = immutableArtifacts,
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
            DownstreamClosure = closure
        };
    }

    private static Task<QualificationCorpus> CreateSyntheticCorpusAsync() =>
        CreateCorpusAsync(
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
            new DownstreamClosureDefinition
            {
                Roots = [],
                ResolverMembers = [],
                RequiredMembers = []
            });

    private static Task<QualificationCorpus> CreateHarnessCorpusAsync() =>
        CreateCorpusAsync(
            s_harnessPackages
                .Select(package => new ImmutableArtifactDefinition
                {
                    Name = package.Name,
                    Version = package.Version,
                    Uri = package.SourceUri,
                    Sha256 = Sha256(package.Archive)
                })
                .ToArray(),
            new DownstreamClosureDefinition
            {
                Roots = [UsCoreId, IpsId],
                ResolverMembers = [R4CoreId, UsCoreId, IpsId],
                RequiredMembers = [R4CoreId, UsCoreId, IpsId]
            });

    private static Dictionary<string, byte[]> CreateSyntheticArchives() =>
        new(StringComparer.Ordinal)
        {
            [CoreId] = s_coreArchive,
            [GuideId] = s_guideArchive,
            [CurrentId] = s_currentArchive,
            [CurrentMasterId] = s_currentArchive
        };

    private static Dictionary<string, byte[]> CreateHarnessArchives()
    {
        Dictionary<string, byte[]> archives = s_harnessPackages.ToDictionary(
            package => package.Id,
            package => package.Archive,
            StringComparer.Ordinal);
        archives[CurrentId] = s_harnessR6Archive;
        archives[CurrentMasterId] = s_harnessR6Archive;
        return archives;
    }

    private async Task<EntryPointFixture> CreateEntryPointFixtureAsync()
    {
        QualificationCorpus repositoryCorpus =
            await QualificationCorpus.LoadAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "qualification-corpus.json"),
                TestContext.Current.CancellationToken);
        Dictionary<string, byte[]> archives = new(StringComparer.Ordinal);
        List<ImmutableArtifactDefinition> immutableArtifacts = [];
        foreach (ImmutableArtifactDefinition definition in
            repositoryCorpus.ImmutableArtifacts)
        {
            string fhirVersion = definition.Name switch
            {
                "hl7.fhir.r4b.core" => "4.3.0",
                "hl7.fhir.r5.core" => "5.0.0",
                _ => "4.0.1"
            };
            byte[] archive = CreateManifestArchive(
                definition.Name,
                definition.Version,
                fhirVersion,
                "entrypoint-pinned");
            archives[definition.Id] = archive;
            immutableArtifacts.Add(definition with { Sha256 = Sha256(archive) });
        }

        foreach (MutableArtifactDefinition definition in
            repositoryCorpus.MutableArtifacts)
        {
            archives[definition.Id] = CreateManifestArchive(
                definition.Name,
                "6.0.0-ballot3",
                "6.0.0-ballot3",
                $"captured-{definition.Id}");
        }

        QualificationCorpus syntheticCorpus = repositoryCorpus with
        {
            ImmutableArtifacts = immutableArtifacts
        };
        string corpusPath = Path.Combine(_tempRoot, "qualification-corpus.json");
        await File.WriteAllBytesAsync(
            corpusPath,
            JsonSerializer.SerializeToUtf8Bytes(
                syntheticCorpus,
                QualificationJson.SerializerOptions),
            TestContext.Current.CancellationToken);
        await QualificationSchemaValidator.ValidateFileAsync(
            corpusPath,
            QualificationSchemaPaths.Corpus,
            TestContext.Current.CancellationToken);
        QualificationCorpus corpus = await QualificationCorpus.LoadAsync(
            corpusPath,
            TestContext.Current.CancellationToken);
        string corpusSha256 = await QualificationCorpusHash.ComputeFileAsync(
            corpusPath,
            TestContext.Current.CancellationToken);
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            archives,
            corpusSha256);
        ImmutableArtifactDefinition approvedDefinition =
            corpus.GetImmutable(UsCoreId);
        SyntheticPackage approved = CreateSyntheticPackage(
            approvedDefinition.Name,
            approvedDefinition.Version,
            "4.0.1",
            "entrypoint-republished");
        string approvedSha256 = Sha256(approved.Archive);
        approvedSha256.ShouldNotBe(approvedDefinition.Sha256);
        WriteApproval(
            _approvalPath,
            CreateApproval(
            [
                new FixtureApprovalReplacement
                {
                    Artifact = approvedDefinition.Id,
                    PinnedSha256 = approvedDefinition.Sha256,
                    ApprovedSha256 = approvedSha256,
                    ObservedLastModified = s_republishedUtc
                }
            ]));
        return new EntryPointFixture(
            corpus,
            corpusPath,
            corpusSha256,
            baseDigest,
            approved);
    }

    private async Task<string> FreezeBaseBundleAsync(
        QualificationCorpus corpus,
        IReadOnlyDictionary<string, byte[]> archives,
        string? corpusSha256 = null)
    {
        string directory = Path.Combine(_tempRoot, "downloads");
        Directory.CreateDirectory(directory);
        Dictionary<string, DownloadedArtifact> artifacts =
            new(StringComparer.Ordinal);
        foreach ((string id, byte[] content) in archives)
        {
            string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tgz");
            await File.WriteAllBytesAsync(
                path,
                content,
                TestContext.Current.CancellationToken);
            ArchiveMetrics metrics = await ArchiveMetricsInspector.InspectAsync(
                path,
                TestContext.Current.CancellationToken);
            artifacts[id] = new DownloadedArtifact(
                id,
                path,
                Sha256(content),
                content.LongLength,
                metrics,
                FinalUri: null,
                PublicationDate: id is CurrentId or CurrentMasterId
                    ? s_capturedUtc
                    : s_frozenUtc);
        }

        return await FixtureBundleWriter.WriteFrozenAsync(
            _baseRoot,
            corpus,
            corpusSha256 ?? s_corpusSha256,
            artifacts,
            s_noEnvironment,
            TestContext.Current.CancellationToken);
    }

    private string[] CreateRequiredArguments(string cacheName = "cache") =>
    [
        "--cache",
        Path.Combine(_tempRoot, cacheName),
        "--output",
        Path.Combine(_tempRoot, ReportFileName)
    ];

    private string[] CreateEntryPointArguments(
        EntryPointFixture fixture,
        string summaryPath,
        bool deriveFirst = false)
    {
        string[] bundleOptions =
        [
            "--bundle",
            _baseRoot,
            "--bundle-sha256",
            fixture.BaseDigest
        ];
        string[] deriveOptions = ["--derive-bundle", _derivedRoot];
        string[] modeOptions = deriveFirst
            ? [.. deriveOptions, .. bundleOptions]
            : [.. bundleOptions, .. deriveOptions];
        return
        [
            .. CreateRequiredArguments("entrypoint-cache"),
            "--corpus",
            fixture.CorpusPath,
            "--summary",
            summaryPath,
            "--summary-title",
            EntryPointTitle,
            "--http-timeout-seconds",
            "30",
            .. modeOptions,
            "--approval",
            _approvalPath,
            "--approval-commit",
            ApprovalCommit,
            "--release-tag",
            Release
        ];
    }

    private static string[] CreateDeriveOptions(string? without = null)
    {
        (string Name, string Value)[] options =
        [
            ("--bundle", "fixture-bundle"),
            ("--bundle-sha256", ValidDigest),
            ("--derive-bundle", "derived-bundle"),
            ("--approval", $"approvals/{Release}.json"),
            ("--approval-commit", ApprovalCommit),
            ("--release-tag", Release)
        ];
        return options
            .Where(option => option.Name != without)
            .SelectMany(option => new[] { option.Name, option.Value })
            .ToArray();
    }

    private QualificationRunner CreateRunner(
        QualificationCorpus corpus,
        string cacheName,
        string[] options,
        HttpMessageHandler? acquisitionHandler = null) =>
        new(
            QualificationArguments.Parse(
            [
                .. CreateRequiredArguments(cacheName),
                "--http-timeout-seconds",
                "30",
                "--process-host",
                Path.Combine(_tempRoot, "missing-process-host.dll"),
                .. options
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
            acquisitionHandler);

    private QualificationRunner CreateDeriveRunner(
        QualificationCorpus corpus,
        string baseDigest,
        HttpMessageHandler upstream,
        string cacheName = "derive-cache",
        string? basePath = null,
        string? derivedPath = null) =>
        CreateRunner(
            corpus,
            cacheName,
            [
                "--bundle",
                basePath ?? _baseRoot,
                "--bundle-sha256",
                baseDigest,
                "--derive-bundle",
                derivedPath ?? _derivedRoot,
                "--approval",
                _approvalPath,
                "--approval-commit",
                ApprovalCommit,
                "--release-tag",
                Release
            ],
            upstream);

    private QualificationRunner CreateBundleRunner(
        QualificationCorpus corpus,
        string cacheName,
        string bundlePath,
        string digest,
        string? releaseTag) =>
        CreateRunner(
            corpus,
            cacheName,
            releaseTag is null
                ? ["--bundle", bundlePath, "--bundle-sha256", digest]
                :
                [
                    "--bundle",
                    bundlePath,
                    "--bundle-sha256",
                    digest,
                    "--release-tag",
                    releaseTag
                ]);

    private async Task<DerivedFixture> DeriveSyntheticBundleAsync()
    {
        QualificationCorpus corpus = await CreateSyntheticCorpusAsync();
        string baseDigest = await FreezeBaseBundleAsync(
            corpus,
            CreateSyntheticArchives());
        WriteApproval(_approvalPath, CreateApproval());
        using FixtureHttpMessageHandler upstream = new();
        upstream.Serve(CoreUri, s_republishedCoreArchive, s_republishedUtc);

        QualificationReport report = await CreateDeriveRunner(
                corpus,
                baseDigest,
                upstream)
            .DeriveAsync(TestContext.Current.CancellationToken);

        DescribeFailedCases(report).ShouldBeEmpty();
        report.Success.ShouldBeTrue();
        return new DerivedFixture(
            corpus,
            baseDigest,
            GetDetail(
                report.Cases.ShouldHaveSingleItem(),
                "derivedBundleSha256"),
            report);
    }

    private static string RewriteManifest(
        string bundleRoot,
        Action<JsonObject> change)
    {
        string path = Path.Combine(bundleRoot, "bundle-manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(path))!
            .AsObject();
        change(manifest);
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(path, bytes);
        return Sha256(bytes);
    }

    private static JsonObject GetFirstApprovalNode(JsonObject manifest) =>
        manifest["approvals"]!.AsArray()[0]!.AsObject();

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

    private static (FixtureApproval Approval, string Message)
        CreateStructureViolation(string rule)
    {
        FixtureApproval valid = CreateApproval();
        FixtureApprovalReplacement core = CreateCoreReplacement();
        return rule switch
        {
            "schema-version" => (
                valid with { SchemaVersion = 2 },
                "The approval declares schema version 2; only version 1 is supported."),
            "release-without-v" => CreateReleaseViolation("2026.1016.1417"),
            "release-with-capital-v" =>
                CreateReleaseViolation("V2026.1016.1417"),
            "release-with-two-components" =>
                CreateReleaseViolation("v2026.1016"),
            "release-with-suffix" =>
                CreateReleaseViolation("v2026.1016.1417-rc1"),
            "release-with-trailing-newline" =>
                CreateReleaseViolation("v2026.1016.1417\n"),
            "release-with-non-ascii-digit" =>
                CreateReleaseViolation("v2026.1016.141\u0667"),
            "blank-approver" => (
                valid with { ApprovedBy = " " },
                "The approval names no approver."),
            "blank-reason" => (
                valid with { Reason = "" },
                "The approval gives no reason."),
            "no-replacements" => (
                valid with { Replacements = [] },
                "The approval lists no replacements."),
            "null-replacement" => (
                valid with { Replacements = [null!] },
                "The approval lists an empty replacement."),
            "artifact-without-version" =>
                CreateArtifactViolation(core, CoreName, wildcard: false),
            "artifact-with-empty-name" =>
                CreateArtifactViolation(core, "#1.0.0", wildcard: false),
            "artifact-with-blank-version" => CreateArtifactViolation(
                core,
                "example.fixture.core# ",
                wildcard: false),
            "artifact-with-two-separators" => CreateArtifactViolation(
                core,
                "example.fixture.core#1.0.0#2",
                wildcard: false),
            "artifact-with-star" => CreateArtifactViolation(
                core,
                "example.fixture.core#1.0.*",
                wildcard: true),
            "artifact-with-question-mark" => CreateArtifactViolation(
                core,
                "example.fixture.c?re#1.0.0",
                wildcard: true),
            "artifact-with-brackets" => CreateArtifactViolation(
                core,
                "example.fixture.core#1.0.[01]",
                wildcard: true),
            "uppercase-pinned-hash" => (
                CreateApproval([core with { PinnedSha256 = new string('A', 64) }]),
                $"Approval for '{CoreId}' records a pinned SHA-256 that is not 64 lowercase hexadecimal characters."),
            "short-approved-hash" => (
                CreateApproval([core with { ApprovedSha256 = new string('b', 63) }]),
                $"Approval for '{CoreId}' records an approved SHA-256 that is not 64 lowercase hexadecimal characters."),
            "approved-equals-pinned" => (
                CreateApproval([core with { ApprovedSha256 = core.PinnedSha256 }]),
                $"Approval for '{CoreId}' approves its pinned bytes; the approved SHA-256 must differ from the pin."),
            "duplicate-artifact" => (
                CreateApproval(
                [
                    core,
                    core with { ApprovedSha256 = new string('e', 64) }
                ]),
                $"The approval lists artifact '{CoreId}' more than once."),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }

    private static (FixtureApproval Approval, string Message)
        CreateReleaseViolation(string release) =>
        (
            CreateApproval() with { Release = release },
            $"The approval release '{release}' is not a release tag of the form v<major>.<minor>.<patch>.");

    private static (FixtureApproval Approval, string Message)
        CreateArtifactViolation(
            FixtureApprovalReplacement core,
            string artifact,
            bool wildcard) =>
        (
            CreateApproval([core with { Artifact = artifact }]),
            wildcard
                ? $"Approval artifact '{artifact}' contains one of *?[]; approvals name exact artifacts only."
                : $"Approval artifact '{artifact}' does not name one exact name#version.");

    private static (Action<JsonObject> Change, string Message)
        CreateManifestApprovalViolation(string rule)
    {
        string pinned = Sha256(s_coreArchive);
        string approved = Sha256(s_republishedCoreArchive);
        string guide = Sha256(s_guideArchive);
        string otherPin = new('d', 64);
        const string mutable = "hl7.fhir.r6.core#current";
        return rule switch
        {
            "approved-entry-holds-other-bytes" => (
                manifest => GetArtifactNode(manifest, CoreId)["sha256"] = guide,
                $"Bundle artifact '{CoreId}' records SHA-256 {guide}, not its approved SHA-256 {approved}."),
            "approvals-without-base" => (
                manifest => manifest.Remove("baseBundleSha256"),
                "The bundle manifest lists approvals without a base bundle."),
            "base-without-approvals" => (
                manifest => manifest.Remove("approvals"),
                "The bundle manifest records a base bundle but no approvals."),
            "empty-approvals" => (
                manifest => manifest["approvals"] = new JsonArray(),
                "The bundle manifest records a base bundle but no approvals."),
            "invalid-base" => (
                manifest => manifest["baseBundleSha256"] = "ABC",
                "The bundle manifest records a base bundle SHA-256 that is not 64 lowercase hexadecimal characters."),
            "invalid-approval-commit" => (
                manifest => GetFirstApprovalNode(manifest)["approvalCommit"] =
                    ApprovalCommit.ToUpperInvariant(),
                $"The bundle's approval for '{CoreId}' records an approval commit that is not 40 lowercase hexadecimal characters."),
            "duplicate-approval" => (
                manifest => manifest["approvals"]!.AsArray().Add(
                    GetFirstApprovalNode(manifest).DeepClone()),
                $"The bundle approves artifact '{CoreId}' more than once."),
            "pin-other-than-corpus-pin" => (
                manifest => GetFirstApprovalNode(manifest)["pinnedSha256"] =
                    otherPin,
                $"The bundle's approval for '{CoreId}' is invalid: Approval for '{CoreId}' records pinned SHA-256 {otherPin}, not the corpus pin {pinned}."),
            "approves-pinned-bytes" => (
                manifest => GetFirstApprovalNode(manifest)["approvedSha256"] =
                    pinned,
                $"The bundle's approval for '{CoreId}' is invalid: Approval for '{CoreId}' approves its pinned bytes; the approved SHA-256 must differ from the pin."),
            "mutable-approval" => (
                manifest => GetFirstApprovalNode(manifest)["artifact"] = mutable,
                $"The bundle's approval for '{mutable}' is invalid: Approval artifact '{mutable}' is a mutable corpus artifact; approvals name immutable artifacts only."),
            "blank-approver" => (
                manifest => GetFirstApprovalNode(manifest)["approvedBy"] = " ",
                $"The bundle's approval for '{CoreId}' is invalid: The approval names no approver."),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
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

    private sealed record DerivedFixture(
        QualificationCorpus Corpus,
        string BaseDigest,
        string DerivedDigest,
        QualificationReport Report);

    private sealed record EntryPointFixture(
        QualificationCorpus Corpus,
        string CorpusPath,
        string CorpusSha256,
        string BaseDigest,
        SyntheticPackage ApprovedPackage);

    private sealed record FixtureResponse(
        HttpStatusCode StatusCode,
        byte[] Content,
        DateTimeOffset? LastModified);

    // Stands in for the live upstream, so no test reaches a host.
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
