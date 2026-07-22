// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;

namespace FhirPkg.Cache;

/// <summary>
/// Validates raw tar headers before exposing them to <see cref="System.Formats.Tar.TarReader"/>.
/// This prevents hidden PAX and GNU metadata records from causing unbounded parser allocations.
/// </summary>
internal sealed class TarMetadataPreflightStream : Stream
{
    private const int TarBlockSize = 512;
    private const long PaxMetadataOverheadAllowanceBytes = 4L * 1024;

    private readonly Stream _inner;
    private readonly PackageInstallLimits _limits;
    private readonly string? _directive;
    private readonly byte[] _header = new byte[TarBlockSize];
    private int _headerOffset;
    private int _headerLength;
    private long _remainingDataBytes;
    private long _remainingPaddingBytes;
    private long _declaredRegularBytes;
    private long _metadataBytes;
    private int _metadataEntryCount;
    private PaxSizeOverrideParser? _paxParser;
    private bool _paxParserIsGlobal;
    private long? _pendingPaxSizeOverride;
    private long? _globalPaxSizeOverride;

    internal TarMetadataPreflightStream(
        Stream inner,
        PackageInstallLimits limits,
        string? directive)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(limits);

        _inner = inner;
        _limits = limits;
        _directive = directive;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal long MetadataBytes => _metadataBytes;

    internal int MetadataEntryCount => _metadataEntryCount;

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        if (_headerOffset < _headerLength)
            return CopyHeader(buffer);

        if (_remainingDataBytes > 0)
        {
            int bytesToRead = (int)Math.Min(
                buffer.Length,
                _remainingDataBytes);
            int bytesRead = _inner.Read(buffer[..bytesToRead]);
            if (bytesRead == 0)
                throw TruncatedArchive();

            _paxParser?.Process(buffer[..bytesRead]);
            _remainingDataBytes -= bytesRead;
            if (_remainingDataBytes == 0)
                CompletePaxMetadata();

            return bytesRead;
        }

        if (_remainingPaddingBytes > 0)
        {
            int bytesToRead = (int)Math.Min(
                buffer.Length,
                _remainingPaddingBytes);
            int bytesRead = _inner.Read(buffer[..bytesToRead]);
            if (bytesRead == 0)
                throw TruncatedArchive();

            _remainingPaddingBytes -= bytesRead;
            return bytesRead;
        }

        if (!FillHeader())
            return 0;

        return CopyHeader(buffer);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken)
            .AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;

        if (_headerOffset < _headerLength)
            return CopyHeader(buffer.Span);

        if (_remainingDataBytes > 0)
        {
            int bytesToRead = (int)Math.Min(
                buffer.Length,
                _remainingDataBytes);
            int bytesRead = await _inner.ReadAsync(
                    buffer[..bytesToRead],
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
                throw TruncatedArchive();

            _paxParser?.Process(buffer.Span[..bytesRead]);
            _remainingDataBytes -= bytesRead;
            if (_remainingDataBytes == 0)
                CompletePaxMetadata();

            return bytesRead;
        }

        if (_remainingPaddingBytes > 0)
        {
            int bytesToRead = (int)Math.Min(
                buffer.Length,
                _remainingPaddingBytes);
            int bytesRead = await _inner.ReadAsync(
                    buffer[..bytesToRead],
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
                throw TruncatedArchive();

            _remainingPaddingBytes -= bytesRead;
            return bytesRead;
        }

        if (!await FillHeaderAsync(cancellationToken).ConfigureAwait(false))
            return 0;

        return CopyHeader(buffer.Span);
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private bool FillHeader()
    {
        int totalBytes = 0;
        while (totalBytes < TarBlockSize)
        {
            int bytesRead = _inner.Read(
                _header,
                totalBytes,
                TarBlockSize - totalBytes);
            if (bytesRead == 0)
            {
                if (totalBytes == 0)
                    return false;

                throw TruncatedArchive();
            }

            totalBytes += bytesRead;
        }

        ValidateHeader();
        _headerOffset = 0;
        _headerLength = TarBlockSize;
        return true;
    }

    private async ValueTask<bool> FillHeaderAsync(
        CancellationToken cancellationToken)
    {
        int totalBytes = 0;
        while (totalBytes < TarBlockSize)
        {
            int bytesRead = await _inner.ReadAsync(
                    _header.AsMemory(
                        totalBytes,
                        TarBlockSize - totalBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                if (totalBytes == 0)
                    return false;

                throw TruncatedArchive();
            }

            totalBytes += bytesRead;
        }

        ValidateHeader();
        _headerOffset = 0;
        _headerLength = TarBlockSize;
        return true;
    }

    private int CopyHeader(Span<byte> destination)
    {
        int bytesToCopy = Math.Min(
            destination.Length,
            _headerLength - _headerOffset);
        _header.AsSpan(_headerOffset, bytesToCopy).CopyTo(destination);
        _headerOffset += bytesToCopy;
        return bytesToCopy;
    }

    private void ValidateHeader()
    {
        if (IsZeroBlock(_header))
        {
            _remainingDataBytes = 0;
            _remainingPaddingBytes = 0;
            return;
        }

        long rawSize = ParseTarNumber(_header.AsSpan(124, 12));
        byte entryType = _header[156];
        long framedSize;

        switch (entryType)
        {
            case 0:
            case (byte)' ':
            case (byte)'0':
            case (byte)'7':
                framedSize = GetEffectiveEntrySize(rawSize);
                ValidateRegularFileSize(framedSize);
                _pendingPaxSizeOverride = null;
                break;

            case (byte)'5':
                framedSize = GetEffectiveEntrySize(rawSize);
                if (framedSize != 0)
                    throw MalformedHeader("Directory entry has a non-zero data size.");

                _pendingPaxSizeOverride = null;
                break;

            case (byte)'x':
                ValidateMetadataSize(
                    rawSize,
                    GetPaxMetadataEntryLimit(),
                    pathMetadata: false);
                framedSize = rawSize;
                BeginPaxMetadata(rawSize, isGlobal: false);
                break;

            case (byte)'g':
                ValidateMetadataSize(
                    rawSize,
                    GetPaxMetadataEntryLimit(),
                    pathMetadata: false);
                framedSize = rawSize;
                BeginPaxMetadata(rawSize, isGlobal: true);
                break;

            case (byte)'L':
            case (byte)'K':
                ValidateMetadataSize(
                    rawSize,
                    GetEncodedPathLimit(),
                    pathMetadata: true);
                framedSize = rawSize;
                break;

            default:
                throw UnsupportedEntryType(entryType);
        }

        long paddingBytes = framedSize % TarBlockSize == 0
            ? 0
            : TarBlockSize - (framedSize % TarBlockSize);
        if (framedSize > long.MaxValue - paddingBytes)
            throw MalformedHeader("Archive entry size overflows tar block accounting.");

        _remainingDataBytes = framedSize;
        _remainingPaddingBytes = paddingBytes;
    }

    // PAX size overrides define both the logical entry length and where the
    // next physical tar header begins.
    private long GetEffectiveEntrySize(long rawSize) =>
        _pendingPaxSizeOverride
        ?? _globalPaxSizeOverride
        ?? rawSize;

    private void BeginPaxMetadata(long size, bool isGlobal)
    {
        _paxParser = new PaxSizeOverrideParser(size);
        _paxParserIsGlobal = isGlobal;
        if (size == 0)
            CompletePaxMetadata();
    }

    private void CompletePaxMetadata()
    {
        if (_paxParser is null)
            return;

        _paxParser.Complete();
        if (_paxParser.SizeOverride is long sizeOverride)
        {
            if (_paxParserIsGlobal)
                _globalPaxSizeOverride = sizeOverride;
            else
                _pendingPaxSizeOverride = sizeOverride;
        }

        _paxParser = null;
    }

    private void ValidateRegularFileSize(long size)
    {
        if (size > _limits.MaxEntryBytes)
            throw EntrySizeLimitExceeded(_limits.MaxEntryBytes);

        if (size > _limits.MaxExpandedBytes - _declaredRegularBytes)
            throw ExpandedSizeLimitExceeded(_limits.MaxExpandedBytes);

        _declaredRegularBytes += size;
    }

    private void ValidateMetadataSize(
        long size,
        long entryLimit,
        bool pathMetadata)
    {
        // Hidden metadata has its own finite budget so it cannot drive parser
        // allocations, while regular-file expanded-byte accounting stays exact.
        if (_metadataEntryCount >= _limits.MaxArchiveEntries)
            throw ArchiveEntryCountLimitExceeded(_limits.MaxArchiveEntries);

        _metadataEntryCount++;

        if (size > entryLimit)
        {
            if (pathMetadata)
                throw ArchivePathLengthLimitExceeded(_limits.MaxArchivePathLength);

            throw MetadataSizeLimitExceeded(entryLimit);
        }

        long metadataBudget = Math.Max(
            _limits.MaxExpandedBytes,
            GetPaxMetadataEntryLimit());
        if (size > metadataBudget - _metadataBytes)
            throw MetadataSizeLimitExceeded(metadataBudget);

        _metadataBytes += size;
    }

    private long GetEncodedPathLimit() =>
        checked((long)_limits.MaxArchivePathLength * 4 + 1);

    private long GetPaxMetadataEntryLimit() =>
        checked(
            (long)_limits.MaxArchivePathLength * 4
            + PaxMetadataOverheadAllowanceBytes);

    private PackageInstallException EntrySizeLimitExceeded(long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.EntrySizeLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive entry exceeds the size limit of {limit} bytes.",
            _directive);

    private PackageInstallException ExpandedSizeLimitExceeded(long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ExpandedSizeLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive exceeds the expanded size limit of {limit} bytes.",
            _directive);

    private PackageInstallException MetadataSizeLimitExceeded(long limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.EntrySizeLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive metadata exceeds the safety limit of {limit} bytes.",
            _directive);

    private PackageInstallException ArchiveEntryCountLimitExceeded(int limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive exceeds the hidden metadata count limit of {limit}.",
            _directive);

    private PackageInstallException ArchivePathLengthLimitExceeded(int limit) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Package archive path exceeds the normalized length limit of {limit}.",
            _directive);

    private PackageInstallException UnsupportedEntryType(byte entryType) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            $"Package archive contains unsupported raw entry type 0x{entryType:x2}.",
            _directive);

    private static bool IsZeroBlock(ReadOnlySpan<byte> block)
    {
        foreach (byte value in block)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static long ParseTarNumber(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0)
                throw MalformedHeader("Archive entry contains a negative size.");

            long binaryValue = field[0] & 0x3f;
            for (int index = 1; index < field.Length; index++)
            {
                if (binaryValue > (long.MaxValue - field[index]) / 256)
                    throw MalformedHeader("Archive entry size overflows supported accounting.");

                binaryValue = (binaryValue * 256) + field[index];
            }

            return binaryValue;
        }

        long octalValue = 0;
        bool foundDigit = false;
        bool terminated = false;
        foreach (byte value in field)
        {
            if (value is 0 or (byte)' ')
            {
                if (foundDigit)
                    terminated = true;

                continue;
            }

            if (terminated || value is < (byte)'0' or > (byte)'7')
                throw MalformedHeader("Archive entry contains an invalid size.");

            int digit = value - (byte)'0';
            if (octalValue > (long.MaxValue - digit) / 8)
                throw MalformedHeader("Archive entry size overflows supported accounting.");

            octalValue = (octalValue * 8) + digit;
            foundDigit = true;
        }

        return octalValue;
    }

    private static EndOfStreamException TruncatedArchive() =>
        new EndOfStreamException(
            "Package archive ended before the declared tar entry was complete.");

    private static InvalidDataException MalformedHeader(string message) =>
        new InvalidDataException(message);

    private sealed class PaxSizeOverrideParser
    {
        private static readonly byte[] s_sizeKeyword =
        [
            (byte)'s',
            (byte)'i',
            (byte)'z',
            (byte)'e',
            (byte)'='
        ];

        private readonly long _payloadLength;
        private long _processedBytes;
        private long _recordStartOffset;
        private long _recordLength;
        private long _recordBytesRead;
        private int _lengthDigitCount;
        private int _bodyBytesRead;
        private bool _readingBody;
        private bool _sizeKeywordCandidate;
        private bool _parsingSizeValue;
        private bool _sizeValueHasDigit;
        private long _sizeValue;

        internal PaxSizeOverrideParser(long payloadLength)
        {
            _payloadLength = payloadLength;
        }

        internal long? SizeOverride { get; private set; }

        internal void Process(ReadOnlySpan<byte> content)
        {
            foreach (byte value in content)
            {
                if (_processedBytes >= _payloadLength)
                    throw MalformedPax("PAX metadata exceeds its declared payload length.");

                if (!_readingBody
                    && _recordBytesRead == 0
                    && _lengthDigitCount == 0)
                {
                    _recordStartOffset = _processedBytes;
                }

                if (_readingBody)
                    ProcessBodyByte(value);
                else
                    ProcessLengthByte(value);

                _processedBytes++;
            }
        }

        internal void Complete()
        {
            if (_processedBytes != _payloadLength
                || _readingBody
                || _recordBytesRead != 0
                || _lengthDigitCount != 0)
            {
                throw MalformedPax(
                    "PAX metadata ended before its declared attribute record was complete.");
            }
        }

        private void ProcessLengthByte(byte value)
        {
            _recordBytesRead++;

            if (value == (byte)' ')
            {
                if (_lengthDigitCount == 0)
                    throw MalformedPax("PAX attribute length is missing.");

                long availableBytes = _payloadLength - _recordStartOffset;
                if (_recordLength <= _recordBytesRead
                    || _recordLength > availableBytes)
                {
                    throw MalformedPax(
                        "PAX attribute length exceeds the metadata payload.");
                }

                _readingBody = true;
                _bodyBytesRead = 0;
                _sizeKeywordCandidate = true;
                _parsingSizeValue = false;
                _sizeValueHasDigit = false;
                _sizeValue = 0;
                return;
            }

            if (value is < (byte)'0' or > (byte)'9')
                throw MalformedPax("PAX attribute length is not an invariant decimal integer.");

            int digit = value - (byte)'0';
            if (_recordLength > (long.MaxValue - digit) / 10)
                throw MalformedPax("PAX attribute length overflows supported accounting.");

            _recordLength = (_recordLength * 10) + digit;
            _lengthDigitCount++;
        }

        private void ProcessBodyByte(byte value)
        {
            _recordBytesRead++;
            if (_recordBytesRead > _recordLength)
                throw MalformedPax("PAX attribute exceeds its declared record length.");

            bool isFinalByte = _recordBytesRead == _recordLength;
            if (isFinalByte)
            {
                if (value != (byte)'\n')
                    throw MalformedPax("PAX attribute is not newline terminated.");

                if (_parsingSizeValue)
                {
                    if (!_sizeValueHasDigit)
                        throw MalformedPax("PAX size override is empty.");

                    SizeOverride = _sizeValue;
                }

                ResetRecord();
                return;
            }

            if (_bodyBytesRead < s_sizeKeyword.Length)
            {
                if (_sizeKeywordCandidate
                    && value != s_sizeKeyword[_bodyBytesRead])
                {
                    _sizeKeywordCandidate = false;
                }

                if (_bodyBytesRead == s_sizeKeyword.Length - 1
                    && _sizeKeywordCandidate)
                {
                    _parsingSizeValue = true;
                }
            }
            else if (_parsingSizeValue)
            {
                if (value is < (byte)'0' or > (byte)'9')
                {
                    throw MalformedPax(
                        "PAX size override is not an invariant non-negative decimal integer.");
                }

                int digit = value - (byte)'0';
                if (_sizeValue > (long.MaxValue - digit) / 10)
                    throw MalformedPax("PAX size override overflows supported accounting.");

                _sizeValue = (_sizeValue * 10) + digit;
                _sizeValueHasDigit = true;
            }

            _bodyBytesRead++;
        }

        private void ResetRecord()
        {
            _recordLength = 0;
            _recordBytesRead = 0;
            _lengthDigitCount = 0;
            _bodyBytesRead = 0;
            _readingBody = false;
            _sizeKeywordCandidate = false;
            _parsingSizeValue = false;
            _sizeValueHasDigit = false;
            _sizeValue = 0;
        }

        private static InvalidDataException MalformedPax(string message) =>
            new InvalidDataException(message);
    }
}
