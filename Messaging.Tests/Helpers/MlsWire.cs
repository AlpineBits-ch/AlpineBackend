using Messaging.Domain.Mls;

namespace Messaging.Tests.Helpers;

/// <summary>
/// Builds RFC 9420 <c>MLSMessage</c> byte strings, so tests exercise the same header the server
/// parses rather than a placeholder that happens to be non-empty.
///
/// <para>Only the framing is real - the bodies are filler. That is exactly the extent of what the
/// server reads: version, wire format, group id, epoch and content type, all of which sit outside
/// the encrypted portion. See <see cref="MlsMessageInspector"/>.</para>
/// </summary>
internal static class MlsWire
{
    public static readonly byte[] DefaultGroupId = [1, 2, 3];

    /// <summary>A commit, framed as openmls actually sends one: a PrivateMessage, since the default
    /// wire-format policy is pure ciphertext.</summary>
    public static byte[] Commit(byte[]? groupId = null, ulong epoch = 0) =>
        PrivateMessage(groupId ?? DefaultGroupId, epoch, MlsContentType.Commit);

    public static byte[] Proposal(byte[]? groupId = null, ulong epoch = 0) =>
        PrivateMessage(groupId ?? DefaultGroupId, epoch, MlsContentType.Proposal);

    /// <summary>An application message, which must never be accepted on the commit route.</summary>
    public static byte[] ApplicationMessage(byte[]? groupId = null, ulong epoch = 0) =>
        PrivateMessage(groupId ?? DefaultGroupId, epoch, MlsContentType.Application);

    /// <summary>An external commit, which openmls forces to PublicMessage.</summary>
    public static byte[] ExternalCommit(byte[]? groupId = null, ulong epoch = 0)
    {
        var w = new Writer();
        w.U16(MlsMessageInspector.Mls10);
        w.U16((ushort)MlsWireFormat.PublicMessage);
        w.Vector(groupId ?? DefaultGroupId);
        w.U64(epoch);
        w.U8(4);                       // Sender: new_member_commit, no index
        w.Vector([]);                  // authenticated_data
        w.U8((byte)MlsContentType.Commit);
        w.Raw([0xAA, 0xBB]);           // Commit body + auth: never read
        return w.ToArray();
    }

    public static byte[] GroupInfo()
    {
        var w = new Writer();
        w.U16(MlsMessageInspector.Mls10);
        w.U16((ushort)MlsWireFormat.GroupInfo);
        w.Raw([0xCC, 0xDD]);
        return w.ToArray();
    }

    public static byte[] Welcome()
    {
        var w = new Writer();
        w.U16(MlsMessageInspector.Mls10);
        w.U16((ushort)MlsWireFormat.Welcome);
        w.Raw([0xEE, 0xFF]);
        return w.ToArray();
    }

    private static byte[] PrivateMessage(byte[] groupId, ulong epoch, MlsContentType contentType)
    {
        var w = new Writer();
        w.U16(MlsMessageInspector.Mls10);
        w.U16((ushort)MlsWireFormat.PrivateMessage);
        w.Vector(groupId);
        w.U64(epoch);
        w.U8((byte)contentType);
        w.Vector([]);                  // authenticated_data
        w.Vector([0x01, 0x02]);        // encrypted_sender_data
        w.Vector([0x03, 0x04]);        // ciphertext
        return w.ToArray();
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void U8(byte value) => _bytes.Add(value);

        public void U16(ushort value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void U64(ulong value)
        {
            for (var shift = 56; shift >= 0; shift -= 8) _bytes.Add((byte)(value >> shift));
        }

        public void Raw(byte[] value) => _bytes.AddRange(value);

        /// <summary>QUIC varint length prefix. Tests never need more than the 1- and 2-byte
        /// forms.</summary>
        public void Vector(byte[] value)
        {
            if (value.Length < 64)
            {
                _bytes.Add((byte)value.Length);
            }
            else if (value.Length < 16384)
            {
                _bytes.Add((byte)(0x40 | (value.Length >> 8)));
                _bytes.Add((byte)value.Length);
            }
            else
            {
                throw new ArgumentException("Test vectors stay under 16 KiB.", nameof(value));
            }

            _bytes.AddRange(value);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
