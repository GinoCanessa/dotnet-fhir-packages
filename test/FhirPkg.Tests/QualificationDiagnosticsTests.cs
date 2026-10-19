// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirPkg.Qualification;
using FhirPkg.Qualification.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class QualificationDiagnosticsTests : IDisposable
{
    private const string SummaryTitle = "qualify-ubuntu-latest-net10.0";
    private const string DeriveTitle = "recover-derive";
    private const string ReportFileName = "qualification-report.json";
    private const string UsCoreId = "hl7.fhir.us.core#6.0.0";
    private const string UsCoreUnavailable =
        "Required artifact 'hl7.fhir.us.core#6.0.0' was unavailable.";
    private const string ApprovalCommit =
        "0123456789abcdef0123456789abcdef01234567";

    private static readonly string s_pinnedSha256 = new('a', 64);
    private static readonly string s_observedSha256 = new('b', 64);
    private static readonly string s_corpusSha256 = new('0', 64);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-qualification-diagnostics-{Guid.NewGuid():N}");

    public QualificationDiagnosticsTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void FailureCodes_AreStableStrings()
    {
        QualificationFailureCodes.ImmutableHashMismatch
            .ShouldBe("ImmutableHashMismatch");
        QualificationFailureCodes.LocalFixtureHashMismatch
            .ShouldBe("LocalFixtureHashMismatch");
        QualificationFailureCodes.SourceUnreachable
            .ShouldBe("SourceUnreachable");
        QualificationFailureCodes.RequiredArtifactUnavailable
            .ShouldBe("RequiredArtifactUnavailable");
    }

    [Fact]
    public void Classify_NetworkFailures_AreSourceUnreachable()
    {
        QualificationFailureCodes.Classify(
                new HttpRequestException("No such host is known."))
            .ShouldBe(QualificationFailureCodes.SourceUnreachable);
        QualificationFailureCodes.Classify(
                new TimeoutException(
                    "Artifact exceeded the qualification HTTP timeout."))
            .ShouldBe(QualificationFailureCodes.SourceUnreachable);
    }

    [Fact]
    public void Classify_RequiredArtifactUnavailable_IsRequiredArtifactUnavailable()
    {
        QualificationFailureCodes.Classify(
                new RequiredArtifactUnavailableException(UsCoreId))
            .ShouldBe(
                QualificationFailureCodes.RequiredArtifactUnavailable);
    }

    [Fact]
    public void Classify_OtherExceptions_AreUnclassified()
    {
        QualificationFailureCodes.Classify(
                new QualificationInvariantException("Invariant failed."))
            .ShouldBeNull();
        QualificationFailureCodes.Classify(
                new InvalidDataException("The archive was invalid."))
            .ShouldBeNull();
        QualificationFailureCodes.Classify(
                new OperationCanceledException())
            .ShouldBeNull();
    }

    [Fact]
    public void RequireArtifact_Unavailable_ThrowsTypedExceptionAndRecordsDetail()
    {
        QualificationCaseResult result = new()
        {
            Id = "06-downstream-direct-streams",
            Success = false,
            DurationMilliseconds = 0,
            Details = [],
            Failures = []
        };
        QualificationRunner.QualificationCaseContext context = new(
            result.Id,
            CreateRunner(),
            result);

        RequiredArtifactUnavailableException exception =
            Should.Throw<RequiredArtifactUnavailableException>(
                () => context.RequireArtifact(UsCoreId));

        exception.Message.ShouldBe(UsCoreUnavailable);
        exception.ArtifactId.ShouldBe(UsCoreId);
        QualificationDetail detail =
            result.Details.ShouldHaveSingleItem();
        detail.Name.ShouldBe("requiredArtifact");
        detail.Value.ShouldBe(UsCoreId);
    }

    [Fact]
    public async Task ArtifactDownloader_InjectedHandler_RecordsSha256AndPublicationDate()
    {
        byte[] archive = DeterministicPackageArchive.Create(
            "example.diagnostics",
            "1.0.0",
            "diagnostics");
        DateTimeOffset lastModified =
            new(2026, 7, 1, 12, 30, 15, TimeSpan.Zero);
        Uri uri = new(
            "https://packages.example.test/example.diagnostics/1.0.0");
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            archive,
            lastModified);
        DownloadedArtifact artifact;
        using (ArtifactDownloader downloader = new(
            Path.Combine(_tempRoot, "downloads"),
            TimeSpan.FromSeconds(30),
            handler))
        {
            artifact = await downloader.DownloadAsync(
                "example.diagnostics#1.0.0",
                uri,
                TestContext.Current.CancellationToken);
        }

        artifact.ActualSha256.ShouldBe(
            Convert.ToHexString(SHA256.HashData(archive))
                .ToLowerInvariant());
        artifact.PublicationDate.ShouldBe(lastModified);
        artifact.CompressedBytes.ShouldBe(archive.LongLength);
        File.ReadAllBytes(artifact.FilePath)
            .SequenceEqual(archive)
            .ShouldBeTrue();
        handler.RequestUris.ShouldHaveSingleItem().ShouldBe(uri);
        handler.WasDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task ArtifactDownloader_NotFound_ThrowsSourceUnreachableFailure()
    {
        string downloadRoot = Path.Combine(_tempRoot, "downloads");
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.NotFound,
            [],
            lastModified: null);
        HttpRequestException exception;
        using (ArtifactDownloader downloader = new(
            downloadRoot,
            TimeSpan.FromSeconds(30),
            handler))
        {
            exception = await Should.ThrowAsync<HttpRequestException>(
                () => downloader.DownloadAsync(
                    "missing.package#1.0.0",
                    new Uri(
                        "https://packages.example.test/missing.package/1.0.0"),
                    TestContext.Current.CancellationToken));
        }

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        QualificationFailureCodes.Classify(exception)
            .ShouldBe(QualificationFailureCodes.SourceUnreachable);
        Directory.EnumerateFileSystemEntries(downloadRoot)
            .ShouldBeEmpty();
        handler.WasDisposed.ShouldBeFalse();
    }

    [Fact]
    public void Render_FailedReport_SeparatesDependentFromIndependentCases()
    {
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_pinnedSha256,
                    s_observedSha256,
                    success: false,
                    CreateHashMismatchFailure())
            ],
            [
                CreateCase(
                    "01-immutable-expected-stream",
                    [CreateRequiredArtifactFailure()],
                    [CreateRequiredArtifactDetail(UsCoreId)]),
                CreateCase(
                    "02-directive-exact",
                    [
                        CreateFailure(
                            "ChecksumMismatch",
                            "Package checksum did not match.",
                            "PackageInstallException")
                    ]),
                CreateCase("03-uri-sources", []),
                CreateCase(
                    "05-dependencies-closure",
                    [
                        CreateRequiredArtifactFailure(),
                        CreateFailure(
                            QualificationFailureCodes.SourceUnreachable,
                            "Response status code does not indicate success: 404 (Not Found).",
                            nameof(HttpRequestException))
                    ],
                    [CreateRequiredArtifactDetail(UsCoreId)])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            "### qualify-ubuntu-latest-net10.0 \u2014 FAILED\n"
            + "\n"
            + $"Qualification inputs: live upstream (no bundle) \u00b7 corpus {s_corpusSha256}\n"
            + "\n"
            + "#### Rejected artifacts\n"
            + "\n"
            + "| Artifact | Code | Expected SHA-256 | Actual SHA-256 |\n"
            + "|-|-|-|-|\n"
            + $"| hl7.fhir.us.core#6.0.0 | ImmutableHashMismatch | {s_pinnedSha256} | {s_observedSha256} |\n"
            + "\n"
            + "#### Failed cases\n"
            + "\n"
            + "| Case | Classification | Message |\n"
            + "|-|-|-|\n"
            + $"| 01-immutable-expected-stream | dependent on hl7.fhir.us.core#6.0.0 | {UsCoreUnavailable} |\n"
            + "| 02-directive-exact | independent | Package checksum did not match. |\n"
            + $"| 05-dependencies-closure | independent | {UsCoreUnavailable} Response status code does not indicate success: 404 (Not Found). |\n"
            + "\n"
            + "1 of 4 cases passed \u00b7 full detail: qualification-report.json\n");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Rejected fixture hl7.fhir.us.core#6.0.0::ImmutableHashMismatch expected {s_pinnedSha256} actual {s_observedSha256}",
            "::error title=Qualification case 02-directive-exact failed::Package checksum did not match.",
            $"::error title=Qualification case 05-dependencies-closure failed::{UsCoreUnavailable} Response status code does not indicate success: 404 (Not Found)."
        ]);
    }

    [Fact]
    public void Render_RejectedArtifacts_ListEveryFailedArtifact()
    {
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    "hl7.fhir.r4.core#4.0.1",
                    s_pinnedSha256,
                    s_pinnedSha256,
                    success: true),
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_pinnedSha256,
                    s_observedSha256,
                    success: false,
                    CreateHashMismatchFailure()),
                CreateArtifact(
                    "immutable",
                    "hl7.fhir.uv.ips#1.1.0",
                    s_pinnedSha256,
                    actualSha256: null,
                    success: false,
                    CreateFailure(
                        QualificationFailureCodes.SourceUnreachable,
                        "Response status code does not indicate success: 404 (Not Found).",
                        nameof(HttpRequestException))),
                CreateArtifact(
                    "local",
                    "local.qualification.dev#dev",
                    s_pinnedSha256,
                    s_observedSha256,
                    success: false,
                    CreateFailure(
                        QualificationFailureCodes.LocalFixtureHashMismatch,
                        $"Generated local fixture SHA-256 was {s_observedSha256}.")),
                CreateArtifact(
                    "mutable",
                    "r6-current",
                    expectedSha256: null,
                    actualSha256: null,
                    success: false,
                    CreateFailure(
                        code: null,
                        "Artifact 'r6-current' exceeds the qualification download limit.",
                        nameof(InvalidDataException)))
            ],
            [CreateCase("03-uri-sources", [])]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldContain(
            "#### Rejected artifacts\n"
            + "\n"
            + "| Artifact | Code | Expected SHA-256 | Actual SHA-256 |\n"
            + "|-|-|-|-|\n"
            + $"| hl7.fhir.us.core#6.0.0 | ImmutableHashMismatch | {s_pinnedSha256} | {s_observedSha256} |\n"
            + $"| hl7.fhir.uv.ips#1.1.0 | SourceUnreachable | {s_pinnedSha256} | none |\n"
            + $"| local.qualification.dev#dev | LocalFixtureHashMismatch | {s_pinnedSha256} | {s_observedSha256} |\n"
            + "| r6-current | unclassified (InvalidDataException) | none | none |\n"
            + "\n");
        content.Markdown.ShouldNotContain("hl7.fhir.r4.core#4.0.1");
        content.Markdown.ShouldNotContain("#### Failed cases");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Rejected fixture hl7.fhir.us.core#6.0.0::ImmutableHashMismatch expected {s_pinnedSha256} actual {s_observedSha256}",
            $"::error title=Rejected fixture hl7.fhir.uv.ips#1.1.0::SourceUnreachable expected {s_pinnedSha256} actual none",
            $"::error title=Rejected fixture local.qualification.dev#dev::LocalFixtureHashMismatch expected {s_pinnedSha256} actual {s_observedSha256}",
            "::error title=Rejected fixture r6-current::unclassified (InvalidDataException) expected none actual none"
        ]);
    }

    [Fact]
    public void Render_Annotations_EscapeWorkflowCommandValues()
    {
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    "example:fixture,one#1.0.0",
                    s_pinnedSha256,
                    s_observedSha256,
                    success: false,
                    CreateHashMismatchFailure())
            ],
            [
                CreateCase(
                    "case:one,two%",
                    [
                        CreateFailure(
                            code: null,
                            "100% failed\r\nnext: a, b\nlast")
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Rejected fixture example%3Afixture%2Cone#1.0.0::ImmutableHashMismatch expected {s_pinnedSha256} actual {s_observedSha256}",
            "::error title=Qualification case case%3Aone%2Ctwo%25 failed::100%25 failed%0D%0Anext: a, b%0Alast"
        ]);
    }

    [Fact]
    public void Render_MarkdownText_IsEscapedForTablesAndHtml()
    {
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "04-discovery-stream-checksum",
                    [
                        CreateFailure(
                            code: null,
                            "Could not read <local-path>|archive.tgz\r\nnext & last \\ line")
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                "qualify <a|b>",
                ReportFileName);

        content.Markdown.ShouldStartWith(
            "### qualify &lt;a\\|b&gt; \u2014 FAILED\n");
        content.Markdown.ShouldContain(
            @"| 04-discovery-stream-checksum | independent | Could not read &lt;local-path&gt;\|archive.tgz next &amp; last \\ line |"
            + "\n");
    }

    [Fact]
    public void Render_PassingReport_EmitsNoAnnotations()
    {
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_pinnedSha256,
                    s_pinnedSha256,
                    success: true)
            ],
            [
                CreateCase("01-immutable-expected-stream", []),
                CreateCase("02-directive-exact", [])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Annotations.ShouldBeEmpty();
        content.Markdown.ShouldBe(
            "### qualify-ubuntu-latest-net10.0 \u2014 PASSED\n"
            + "\n"
            + $"Qualification inputs: live upstream (no bundle) \u00b7 corpus {s_corpusSha256}\n"
            + "\n"
            + "2 of 2 cases passed \u00b7 full detail: qualification-report.json\n");
    }

    [Fact]
    public void Render_PassingBundleVerify_NamesBundleCorpusAndApprovals()
    {
        string bundleSha256 = new('f', 64);
        string corpusSha256 = new('c', 64);
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "bundle-verify",
                    [],
                    [
                        CreateDetail("approvals", "none"),
                        CreateDetail("bundleSha256", bundleSha256),
                        CreateDetail("corpusSha256", corpusSha256)
                    ]),
                CreateCase("01-immutable-expected-stream", [])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Annotations.ShouldBeEmpty();
        content.Markdown.ShouldBe(
            "### qualify-ubuntu-latest-net10.0 \u2014 PASSED\n"
            + "\n"
            + $"Qualification inputs: bundle sha256:{bundleSha256} \u00b7 corpus {corpusSha256} \u00b7 approvals: none\n"
            + "\n"
            + "2 of 2 cases passed \u00b7 full detail: qualification-report.json\n");
    }

    [Fact]
    public void Render_PassingBundleVerifyWithApprovals_ListsEachApprovalInTheInputs()
    {
        string bundleSha256 = new('f', 64);
        string corpusSha256 = new('c', 64);
        string ipsPinned = new('1', 64);
        string ipsApproved = new('2', 64);
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "bundle-verify",
                    [],
                    [
                        CreateDetail(
                            $"approval.{UsCoreId}",
                            $"{s_pinnedSha256} \u2192 {s_observedSha256} by GinoCanessa at {ApprovalCommit}"),
                        CreateDetail(
                            "approval.hl7.fhir.uv.ips#1.1.0",
                            $"{ipsPinned} \u2192 {ipsApproved} by Release Manager at {ApprovalCommit}"),
                        CreateDetail("approvals", "2"),
                        CreateDetail("bundleSha256", bundleSha256),
                        CreateDetail("corpusSha256", corpusSha256)
                    ]),
                CreateCase("01-immutable-expected-stream", [])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Annotations.ShouldBeEmpty();
        content.Markdown.ShouldBe(
            "### qualify-ubuntu-latest-net10.0 \u2014 PASSED\n"
            + "\n"
            + $"Qualification inputs: bundle sha256:{bundleSha256} \u00b7 corpus {corpusSha256} \u00b7 approvals: "
            + $"{UsCoreId} {s_pinnedSha256} \u2192 {s_observedSha256}, "
            + $"hl7.fhir.uv.ips#1.1.0 {ipsPinned} \u2192 {ipsApproved}\n"
            + "\n"
            + "2 of 2 cases passed \u00b7 full detail: qualification-report.json\n");
    }

    [Fact]
    public void Render_DeriveReport_Succeeded_RendersTheDeriveTableAndFooter()
    {
        string baseSha256 = new('d', 64);
        string derivedSha256 = new('e', 64);
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_observedSha256,
                    s_observedSha256,
                    success: true)
            ],
            [
                CreateCase(
                    "bundle-derive",
                    [],
                    [
                        CreateDetail(
                            $"approval.{UsCoreId}",
                            $"{s_pinnedSha256} \u2192 {s_observedSha256} by GinoCanessa at {ApprovalCommit}"),
                        CreateDetail("baseBundleSha256", baseSha256),
                        CreateDetail("derivedBundleSha256", derivedSha256)
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                DeriveTitle,
                ReportFileName);

        content.Annotations.ShouldBeEmpty();
        content.Markdown.ShouldBe(
            "### recover-derive \u2014 PASSED\n"
            + "\n"
            + "| Artifact | Pinned SHA-256 | Approved SHA-256 | Observed SHA-256 | Result |\n"
            + "|-|-|-|-|-|\n"
            + $"| {UsCoreId} | {s_pinnedSha256} | {s_observedSha256} | {s_observedSha256} | verified |\n"
            + "\n"
            + $"derived bundle sha256:{derivedSha256} from base sha256:{baseSha256}\n");
    }

    [Fact]
    public void Render_DeriveReport_ApprovedBytesUnavailable_ShowsTheRowAndNoBundle()
    {
        string baseSha256 = new('d', 64);
        string servedSha256 = new('9', 64);
        string message =
            $"upstream no longer serves the approved bytes for {UsCoreId}: observed {servedSha256}";
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_observedSha256,
                    servedSha256,
                    success: false,
                    CreateFailure(
                        QualificationFailureCodes.ApprovedBytesUnavailable,
                        message,
                        nameof(FixtureBundleException)))
            ],
            [
                CreateCase(
                    "bundle-derive",
                    [
                        CreateFailure(
                            QualificationFailureCodes.ApprovedBytesUnavailable,
                            message,
                            nameof(FixtureBundleException))
                    ],
                    [
                        CreateDetail(
                            $"approval.{UsCoreId}",
                            $"{s_pinnedSha256} \u2192 {s_observedSha256} by GinoCanessa at {ApprovalCommit}"),
                        CreateDetail("baseBundleSha256", baseSha256)
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                DeriveTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            "### recover-derive \u2014 FAILED\n"
            + "\n"
            + "| Artifact | Pinned SHA-256 | Approved SHA-256 | Observed SHA-256 | Result |\n"
            + "|-|-|-|-|-|\n"
            + $"| {UsCoreId} | {s_pinnedSha256} | {s_observedSha256} | {servedSha256} | **unavailable** |\n"
            + "\n"
            + "no bundle derived\n");
        content.Annotations.ToArray().ShouldBe(
            [$"::error title=Approved bytes unavailable {UsCoreId}::{message}"]);
    }

    [Fact]
    public void Render_DeriveReport_RefusedApproval_ShowsTheFailureWithoutATable()
    {
        const string message =
            "The approval is for release v2026.1016.1500, not release v2026.1016.1417.";
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "bundle-derive",
                    [
                        CreateFailure(
                            QualificationFailureCodes.ApprovalInvalid,
                            message,
                            nameof(FixtureBundleException))
                    ],
                    [CreateDetail("baseBundleSha256", new string('d', 64))])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                DeriveTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            "### recover-derive \u2014 FAILED\n"
            + "\n"
            + $"bundle-derive failed: {message}\n"
            + "\n"
            + "no bundle derived\n");
        content.Annotations.ToArray().ShouldBe(
            [$"::error title=Qualification case bundle-derive failed::{message}"]);
    }

    [Fact]
    public void Render_WithoutBundleVerify_NamesLiveUpstream()
    {
        QualificationReport report = CreateReport(
            [],
            [CreateCase("02-directive-exact", [])]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldContain(
            $"\n\nQualification inputs: live upstream (no bundle) \u00b7 corpus {s_corpusSha256}\n\n");
        content.Markdown.ShouldNotContain("bundle sha256:");
    }

    [Fact]
    public void Render_FailedBundleVerify_IsAnIndependentFailureWithOneAnnotation()
    {
        string message =
            $"The bundle manifest SHA-256 was {s_observedSha256}, not the expected {s_pinnedSha256}.";
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "bundle-verify",
                    [
                        CreateFailure(
                            QualificationFailureCodes.BundleVerificationFailed,
                            message,
                            nameof(FixtureBundleException))
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            "### qualify-ubuntu-latest-net10.0 \u2014 FAILED\n"
            + "\n"
            + $"Qualification inputs: bundle sha256:none \u00b7 corpus {s_corpusSha256} \u00b7 approvals: none\n"
            + "\n"
            + "#### Failed cases\n"
            + "\n"
            + "| Case | Classification | Message |\n"
            + "|-|-|-|\n"
            + $"| bundle-verify | independent | {message} |\n"
            + "\n"
            + "0 of 1 cases passed \u00b7 full detail: qualification-report.json\n");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Qualification case bundle-verify failed::{message}"
        ]);
    }

    [Fact]
    public void Render_UnroutedFixtureRequest_IsAnIndependentFailure()
    {
        const string message =
            "Unrouted fixture request(s): GET https://packages2.fhir.org/packages/example.unpinned";
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "05-dependencies-closure",
                    [
                        CreateFailure(
                            QualificationFailureCodes.UnroutedFixtureRequest,
                            message)
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldContain(
            $"| 05-dependencies-closure | independent | {message} |\n");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Qualification case 05-dependencies-closure failed::{message}"
        ]);
    }

    [Fact]
    public void Render_BootstrapFailure_OmitsTheQualificationInputs()
    {
        const string message =
            "Option '--bundle-sha256' is required with '--bundle'.";
        QualificationReport report = CreateReport(
            [],
            [
                CreateCase(
                    "bootstrap",
                    [
                        CreateFailure(
                            code: null,
                            message,
                            nameof(ArgumentException))
                    ])
            ]);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);

        content.Markdown.ShouldStartWith(
            "### qualify-ubuntu-latest-net10.0 \u2014 FAILED\n\n#### Failed cases\n");
        content.Markdown.ShouldNotContain("Qualification inputs");
        content.Annotations.ToArray().ShouldBe(
        [
            $"::error title=Qualification case bootstrap failed::{message}"
        ]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_ValidateOnlyReport_RendersHeadingAndCaseCountsOnly(
        bool passed)
    {
        List<QualificationFailure> failures = passed
            ? []
            :
            [
                CreateFailure(
                    code: null,
                    "The deterministic local fixture hash did not match the corpus pin.")
            ];
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "local",
                    "local.qualification.dev#dev",
                    s_pinnedSha256,
                    passed ? s_pinnedSha256 : s_observedSha256,
                    success: passed)
            ],
            [CreateCase("validate-only", failures)],
            validationOnly: true);

        QualificationSummaryContent content =
            QualificationSummaryWriter.Render(
                report,
                QualificationArguments.DefaultSummaryTitle,
                ReportFileName);

        content.Markdown.ShouldBe(
            passed
                ? "### qualification \u2014 PASSED\n\n1 of 1 cases passed\n"
                : "### qualification \u2014 FAILED\n\n0 of 1 cases passed\n");
        content.Annotations.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteAsync_ExistingSummary_AppendsWithoutTruncating()
    {
        string summaryPath = Path.Combine(_tempRoot, "step-summary.md");
        File.WriteAllText(summaryPath, "### Candidate\n");
        QualificationReport report = CreateReport(
            [
                CreateArtifact(
                    "immutable",
                    UsCoreId,
                    s_pinnedSha256,
                    s_observedSha256,
                    success: false,
                    CreateHashMismatchFailure())
            ],
            [CreateCase("02-directive-exact", [])]);
        QualificationSummaryContent expected =
            QualificationSummaryWriter.Render(
                report,
                SummaryTitle,
                ReportFileName);
        using StringWriter output = new()
        {
            NewLine = "\n"
        };

        await QualificationSummaryWriter.WriteAsync(
            report,
            SummaryTitle,
            ReportFileName,
            summaryPath,
            output,
            TestContext.Current.CancellationToken);
        await QualificationSummaryWriter.WriteAsync(
            report,
            SummaryTitle,
            ReportFileName,
            summaryPath,
            output,
            TestContext.Current.CancellationToken);

        File.ReadAllText(summaryPath).ShouldBe(
            "### Candidate\n"
            + "\n"
            + expected.Markdown
            + "\n"
            + expected.Markdown);
        string annotation = expected.Annotations.ShouldHaveSingleItem();
        output.ToString().ShouldBe($"{annotation}\n{annotation}\n");
    }

    [Fact]
    public async Task WriteAsync_NewSummary_CreatesFileWithoutByteOrderMark()
    {
        string summaryPath = Path.Combine(
            _tempRoot,
            "summaries",
            "step-summary.md");
        QualificationReport report = CreateReport(
            [],
            [CreateCase("01-immutable-expected-stream", [])]);
        using StringWriter output = new();

        await QualificationSummaryWriter.WriteAsync(
            report,
            SummaryTitle,
            ReportFileName,
            summaryPath,
            output,
            TestContext.Current.CancellationToken);

        byte[] bytes = File.ReadAllBytes(summaryPath);
        bytes[0].ShouldBe((byte)'#');
        Encoding.UTF8.GetString(bytes).ShouldBe(
            QualificationSummaryWriter.Render(
                    report,
                    SummaryTitle,
                    ReportFileName)
                .Markdown);
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Parse_WithoutSummaryOptions_WritesNoSummaryAndUsesDefaultTitle()
    {
        QualificationArguments arguments =
            QualificationArguments.Parse(CreateRequiredArguments());

        arguments.SummaryPath.ShouldBeNull();
        arguments.SummaryTitle.ShouldBe("qualification");
    }

    [Fact]
    public void Parse_SummaryOptions_AreExposed()
    {
        string summaryPath = Path.Combine(_tempRoot, "step-summary.md");

        QualificationArguments arguments = QualificationArguments.Parse(
        [
            .. CreateRequiredArguments(),
            "--summary",
            summaryPath,
            "--summary-title",
            "qualify-windows-latest-net8.0"
        ]);

        arguments.SummaryPath.ShouldBe(Path.GetFullPath(summaryPath));
        arguments.SummaryTitle.ShouldBe("qualify-windows-latest-net8.0");
    }

    [Fact]
    public void Parse_BlankSummaryTitle_UsesDefaultTitle()
    {
        QualificationArguments arguments = QualificationArguments.Parse(
        [
            .. CreateRequiredArguments(),
            "--summary-title",
            " "
        ]);

        arguments.SummaryTitle.ShouldBe("qualification");
    }

    [Fact]
    public void Parse_SummaryWithoutValue_IsRejected()
    {
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => QualificationArguments.Parse(
            [
                .. CreateRequiredArguments(),
                "--summary"
            ]));

        exception.Message.ShouldBe("Option '--summary' requires a value.");
    }

    [Fact]
    public async Task EntryPoint_BadBundleArguments_WritesBootstrapSummary()
    {
        const string title = "bootstrap-arguments";
        const string message =
            "Option '--bundle-sha256' is required with '--bundle'.";
        string corpusPath = CopyEntryPointCorpus();
        string summaryPath = Path.Combine(_tempRoot, "bootstrap-summary.md");

        int exitCode = await FhirPkg.Qualification.Program.Main(
            CreateEntryPointArguments(
                corpusPath,
                "bootstrap",
                summaryPath,
                validateOnly: false,
                summaryTitle: title));

        exitCode.ShouldBe(1);
        QualificationReport report = await ReadReportAsync(
            Path.Combine(_tempRoot, "bootstrap", ReportFileName));
        report.Success.ShouldBeFalse();
        report.ValidationOnly.ShouldBeFalse();
        report.Artifacts.ShouldBeEmpty();
        report.Summary.ShouldBe(new QualificationSummary
        {
            ArtifactCount = 0,
            ArtifactFailures = 0,
            CaseCount = 1,
            CaseFailures = 1
        });
        QualificationCaseResult bootstrap =
            report.Cases.ShouldHaveSingleItem();
        bootstrap.Id.ShouldBe("bootstrap");
        bootstrap.Success.ShouldBeFalse();
        bootstrap.Details.ShouldBeEmpty();
        QualificationFailure failure = bootstrap.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBeNull();
        failure.Stage.ShouldBeNull();
        failure.ExceptionType.ShouldBe(nameof(ArgumentException));
        failure.Message.ShouldBe(message);
        string summary = File.ReadAllText(summaryPath);
        summary.ShouldStartWith($"### {title} \u2014 FAILED\n");
        summary.ShouldContain(message);
        summary.ShouldContain("0 of 1 cases passed");
        summary.ShouldNotContain("Qualification inputs");
        summary.ShouldNotContain("live upstream");
        summary.ShouldBe(QualificationSummaryWriter.Render(
            report,
            title,
            ReportFileName).Markdown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntryPoint_SummaryWriteFailure_PreservesExitCode(
        bool validateOnly)
    {
        string corpusPath = CopyEntryPointCorpus();
        string baselineSummaryPath = Path.Combine(
            _tempRoot,
            "baseline-summary.md");
        string summaryDirectory = Path.Combine(_tempRoot, "summary-directory");
        Directory.CreateDirectory(summaryDirectory);

        int baselineExitCode = await FhirPkg.Qualification.Program.Main(
            CreateEntryPointArguments(
                corpusPath,
                "baseline",
                baselineSummaryPath,
                validateOnly));
        int exitCode = await FhirPkg.Qualification.Program.Main(
            CreateEntryPointArguments(
                corpusPath,
                "summary-failure",
                summaryDirectory,
                validateOnly));

        baselineExitCode.ShouldBe(validateOnly ? 0 : 1);
        exitCode.ShouldBe(baselineExitCode);
        QualificationReport baseline = await ReadReportAsync(
            Path.Combine(_tempRoot, "baseline", ReportFileName));
        QualificationReport report = await ReadReportAsync(
            Path.Combine(_tempRoot, "summary-failure", ReportFileName));
        baseline.Success.ShouldBe(validateOnly);
        report.Success.ShouldBe(baseline.Success);
        report.ValidationOnly.ShouldBe(validateOnly);
        report.ValidationOnly.ShouldBe(baseline.ValidationOnly);
        report.CorpusSha256.ShouldBe(baseline.CorpusSha256);
        report.Mode.ShouldBe(baseline.Mode);
        report.Summary.ShouldBe(baseline.Summary);
        report.Summary.ShouldBe(new QualificationSummary
        {
            ArtifactCount = validateOnly ? 1 : 0,
            ArtifactFailures = 0,
            CaseCount = 1,
            CaseFailures = validateOnly ? 0 : 1
        });
        report.Artifacts.ToArray().ShouldBe(baseline.Artifacts.ToArray());
        QualificationCaseResult result = report.Cases.ShouldHaveSingleItem();
        QualificationCaseResult baselineResult =
            baseline.Cases.ShouldHaveSingleItem();
        result.Id.ShouldBe(validateOnly ? "validate-only" : "bootstrap");
        result.Id.ShouldBe(baselineResult.Id);
        result.Success.ShouldBe(validateOnly);
        result.Success.ShouldBe(baselineResult.Success);
        result.Details.ToArray().ShouldBe(baselineResult.Details.ToArray());
        result.Failures.ToArray().ShouldBe(baselineResult.Failures.ToArray());
        if (validateOnly)
        {
            result.Failures.ShouldBeEmpty();
            QualificationCorpus corpus = await QualificationCorpus.LoadAsync(
                corpusPath,
                TestContext.Current.CancellationToken);
            byte[] localArchive = DeterministicPackageArchive.Create(
                corpus.LocalFixture.Name,
                corpus.LocalFixture.ManifestVersion,
                "local-dev");
            string localSha256 = Convert.ToHexString(
                    SHA256.HashData(localArchive))
                .ToLowerInvariant();
            localSha256.ShouldBe(corpus.LocalFixture.Sha256);
            QualificationArtifactResult local =
                report.Artifacts.ShouldHaveSingleItem();
            local.Kind.ShouldBe("local");
            local.Id.ShouldBe(corpus.LocalFixture.Id);
            local.Success.ShouldBeTrue();
            local.ExpectedSha256.ShouldBe(localSha256);
            local.ActualSha256.ShouldBe(localSha256);
            local.Failure.ShouldBeNull();
            report.CorpusSha256.ShouldBe(
                await QualificationCorpusHash.ComputeFileAsync(
                    corpusPath,
                    TestContext.Current.CancellationToken));
        }
        else
        {
            QualificationFailure failure =
                result.Failures.ShouldHaveSingleItem();
            failure.ExceptionType.ShouldBe(nameof(ArgumentException));
            failure.Message.ShouldBe(
                "Option '--bundle-sha256' is required with '--bundle'.");
        }

        File.ReadAllText(baselineSummaryPath).ShouldStartWith(
            $"### {SummaryTitle} \u2014 {(validateOnly ? "PASSED" : "FAILED")}\n");
        Directory.Exists(summaryDirectory).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(summaryDirectory).ShouldBeEmpty();
        File.Exists(summaryDirectory).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string[] CreateRequiredArguments() =>
    [
        "--cache",
        Path.Combine(_tempRoot, "cache"),
        "--output",
        Path.Combine(_tempRoot, ReportFileName)
    ];

    private string CopyEntryPointCorpus()
    {
        string path = Path.Combine(_tempRoot, "qualification-corpus.json");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "qualification-corpus.json"),
            path);
        return path;
    }

    private string[] CreateEntryPointArguments(
        string corpusPath,
        string invocation,
        string summaryPath,
        bool validateOnly,
        string summaryTitle = SummaryTitle)
    {
        string[] modeOptions = validateOnly
            ? ["--validate-only", "true"]
            : ["--bundle", Path.Combine(_tempRoot, invocation, "bundle")];
        return
        [
            "--cache",
            Path.Combine(_tempRoot, invocation, "cache"),
            "--output",
            Path.Combine(_tempRoot, invocation, ReportFileName),
            "--corpus",
            corpusPath,
            "--summary",
            summaryPath,
            "--summary-title",
            summaryTitle,
            .. modeOptions
        ];
    }

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

    private QualificationRunner CreateRunner()
    {
        QualificationCorpus corpus = new()
        {
            SchemaVersion = 1,
            ImmutableArtifacts = [],
            MutableArtifacts = [],
            LocalFixture = new LocalFixtureDefinition
            {
                Name = "local.qualification.dev",
                Selector = "dev",
                ManifestVersion = "0.0.1-dev",
                Sha256 = s_pinnedSha256
            },
            DownstreamClosure = new DownstreamClosureDefinition
            {
                Roots = [],
                ResolverMembers = [],
                RequiredMembers = []
            }
        };
        return new QualificationRunner(
            QualificationArguments.Parse(CreateRequiredArguments()),
            corpus,
            new QualificationBuildSnapshot(
                "source",
                RequestedPackageVersion: null,
                PackageVersion: null,
                FhirPkgAssemblyVersion: "1.0.0.0",
                FhirPkgInformationalVersion: null),
            new ReportSanitizer([]),
            DateTimeOffset.UnixEpoch,
            new string('0', 64));
    }

    private static QualificationReport CreateReport(
        List<QualificationArtifactResult> artifacts,
        List<QualificationCaseResult> cases,
        bool validationOnly = false)
    {
        int artifactFailures = artifacts.Count(result => !result.Success);
        int caseFailures = cases.Count(result => !result.Success);
        return new QualificationReport
        {
            Mode = "source",
            ValidationOnly = validationOnly,
            FhirPkgAssemblyVersion = "1.0.0.0",
            CorpusSha256 = s_corpusSha256,
            CorpusHashAlgorithm = "canonical-json-sha256-v1",
            Framework = ".NET",
            OperatingSystem = "test",
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            Success = artifactFailures == 0 && caseFailures == 0,
            Artifacts = artifacts,
            Cases = cases,
            Summary = new QualificationSummary
            {
                ArtifactCount = artifacts.Count,
                ArtifactFailures = artifactFailures,
                CaseCount = cases.Count,
                CaseFailures = caseFailures
            }
        };
    }

    private static QualificationArtifactResult CreateArtifact(
        string kind,
        string id,
        string? expectedSha256,
        string? actualSha256,
        bool success,
        QualificationFailure? failure = null) =>
        new()
        {
            Kind = kind,
            Id = id,
            SourceUri = $"https://packages.example.test/{id}",
            ExpectedSha256 = expectedSha256,
            ActualSha256 = actualSha256,
            Success = success,
            Failure = failure
        };

    private static QualificationCaseResult CreateCase(
        string id,
        List<QualificationFailure> failures,
        List<QualificationDetail>? details = null) =>
        new()
        {
            Id = id,
            Success = failures.Count == 0,
            DurationMilliseconds = 0,
            Details = details ?? [],
            Failures = failures
        };

    private static QualificationFailure CreateFailure(
        string? code,
        string message,
        string exceptionType = nameof(QualificationInvariantException)) =>
        new()
        {
            Code = code,
            ExceptionType = exceptionType,
            Message = message
        };

    private static QualificationFailure CreateHashMismatchFailure() =>
        CreateFailure(
            QualificationFailureCodes.ImmutableHashMismatch,
            $"Immutable artifact SHA-256 was {s_observedSha256}.");

    private static QualificationFailure CreateRequiredArtifactFailure() =>
        CreateFailure(
            QualificationFailureCodes.RequiredArtifactUnavailable,
            UsCoreUnavailable,
            nameof(RequiredArtifactUnavailableException));

    private static QualificationDetail CreateRequiredArtifactDetail(
        string id) =>
        new()
        {
            Name = "requiredArtifact",
            Value = id
        };

    private static QualificationDetail CreateDetail(
        string name,
        string value) =>
        new()
        {
            Name = name,
            Value = value
        };

    private sealed class StubHttpMessageHandler(
        HttpStatusCode statusCode,
        byte[] content,
        DateTimeOffset? lastModified) :
        HttpMessageHandler
    {
        internal List<Uri> RequestUris { get; } = [];

        internal bool WasDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            HttpResponseMessage response = new(statusCode)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.LastModified = lastModified;
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
