// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using FhirPkg.Utilities;
using FluentAssertions;
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

        hash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public void ComputeSha1_Stream_ProducesConsistentHash()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(data);

        var hashFromStream = CheckSum.ComputeSha1(stream);
        var hashFromBytes = CheckSum.ComputeSha1(data);

        hashFromStream.Should().Be(hashFromBytes);
    }

    [Fact]
    public void Verify_MatchingHash_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        var expectedHash = CheckSum.ComputeSha1(data);
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, expectedHash);

        result.Should().BeTrue();
    }

    [Fact]
    public void Verify_MismatchedHash_ReturnsFalse()
    {
        var data = Encoding.UTF8.GetBytes("test content");
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, "0000000000000000000000000000000000000000");

        result.Should().BeFalse();
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

        result.Should().BeTrue();
    }

    [Fact]
    public void Verify_CaseInsensitive_ReturnsTrue()
    {
        var data = Encoding.UTF8.GetBytes("case test");
        var expectedHash = CheckSum.ComputeSha1(data).ToUpperInvariant();
        using var stream = new MemoryStream(data);

        var result = CheckSum.Verify(stream, expectedHash);

        result.Should().BeTrue();
    }

    [Fact]
    public void ComputeSha1_NonEmptyInput_Returns40Chars()
    {
        var data = Encoding.UTF8.GetBytes("FHIR package");

        var hash = CheckSum.ComputeSha1(data);

        hash.Should().HaveLength(40);
        hash.Should().MatchRegex("^[0-9a-f]{40}$");
    }
}
