using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Fixtures;

public sealed class TestPhotoTests : ErewhonTestBase
{
    private const string PhotoSha256 = "8627ddb70d2494e4f38a290f8b025343706f19437830600708f76f0cf0a64f10";

    public enum WritePath
    {
        BytesArraySegment,
        BytesMemory,
        ToArrayCopy,
    }

    [Theory]
    [InlineData(WritePath.BytesArraySegment)]
    [InlineData(WritePath.BytesMemory)]
    [InlineData(WritePath.ToArrayCopy)]
    public void WritingThroughOneReturnedValueLeavesLaterValuesUnchanged(WritePath path)
    {
        Memory<byte> storage;
        switch (path)
        {
            case WritePath.BytesArraySegment:
                Assert.True(MemoryMarshal.TryGetArray(TestPhoto.Bytes, out var segment));
                storage = segment.AsMemory();
                break;
            case WritePath.BytesMemory:
                storage = MemoryMarshal.AsMemory(TestPhoto.Bytes);
                break;
            case WritePath.ToArrayCopy:
                storage = TestPhoto.ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }

        storage.Span.Fill(0);

        Assert.Equal(TestPhoto.Length, storage.Length);
        Assert.NotEqual(PhotoSha256, Sha256Hex(storage.Span));

        var laterBytes = TestPhoto.Bytes;
        var laterArray = TestPhoto.ToArray();

        Assert.Equal(TestPhoto.Length, laterBytes.Length);
        Assert.Equal(PhotoSha256, Sha256Hex(laterBytes.Span));
        Assert.Equal(TestPhoto.Length, laterArray.Length);
        Assert.Equal(PhotoSha256, Sha256Hex(laterArray));
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
