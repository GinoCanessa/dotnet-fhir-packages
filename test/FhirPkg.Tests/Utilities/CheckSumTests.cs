// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class CheckSumTests
{
    [Fact]
    public void ComputeSha1_KnownInput_MatchesExpected()
    {
        // SHA-1 of empty string is da39a3ee5e6b4b0d3255bfef95601890afd80709
        var data = Array.Empty<byte>();

        var hash = CheckSum.ComputeSha1(data);

        hash.ShouldBe("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public void ComputeSha1_Stream_ProducesConsistentHash()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(data);

        var hashFromStream = CheckSum.ComputeSha1(stream);
        var hashFromBytes = CheckSum.ComputeSha1(data);

        hashFromStream.ShouldBe(hashFromBytes);
    }

    [Fact]
    public void Verify_MatchingHash_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        var expectedHash = CheckSum.ComputeSha1(data);
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Verify_MismatchedHash_ReturnsFalse()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, "0000000000000000000000000000000000000000");

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_NullOrEmptyExpected_ReturnsTrue(string? expectedHash)
    {
        var data = Encoding.UTF8.GetBytes("any content");
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Verify_CaseInsensitive_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("case test");
        var expectedHash = CheckSum.ComputeSha1(data).ToUpperInvariant();
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void ComputeSha1_NonEmptyInput_Returns40Chars()
    {
        var data = Encoding.UTF8.GetBytes("FHIR package");

        var hash = CheckSum.ComputeSha1(data);

        hash.Length.ShouldBe(40);
        hash.ShouldMatch("^[0-9a-f]{40}$");
    }

    // ── SHA-256 tests ───────────────────────────────────────────────────

    [Fact]
    public void ComputeSha256_KnownInput_MatchesExpected()
    {
        // SHA-256 of empty byte array
        var data = Array.Empty<byte>();

        var hash = CheckSum.ComputeSha256(data);

        hash.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void ComputeSha256_Stream_ProducesConsistentHash()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(data);

        var hashFromStream = CheckSum.ComputeSha256(stream);
        var hashFromBytes = CheckSum.ComputeSha256(data);

        hashFromStream.ShouldBe(hashFromBytes);
    }

    [Fact]
    public void ComputeSha256_NonEmptyInput_Returns64Chars()
    {
        var data = Encoding.UTF8.GetBytes("FHIR package");

        var hash = CheckSum.ComputeSha256(data);

        hash.Length.ShouldBe(64);
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void VerifySha256_MatchingHash_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        var expectedHash = CheckSum.ComputeSha256(data);
        using var stream = new MemoryStream(data);

        var result = CheckSum.VerifySha256(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void VerifySha256_MismatchedHash_ReturnsFalse()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        using var stream = new MemoryStream(data);

        var result = CheckSum.VerifySha256(stream, "0000000000000000000000000000000000000000000000000000000000000000");

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifySha256_NullOrEmptyExpected_ReturnsTrue(string? expectedHash)
    {
        var data = Encoding.UTF8.GetBytes("any content");
        using var stream = new MemoryStream(data);

        var result = CheckSum.VerifySha256(stream, expectedHash);

        result.ShouldBeTrue();
    }
}
