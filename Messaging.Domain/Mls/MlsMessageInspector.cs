namespace Messaging.Domain.Mls;

/// <summary>RFC 9420 §6 wire formats, as they appear in the two-byte header of an
/// <c>MLSMessage</c>.</summary>
public enum MlsWireFormat : ushort
{
    Reserved = 0,
    PublicMessage = 1,
    PrivateMessage = 2,
    Welcome = 3,
    GroupInfo = 4,
    KeyPackage = 5,
}

/// <summary>RFC 9420 §6 <c>ContentType</c>.</summary>
public enum MlsContentType : byte
{
    Reserved = 0,
    Application = 1,
    Proposal = 2,
    Commit = 3,
}

/// <summary>What could be read out of an <c>MLSMessage</c> without any group secrets.</summary>
public readonly record struct MlsMessageHeader(
    ushort Version,
    MlsWireFormat WireFormat,
    byte[]? GroupId,
    ulong? Epoch,
    MlsContentType? ContentType);

/// <summary>Reads the parts of an MLS message that are in the clear.</summary>
public static class MlsMessageInspector
{
    /// <summary>The only protocol version RFC 9420 defines.</summary>
    public const ushort Mls10 = 1;

    /// <summary>Parses the readable header, or returns false with a reason.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out MlsMessageHeader header, out string? error)
    {
        header = default;
        error = null;

        var reader = new TlsReader(bytes);

        if (!reader.TryReadU16(out var version)) return Fail(out error, "truncated before the protocol version");
        if (!reader.TryReadU16(out var rawFormat)) return Fail(out error, "truncated before the wire format");

        var format = (MlsWireFormat)rawFormat;

        switch (format)
        {
            case MlsWireFormat.PublicMessage:
            {
                // FramedContent: group_id<V>, epoch(u64), Sender, authenticated_data<V>, content_type
                if (!reader.TryReadVector(out var groupId)) return Fail(out error, "truncated group_id");
                if (!reader.TryReadU64(out var epoch)) return Fail(out error, "truncated epoch");
                if (!TryReadSender(ref reader)) return Fail(out error, "truncated or unknown sender");
                if (!reader.TryReadVector(out _)) return Fail(out error, "truncated authenticated_data");
                if (!reader.TryReadU8(out var contentType)) return Fail(out error, "truncated content_type");

                header = new MlsMessageHeader(version, format, groupId.ToArray(), epoch,
                    (MlsContentType)contentType);
                return true;
            }

            case MlsWireFormat.PrivateMessage:
            {
                // PrivateMessage: group_id<V>, epoch(u64), content_type, authenticated_data<V>, ...
                if (!reader.TryReadVector(out var groupId)) return Fail(out error, "truncated group_id");
                if (!reader.TryReadU64(out var epoch)) return Fail(out error, "truncated epoch");
                if (!reader.TryReadU8(out var contentType)) return Fail(out error, "truncated content_type");

                header = new MlsMessageHeader(version, format, groupId.ToArray(), epoch,
                    (MlsContentType)contentType);
                return true;
            }

            case MlsWireFormat.Welcome:
            case MlsWireFormat.GroupInfo:
            case MlsWireFormat.KeyPackage:
                // Nothing usefully readable past the header for these, and nothing here needs it.
                header = new MlsMessageHeader(version, format, null, null, null);
                return true;

            default:
                return Fail(out error, $"unknown wire format {rawFormat}");
        }
    }

    private static bool Fail(out string? error, string reason)
    {
        error = $"not a well-formed MLSMessage: {reason}";
        return false;
    }

    /// <summary>Sender is a one-byte tag plus, for member/external, a u32 index.</summary>
    private static bool TryReadSender(ref TlsReader reader)
    {
        if (!reader.TryReadU8(out var senderType)) return false;

        return senderType switch
        {
            1 or 2 => reader.TrySkip(4),        // member(leaf_index) / external(sender_index)
            3 or 4 => true,                     // new_member_proposal / new_member_commit: empty
            _ => false,
        };
    }

    /// <summary>
    /// Minimal reader for the TLS-presentation-language subset RFC 9420 uses: fixed-width integers
    /// and QUIC-varint-prefixed opaque vectors (RFC 9000 §16, as adopted by RFC 8446bis).
    /// </summary>
    private ref struct TlsReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        private bool Has(int count) => _offset + count <= _data.Length;

        public bool TrySkip(int count)
        {
            if (!Has(count)) return false;
            _offset += count;
            return true;
        }

        public bool TryReadU8(out byte value)
        {
            value = 0;
            if (!Has(1)) return false;
            value = _data[_offset++];
            return true;
        }

        public bool TryReadU16(out ushort value)
        {
            value = 0;
            if (!Has(2)) return false;
            value = (ushort)((_data[_offset] << 8) | _data[_offset + 1]);
            _offset += 2;
            return true;
        }

        public bool TryReadU64(out ulong value)
        {
            value = 0;
            if (!Has(8)) return false;
            for (var i = 0; i < 8; i++) value = (value << 8) | _data[_offset + i];
            _offset += 8;
            return true;
        }

        /// <summary>Variable-length prefix: the top two bits of the first byte give the prefix
        /// width (1, 2, 4 or 8 bytes) and the remaining bits start the length.</summary>
        private bool TryReadVarint(out ulong value)
        {
            value = 0;
            if (!Has(1)) return false;

            var first = _data[_offset];
            var prefix = first >> 6;
            var length = 1 << prefix;

            if (!Has(length)) return false;

            value = (ulong)(first & 0x3F);
            for (var i = 1; i < length; i++) value = (value << 8) | _data[_offset + i];
            _offset += length;
            return true;
        }

        public bool TryReadVector(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!TryReadVarint(out var length)) return false;
            if (length > int.MaxValue || !Has((int)length)) return false;

            value = _data.Slice(_offset, (int)length);
            _offset += (int)length;
            return true;
        }
    }
}
