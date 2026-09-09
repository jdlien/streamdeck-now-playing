using NowPlaying.Device;

namespace NowPlaying.Media.Tests;

/// <summary>
/// The DDC/CI reply validation from macos-port-plan D6. The frames here are
/// real: both were captured from the development machine on 2026-09-09, one
/// from a BenQ MA270S that answers properly and one from a Studio Display whose
/// I2C bus reports success and returns noise.
/// </summary>
public class DdcCodecTests
{
    /// <summary>The BenQ MA270S answering "brightness 63 of 100".</summary>
    private static readonly byte[] GoodReply =
        [0x6e, 0x88, 0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x3f, 0xff, 0x00];

    /// <summary>A Studio Display's bus after a "successful" read. Parsed naively this is "brightness 158".</summary>
    private static readonly byte[] GarbageReply =
        [0xed, 0xc4, 0x88, 0x9e, 0x7e, 0x19, 0x7f, 0x22, 0x71, 0xe6, 0xc2, 0x15];

    [Fact]
    public void ARealReplyParses()
    {
        var reading = DdcCodec.ParseGetReply(GoodReply, DdcCodec.Luminance);
        Assert.Equal(new VcpReading(63, 100), reading);
    }

    [Fact]
    public void TheRecordedGarbageIsRejected()
    {
        // The whole point of D6: this frame came back from a successful read.
        Assert.Null(DdcCodec.ParseGetReply(GarbageReply, DdcCodec.Luminance));
    }

    [Fact]
    public void AReplyAboutADifferentFeatureIsRejected()
    {
        var reply = (byte[])GoodReply.Clone();
        reply[4] = 0x12; // contrast, not luminance
        reply[10] = Recompute(reply);
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void AChecksumValidUnsupportedFeatureReplyIsStillAFailure()
    {
        // Result code 1 means "unsupported VCP". A valid checksum does not make it an answer.
        var reply = (byte[])GoodReply.Clone();
        reply[3] = 0x01;
        reply[10] = Recompute(reply);
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void ABadChecksumIsRejectedEvenWhenEveryFieldLooksRight()
    {
        var reply = (byte[])GoodReply.Clone();
        reply[10] ^= 0xFF;
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void AZeroMaximumIsRejectedRatherThanDividedBy()
    {
        var reply = (byte[])GoodReply.Clone();
        reply[6] = 0; reply[7] = 0;
        reply[10] = Recompute(reply);
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void ACurrentAboveTheMaximumIsRejectedRatherThanClamped()
    {
        // Clamping here would hide a parser fault behind a plausible 100%.
        var reply = (byte[])GoodReply.Clone();
        reply[8] = 0x01; reply[9] = 0x2c; // 300 of 100
        reply[10] = Recompute(reply);
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void ATruncatedFrameIsRejected()
    {
        Assert.Null(DdcCodec.ParseGetReply(GoodReply.AsSpan(0, 8), DdcCodec.Luminance));
        Assert.Null(DdcCodec.ParseGetReply([], DdcCodec.Luminance));
    }

    [Fact]
    public void AReplyFromSomethingOtherThanADisplayIsRejected()
    {
        var reply = (byte[])GoodReply.Clone();
        reply[0] = 0x50;
        reply[10] = Recompute(reply);
        Assert.Null(DdcCodec.ParseGetReply(reply, DdcCodec.Luminance));
    }

    [Fact]
    public void RequestsCarryTheChecksumTheDisplayExpects()
    {
        var get = DdcCodec.GetRequest(DdcCodec.Luminance);
        Assert.Equal([0x82, 0x01, 0x10, (byte)(0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ 0x10)], get);

        var set = DdcCodec.SetRequest(DdcCodec.Luminance, 40);
        Assert.Equal([0x84, 0x03, 0x10, 0x00, 0x28, (byte)(0x6E ^ 0x51 ^ 0x84 ^ 0x03 ^ 0x10 ^ 0x00 ^ 0x28)], set);
    }

    /// <summary>The reply checksum: 0x50 XORed with the first ten bytes.</summary>
    private static byte Recompute(byte[] reply)
    {
        byte checksum = 0x50;
        for (var i = 0; i < 10; i++)
        {
            checksum ^= reply[i];
        }

        return checksum;
    }
}
