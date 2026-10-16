// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;
using FhirPkg.Installation;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Installation;

public class PortableArchivePathTests
{
    [Fact]
    public void Create_NormalizesSeparatorsAndUnicodeToNfc()
    {
        PortableArchivePath path = PortableArchivePath.Create(
            "folder\\cafe\u0301.json",
            isDirectory: false);

        path.CanonicalPath.ShouldBe("folder/café.json");
        path.ExactSpelling.ShouldBe("folder/cafe\u0301.json");
        path.Depth.ShouldBe(2);
        path.Segments.ShouldBe(["folder", "café.json"]);
        path.ExactSegments.ShouldBe(["folder", "cafe\u0301.json"]);
    }

    [Fact]
    public void Create_DirectoryWithOneTrailingSeparator_NormalizesSuccessfully()
    {
        PortableArchivePath path = PortableArchivePath.Create(
            "package\\nested\\",
            isDirectory: true);

        path.CanonicalPath.ShouldBe("package/nested");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/rooted")]
    [InlineData("\\rooted")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\rooted")]
    [InlineData("C:relative")]
    [InlineData("./file")]
    [InlineData("folder/./file")]
    [InlineData("folder/../file")]
    [InlineData("folder//file")]
    [InlineData("folder/file.")]
    [InlineData("folder/file ")]
    [InlineData("folder/CON")]
    [InlineData("folder/con.txt")]
    [InlineData("folder/CON .txt")]
    [InlineData("folder/NUL .json")]
    [InlineData("folder/COM1 .log")]
    [InlineData("folder/COM¹ .log")]
    [InlineData("folder/LPT9 .txt")]
    [InlineData("folder/LPT9.log")]
    [InlineData("folder/COM¹.txt")]
    [InlineData("folder/a<b")]
    [InlineData("folder/a>b")]
    [InlineData("folder/a:b")]
    [InlineData("folder/a\"b")]
    [InlineData("folder/a|b")]
    [InlineData("folder/a?b")]
    [InlineData("folder/a*b")]
    public void Create_InvalidPortablePath_ThrowsTypedArchiveFailure(
        string entryName)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                entryName,
                isDirectory: false,
                directive: "example.package#1.0.0"));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public void Create_ControlCharacter_ThrowsTypedArchiveFailure()
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                "folder/line\nbreak",
                isDirectory: false));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
    }

    [Fact]
    public void Create_UnpairedSurrogate_ThrowsTypedArchiveFailure()
    {
        string malformed = string.Concat("folder/", '\ud800');

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                malformed,
                isDirectory: false));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
    }

    [Fact]
    public void Create_RegularFileWithTrailingSeparator_ThrowsTypedArchiveFailure()
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                "folder/file/",
                isDirectory: false));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
    }

    [Fact]
    public void Create_AsciiComponentAtPortableLimit_IsAccepted()
    {
        string component = new string('a', PortableArchivePath.MaximumComponentLength);

        PortableArchivePath path = PortableArchivePath.Create(
            component,
            isDirectory: false);

        path.CanonicalPath.ShouldBe(component);
    }

    [Fact]
    public void Create_AsciiComponentOneOverPortableLimit_IsRejected()
    {
        string component = new string(
            'a',
            PortableArchivePath.MaximumComponentLength + 1);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                component,
                isDirectory: false));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public void Create_MultibyteComponentAtUtf8PortableLimit_IsAccepted()
    {
        string component = string.Concat(
            new string('é', 127),
            "a");

        PortableArchivePath path = PortableArchivePath.Create(
            component,
            isDirectory: false);

        path.CanonicalPath.ShouldBe(component);
    }

    [Fact]
    public void Create_MultibyteComponentOneByteOverUtf8PortableLimit_IsRejected()
    {
        string component = new string('é', 128);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                component,
                isDirectory: false));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public void Create_DecomposedComponentNormalizingWithinUtf8Limit_IsAccepted()
    {
        string decomposed = string.Concat(
            Enumerable.Repeat("e\u0301", 127));
        string canonical = new string('é', 127);

        PortableArchivePath path = PortableArchivePath.Create(
            decomposed,
            isDirectory: false);

        path.ExactSpelling.ShouldBe(decomposed);
        path.CanonicalPath.ShouldBe(canonical);
    }

    [Fact]
    public void Create_DecomposedComponentNormalizingOneByteOverUtf8Limit_IsRejected()
    {
        string decomposed = string.Concat(
            Enumerable.Repeat("e\u0301", 128));

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PortableArchivePath.Create(
                decomposed,
                isDirectory: false));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Theory]
    [InlineData("a", 255, "", 255)]
    [InlineData("é", 127, "a", 128)]
    [InlineData("e\u0301", 85, "", 170)]
    public void ValidateExtractionSpelling_ComponentAtPortableLimit_IsAccepted(
        string repeatedText,
        int repeatCount,
        string suffix,
        int expectedUnits)
    {
        string component = string.Concat(Enumerable.Repeat(repeatedText, repeatCount)) + suffix;
        component.Length.ShouldBe(expectedUnits);
        Encoding.UTF8.GetByteCount(component).ShouldBe(255);
        PortableArchivePath path = PortableArchivePath.Create(
            $"folder/{component}",
            isDirectory: false);

        path.ValidateExtractionSpelling();

        path.ExactSegments.ShouldBe(["folder", component]);
        path.CanonicalPath.ShouldBe($"folder/{component.Normalize(NormalizationForm.FormC)}");
    }

    [Theory]
    [InlineData("e\u0301", 85, "a", 171, 256)]
    [InlineData("e\u0301", 127, "", 254, 381)]
    [InlineData("a", 253, "\u03b9\u0308\u0301", 256, 259)]
    public void ValidateExtractionSpelling_ExactComponentOverLimitAcceptedByCreate_IsRejected(
        string repeatedText,
        int repeatCount,
        string suffix,
        int expectedUnits,
        int expectedBytes)
    {
        string component = string.Concat(Enumerable.Repeat(repeatedText, repeatCount)) + suffix;
        component.Length.ShouldBe(expectedUnits);
        Encoding.UTF8.GetByteCount(component).ShouldBe(expectedBytes);
        PortableArchivePath path = PortableArchivePath.Create(
            $"folder/{component}/file.txt",
            isDirectory: false);
        path.Segments[1].Length.ShouldBeLessThanOrEqualTo(255);
        Encoding.UTF8.GetByteCount(path.Segments[1]).ShouldBeLessThanOrEqualTo(255);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => path.ValidateExtractionSpelling("example.package#1.0.0"));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.Directive.ShouldBe("example.package#1.0.0");
    }
}
