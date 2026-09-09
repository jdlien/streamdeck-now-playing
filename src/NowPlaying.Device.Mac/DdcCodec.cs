namespace NowPlaying.Device;

/// <summary>One monitor's answer to a get-VCP request.</summary>
/// <param name="Current">The feature's present value, in the monitor's own units.</param>
/// <param name="Maximum">The largest value the monitor accepts. Arbitrary per monitor.</param>
public readonly record struct VcpReading(int Current, int Maximum);

/// <summary>
/// DDC/CI framing and, more importantly, reply validation.
///
/// Windows never needs this: dxva2 speaks DDC for us and hands back a number.
/// On macOS we drive the I2C bus ourselves, and a *successful* transaction can
/// still return rubbish. Measured on the target machine, a Studio Display's
/// I2C service accepted a write, reported success on the read, and returned
/// <c>ed c4 88 9e 7e 19 7f 22 71 e6 c2 15</c> -- which, parsed naively, is
/// "brightness 158". Every field is therefore checked before the numbers are
/// believed (macos-port-plan D6).
///
/// Pure, so the awkward replies can be unit tested without a monitor.
/// </summary>
public static class DdcCodec
{
    /// <summary>VCP feature code for luminance.</summary>
    public const byte Luminance = 0x10;

    /// <summary>I2C address of a DDC/CI display.</summary>
    public const uint ChipAddress = 0x37;

    /// <summary>Offset writes and reads are addressed to.</summary>
    public const uint DataAddress = 0x51;

    private const byte HostAddress = 0x6E;      // the source byte a display puts in its reply
    private const byte ReadChecksumSeed = 0x50; // the virtual host address replies are summed against
    private const byte GetVcpOpcode = 0x01;
    private const byte SetVcpOpcode = 0x03;
    private const byte FeatureReplyOpcode = 0x02;

    /// <summary>The request that asks a monitor for one feature's value.</summary>
    public static byte[] GetRequest(byte vcp)
    {
        var frame = new byte[] { 0x82, GetVcpOpcode, vcp, 0x00 };
        frame[3] = Checksum(HostAddress ^ (byte)DataAddress, frame.AsSpan(0, 3));
        return frame;
    }

    /// <summary>The request that sets one feature's value, in the monitor's own units.</summary>
    public static byte[] SetRequest(byte vcp, int value)
    {
        var clamped = Math.Clamp(value, 0, 0xFFFF);
        var frame = new byte[] { 0x84, SetVcpOpcode, vcp, (byte)(clamped >> 8), (byte)(clamped & 0xFF), 0x00 };
        frame[5] = Checksum(HostAddress ^ (byte)DataAddress, frame.AsSpan(0, 5));
        return frame;
    }

    /// <summary>
    /// The reading in a reply, or null when the reply is not a trustworthy
    /// answer to <paramref name="expectedVcp"/>. Null covers a monitor that is
    /// not there, is asleep, does not implement the feature, or is a display
    /// whose I2C bus answers with noise.
    /// </summary>
    public static VcpReading? ParseGetReply(ReadOnlySpan<byte> reply, byte expectedVcp)
    {
        // A feature reply is 11 bytes; callers usually read a little more.
        if (reply.Length < 11)
        {
            return null;
        }

        if (reply[0] != HostAddress)
        {
            return null; // not from a display
        }

        if (reply[1] != 0x88)
        {
            return null; // length byte of a feature reply, with the high bit set
        }

        if (reply[2] != FeatureReplyOpcode)
        {
            return null; // answering some other question
        }

        if (reply[3] != 0x00)
        {
            return null; // the monitor's own result code: non-zero means unsupported feature
        }

        if (reply[4] != expectedVcp)
        {
            return null; // answering about a different feature
        }

        var maximum = (reply[6] << 8) | reply[7];
        var current = (reply[8] << 8) | reply[9];

        if (maximum <= 0 || current < 0 || current > maximum)
        {
            return null; // a range that cannot be turned into a percentage
        }

        if (Checksum(ReadChecksumSeed, reply[..10]) != reply[10])
        {
            return null;
        }

        return new VcpReading(current, maximum);
    }

    private static byte Checksum(int seed, ReadOnlySpan<byte> bytes)
    {
        var checksum = (byte)seed;
        foreach (var b in bytes)
        {
            checksum ^= b;
        }

        return checksum;
    }
}
