// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using FhirPkg.IO;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

[Collection("EnvironmentVariable")]
public class CheckSumTests
{
    [Fact]
    public void ComputeSha1_KnownInput_MatchesExpected()
    {
        // SHA-1 of empty string is da39a3ee5e6b4b0d3255bfef95601890afd80709
        byte[] data = Array.Empty<byte>();

        string hash = CheckSum.ComputeSha1(data);

        hash.ShouldBe("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public void ComputeSha1_Stream_ProducesConsistentHash()
    {
        byte[] data = Encoding.UTF8.GetBytes("hello world");
        using MemoryStream stream = new MemoryStream(data);

        string hashFromStream = CheckSum.ComputeSha1(stream);
        string hashFromBytes = CheckSum.ComputeSha1(data);

        hashFromStream.ShouldBe(hashFromBytes);
    }

    [Fact]
    public void Verify_MatchingHash_ReturnsTrue()
    {
        byte[] data = Encoding.UTF8.GetBytes("test content");
        string expectedHash = CheckSum.ComputeSha1(data);
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Verify_MismatchedHash_ReturnsFalse()
    {
        byte[] data = Encoding.UTF8.GetBytes("test content");
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.Verify(stream, "0000000000000000000000000000000000000000");

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_NullOrEmptyExpected_ReturnsTrue(string? expectedHash)
    {
        byte[] data = Encoding.UTF8.GetBytes("any content");
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Verify_CaseInsensitive_ReturnsTrue()
    {
        byte[] data = Encoding.UTF8.GetBytes("case test");
        string expectedHash = CheckSum.ComputeSha1(data).ToUpperInvariant();
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.Verify(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void ComputeSha1_NonEmptyInput_Returns40Chars()
    {
        byte[] data = Encoding.UTF8.GetBytes("FHIR package");

        string hash = CheckSum.ComputeSha1(data);

        hash.Length.ShouldBe(40);
        hash.ShouldMatch("^[0-9a-f]{40}$");
    }

    // ── SHA-256 tests ───────────────────────────────────────────────────

    [Fact]
    public void ComputeSha256_KnownInput_MatchesExpected()
    {
        // SHA-256 of empty byte array
        byte[] data = Array.Empty<byte>();

        string hash = CheckSum.ComputeSha256(data);

        hash.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void ComputeSha256_Stream_ProducesConsistentHash()
    {
        byte[] data = Encoding.UTF8.GetBytes("hello world");
        using MemoryStream stream = new MemoryStream(data);

        string hashFromStream = CheckSum.ComputeSha256(stream);
        string hashFromBytes = CheckSum.ComputeSha256(data);

        hashFromStream.ShouldBe(hashFromBytes);
    }

    [Fact]
    public void ComputeSha256_NonEmptyInput_Returns64Chars()
    {
        byte[] data = Encoding.UTF8.GetBytes("FHIR package");

        string hash = CheckSum.ComputeSha256(data);

        hash.Length.ShouldBe(64);
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void VerifySha256_MatchingHash_ReturnsTrue()
    {
        byte[] data = Encoding.UTF8.GetBytes("test content");
        string expectedHash = CheckSum.ComputeSha256(data);
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.VerifySha256(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Fact]
    public void VerifySha256_MismatchedHash_ReturnsFalse()
    {
        byte[] data = Encoding.UTF8.GetBytes("test content");
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.VerifySha256(stream, "0000000000000000000000000000000000000000000000000000000000000000");

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifySha256_NullOrEmptyExpected_ReturnsTrue(string? expectedHash)
    {
        byte[] data = Encoding.UTF8.GetBytes("any content");
        using MemoryStream stream = new MemoryStream(data);

        bool result = CheckSum.VerifySha256(stream, expectedHash);

        result.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Verify_FileStream_RetainsResetPositionContract(bool sha256, bool resetPosition)
    {
        string path = Path.Combine(Path.GetTempPath(), $"checksum-reset-{Guid.NewGuid():N}");
        try
        {
            byte[] bytes = "skipverified payload"u8.ToArray();
            File.WriteAllBytes(path, bytes);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;
            ScriptedFileSystemPrimitives primitives = SystemFiles();
            primitives.Enqueue<int>("ReadBorrowed", call =>
            {
                call.Stream!.Read(call.Buffer.Span[..3]).ShouldBe(3);
                call.Buffer.Span[..3].Fill(0xFF);
                return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "ReadBorrowed", path);
            });
            int seeks = 0;
            int readsBeforeReset = 0;
            primitives.Set<long>("SetPosition", call =>
            {
                seeks++;
                if (resetPosition && seeks == 2)
                {
                    readsBeforeReset = primitives.Count("ReadBorrowed");
                    return ScriptedFileSystemPrimitives.NativeFailure<long>(5, "reset", path);
                }
                return SystemFileSystemPrimitives.Instance.SetPosition(call.Stream!, call.Offset!.Value);
            });
            FileSystemContext context = primitives.CreateContext(retries: 1);
            string expected = sha256 ? CheckSum.ComputeSha256(bytes[4..]) : CheckSum.ComputeSha1(bytes[4..]);

            bool valid = sha256
                ? CheckSum.VerifySha256(caller, expected.ToUpperInvariant(), resetPosition, context)
                : CheckSum.Verify(caller, expected.ToUpperInvariant(), resetPosition, context);

            valid.ShouldBeTrue();
            caller.Position.ShouldBe(resetPosition ? 4 : bytes.Length);
            caller.CanRead.ShouldBeTrue();
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("SetPosition").ShouldBe(resetPosition ? 3 : 1);
            primitives.Waits.ShouldBe(resetPosition ? [10, 10] : [10]);
            if (resetPosition)
                primitives.Count("ReadBorrowed").ShouldBe(readsBeforeReset);
            primitives.Count("CloseFile").ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compute_FileStream_RetriesOnlyFailedBlockWithoutRehashingEarlierBytes(bool sha256)
    {
        string path = Path.Combine(Path.GetTempPath(), $"checksum-block-{Guid.NewGuid():N}");
        try
        {
            byte[] bytes = Enumerable.Range(0, 25_000).Select(index => (byte)(index % 251)).ToArray();
            File.WriteAllBytes(path, bytes);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 13;
            ScriptedFileSystemPrimitives primitives = SystemFiles();
            List<long> offsets = [];
            primitives.Set<int>("ReadBorrowed", call =>
            {
                offsets.Add(call.Stream!.Position);
                if (offsets.Count == 2)
                {
                    call.Stream.Read(call.Buffer.Span[..3]).ShouldBe(3);
                    call.Buffer.Span[..3].Fill(0xEE);
                    return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "read", path);
                }
                return SystemFileSystemPrimitives.Instance.ReadBorrowed(call.Stream, call.Buffer);
            });
            FileSystemContext context = primitives.CreateContext();

            string actual = sha256 ? CheckSum.ComputeSha256(caller, context) : CheckSum.ComputeSha1(caller, context);

            actual.ShouldBe(sha256 ? CheckSum.ComputeSha256(bytes[13..]) : CheckSum.ComputeSha1(bytes[13..]));
            offsets.Count.ShouldBeGreaterThan(3);
            offsets[0].ShouldBe(13);
            offsets.Count(offset => offset == 13).ShouldBe(1);
            offsets[1].ShouldBe(offsets[2]);
            caller.Position.ShouldBe(bytes.Length);
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("SetPosition").ShouldBe(1);
            primitives.Waits.ShouldBe([10]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_PublicFileStreamRetainsDefaultReset(bool sha256)
    {
        string path = Path.Combine(Path.GetTempPath(), $"checksum-public-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "skipcontent");
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;

            bool valid = sha256
                ? CheckSum.VerifySha256(caller, CheckSum.ComputeSha256("content"u8.ToArray()))
                : CheckSum.Verify(caller, CheckSum.ComputeSha1("content"u8.ToArray()));

            valid.ShouldBeTrue();
            caller.Position.ShouldBe(4);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Verify_ExhaustedResetDoesNotRereadOrRehash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"checksum-reset-failure-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "content");
            using FileStream caller = File.OpenRead(path);
            ScriptedFileSystemPrimitives primitives = SystemFiles();
            IOException failure = new IOException("reset failed");
            int readsBeforeReset = 0;
            primitives.Set<long>("SetPosition", call =>
            {
                if (readsBeforeReset == 0)
                    readsBeforeReset = primitives.Count("ReadBorrowed");
                return ScriptedFileSystemPrimitives.Fail<long>(failure, "SetPosition", call.Path);
            });

            Should.Throw<IOException>(() => CheckSum.Verify(
                caller, CheckSum.ComputeSha1("content"u8.ToArray()), true, primitives.CreateContext(retries: 2)))
                .ShouldBeSameAs(failure);

            readsBeforeReset.ShouldBeGreaterThan(0);
            primitives.Count("ReadBorrowed").ShouldBe(readsBeforeReset);
            primitives.Count("SetPosition").ShouldBe(3);
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Waits.ShouldBe([10, 10]);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PureAndSkippedHashes_DoNotResolveEnvironmentOrPreparePendingWrites()
    {
        string path = Path.Combine(Path.GetTempPath(), $"checksum-pure-{Guid.NewGuid():N}");
        string? original = Environment.GetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable);
        try
        {
            using FileStream caller = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096);
            caller.Write("pending"u8);
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, "invalid");

            CheckSum.Verify(caller, " ").ShouldBeTrue();
            CheckSum.VerifySha256(caller, null).ShouldBeTrue();
            new FileInfo(path).Length.ShouldBe(0);
            caller.Position.ShouldBe(7);
            byte[] data = "pure"u8.ToArray();
            using MemoryStream sha1Stream = new MemoryStream(data);
            using MemoryStream sha256Stream = new MemoryStream(data);
            CheckSum.ComputeSha1(sha1Stream).ShouldBe(CheckSum.ComputeSha1(data));
            CheckSum.VerifySha256(sha256Stream, CheckSum.ComputeSha256(data)).ShouldBeTrue();
            sha256Stream.Position.ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, original);
            File.Delete(path);
        }
    }

    private static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        return primitives;
    }
}
