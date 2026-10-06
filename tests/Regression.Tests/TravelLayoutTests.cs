using System.Buffers.Binary;
using System.Numerics;
using FF14Accessibility.Services;

namespace Regression.Tests;

public sealed class TravelLayoutTests
{
    [Fact]
    public void MapRangeProvidesActualFloorInsteadOfOldArrivalMap()
    {
        var parsed = TravelLayout.Read(Layout());
        var range = Assert.Single(parsed.Maps);
        Assert.Equal(548u, range.MapId);
        Assert.Equal(100, range.Priority);
        Assert.Equal(548u, parsed.ResolveMap(new(-151, -129, 260), 12));
        Assert.Equal(12u, parsed.ResolveMap(new(-151, 2.8f, 260), 12));
    }

    [Fact]
    public void PriorityDeterminesNestedMapAndEqualPriorityConflictRejectsFallback()
    {
        var parsed = TravelLayout.Read(Layout());
        var same = parsed.Maps[0];
        parsed.Maps.Add(same with { MapId = 12, Priority = 90 });
        Assert.Equal(548u, parsed.MapAt(same.Position));
        parsed.Maps.Add(same with { MapId = 74 });
        Assert.Equal(0u, parsed.ResolveMap(same.Position, 12));
    }

    [Theory]
    [InlineData(1, 1, 9, true)]
    [InlineData(1, 1, 11, false)]
    [InlineData(2, 9, 0, true)]
    [InlineData(2, 11, 0, false)]
    [InlineData(3, 9, 9, true)]
    [InlineData(3, 11, 0, false)]
    [InlineData(3, 0, 11, false)]
    [InlineData(5, 0, 0, false)]
    public void ShapesUseHalfExtentsAndRetainVerticalSeparation(int shape, float x, float y, bool expected)
        => Assert.Equal(expected, new TravelMapRange(548, Vector3.Zero, Vector3.Zero, new(10), shape, 100).Contains(new(x, y, 0)));

    [Fact]
    public void RotatedBoxUsesItsLocalCoordinates()
    {
        var range = new TravelMapRange(548, Vector3.Zero, new(0, MathF.PI / 2, 0), new(2, 3, 10), 1, 100);
        Assert.True(range.Contains(new(9, 0, 1)));
        Assert.False(range.Contains(new(1, 0, 9)));
        Assert.False((range with { Rotation = new(0.1f, 0, 0) }).Contains(Vector3.Zero));
    }

    [Theory]
    [InlineData(114)] // TriggerBox.Enabled
    [InlineData(153)] // MapRange.MapEnabled, not DiscoveryEnabled
    public void DisabledMapRangesCannotResolveArrival(int offset)
    {
        var bytes = Layout(); bytes[offset] = 0;
        Assert.Empty(TravelLayout.Read(bytes).Maps);
    }

    [Fact]
    public void MissingLevelArrivalIsReadByExactLayoutInstanceId()
    {
        var bytes = Layout(); Write(bytes, 60, 40); Write(bytes, 64, 4158063);
        var arrival = Assert.Single(TravelLayout.Read(bytes).Arrivals);
        Assert.Equal(4158063u, arrival.Key);
        Assert.Equal(new Vector3(-151, -127, 265), arrival.Value);
    }

    [Theory]
    [InlineData(28, int.MaxValue)]
    [InlineData(32, -1)]
    [InlineData(32, 100001)]
    [InlineData(36, -100)]
    [InlineData(48, int.MaxValue)]
    [InlineData(52, -1)]
    [InlineData(56, int.MaxValue)]
    public void InvalidBoundsRejectEntireTravelFile(int offset, int value)
    {
        var bytes = Layout(); Write(bytes, offset, value);
        var parsed = TravelLayout.Read(bytes);
        Assert.Empty(parsed.Maps); Assert.Empty(parsed.Objects); Assert.Empty(parsed.Arrivals);
    }

    [Fact]
    public void CorruptLaterObjectCannotLeakEarlierArrival()
    {
        var bytes = Layout(); Write(bytes, 52, 2); Write(bytes, 56, 8); Write(bytes, 60, int.MaxValue);
        Write(bytes, 64, 40);
        Assert.Empty(TravelLayout.Read(bytes).Arrivals);
        Assert.Empty(TravelLayout.Read(Layout()[..150]).Maps);
    }

    private static byte[] Layout()
    {
        var bytes = new byte[180]; "LGB1"u8.CopyTo(bytes); "LGP1"u8.CopyTo(bytes.AsSpan(12));
        Write(bytes, 28, 16); Write(bytes, 32, 1); Write(bytes, 36, 4);
        Write(bytes, 48, 16); Write(bytes, 52, 1); Write(bytes, 56, 4);
        Write(bytes, 60, 43); Write(bytes, 64, 1);
        Float(bytes, 72, -151); Float(bytes, 76, -127); Float(bytes, 80, 265);
        Float(bytes, 96, 13); Float(bytes, 100, 10); Float(bytes, 104, 12);
        Write(bytes, 108, 1); bytes[112] = 100; bytes[114] = 1;
        Write(bytes, 120, 548); bytes[153] = 1;
        return bytes;
    }
    private static void Float(byte[] bytes, int offset, float value) => Write(bytes, offset, BitConverter.SingleToInt32Bits(value));
    private static void Write(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
