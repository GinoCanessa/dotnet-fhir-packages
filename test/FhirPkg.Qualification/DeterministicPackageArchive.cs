// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FhirPkg.Qualification;

internal static class DeterministicPackageArchive
{
    private const int TarBlockSize = 512;

    internal static byte[] Create(
        string name,
        string version,
        string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        string manifest =
            $"{{\"name\":{JsonSerializer.Serialize(name)}," +
            $"\"version\":{JsonSerializer.Serialize(version)}," +
            "\"description\":\"Deterministic local qualification fixture.\"}";
        using MemoryStream tar = new();
        WriteEntry(
            tar,
            "package/",
            content: [],
            typeFlag: (byte)'5',
            mode: 493);
        WriteEntry(
            tar,
            "package/package.json",
            Encoding.UTF8.GetBytes(manifest),
            typeFlag: (byte)'0',
            mode: 420);
        WriteEntry(
            tar,
            "package/qualification.txt",
            Encoding.UTF8.GetBytes($"{marker}\n"),
            typeFlag: (byte)'0',
            mode: 420);
        tar.Write(new byte[TarBlockSize * 2]);
        return WriteDeterministicGzip(tar.ToArray());
    }

    private static void WriteEntry(
        Stream output,
        string name,
        byte[] content,
        byte typeFlag,
        int mode)
    {
        byte[] header = new byte[TarBlockSize];
        WriteAscii(name, header.AsSpan(0, 100));
        WriteOctal(mode, header.AsSpan(100, 8));
        WriteOctal(0, header.AsSpan(108, 8));
        WriteOctal(0, header.AsSpan(116, 8));
        WriteOctal(content.LongLength, header.AsSpan(124, 12));
        WriteOctal(0, header.AsSpan(136, 12));
        header.AsSpan(148, 8).Fill((byte)' ');
        header[156] = typeFlag;
        WriteAscii("ustar\0", header.AsSpan(257, 6));
        WriteAscii("00", header.AsSpan(263, 2));

        int checksum = header.Sum(value => value);
        string checksumText = Convert.ToString(
                checksum,
                8)
            .PadLeft(6, '0');
        WriteAscii(checksumText, header.AsSpan(148, 6));
        header[154] = 0;
        header[155] = (byte)' ';

        output.Write(header);
        if (content.Length == 0)
            return;

        output.Write(content);
        int padding = TarBlockSize
            - (content.Length % TarBlockSize);
        if (padding != TarBlockSize)
            output.Write(new byte[padding]);
    }

    private static void WriteOctal(
        long value,
        Span<byte> destination)
    {
        destination.Clear();
        string text = Convert.ToString(value, 8);
        if (text.Length >= destination.Length)
        {
            throw new InvalidOperationException(
                "The deterministic tar field exceeded its fixed width.");
        }

        int offset = destination.Length - text.Length - 1;
        destination[..offset].Fill((byte)'0');
        Encoding.ASCII.GetBytes(text, destination[offset..]);
    }

    private static void WriteAscii(
        string value,
        Span<byte> destination)
    {
        int byteCount = Encoding.ASCII.GetByteCount(value);
        if (byteCount > destination.Length)
        {
            throw new InvalidOperationException(
                $"The deterministic tar value '{value}' is too long.");
        }

        Encoding.ASCII.GetBytes(value, destination);
    }

    private static byte[] WriteDeterministicGzip(byte[] content)
    {
        using MemoryStream gzip = new();
        gzip.Write(
        [
            0x1f,
            0x8b,
            0x08,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0xff
        ]);

        int offset = 0;
        Span<byte> blockHeader = stackalloc byte[4];
        while (offset < content.Length)
        {
            int blockLength = Math.Min(
                ushort.MaxValue,
                content.Length - offset);
            bool isFinal = offset + blockLength == content.Length;
            gzip.WriteByte(isFinal ? (byte)0x01 : (byte)0x00);
            BinaryPrimitives.WriteUInt16LittleEndian(
                blockHeader,
                (ushort)blockLength);
            BinaryPrimitives.WriteUInt16LittleEndian(
                blockHeader[2..],
                (ushort)~blockLength);
            gzip.Write(blockHeader);
            gzip.Write(content, offset, blockLength);
            offset += blockLength;
        }

        Span<byte> footer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer,
            ComputeCrc32(content));
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[4..],
            unchecked((uint)content.Length));
        gzip.Write(footer);
        return gzip.ToArray();
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> content)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in content)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xedb88320U
                    : crc >> 1;
            }
        }

        return ~crc;
    }
}
