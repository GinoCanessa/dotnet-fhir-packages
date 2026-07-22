// Copyright (c) Gino Canessa. Licensed under the MIT License.

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
}
