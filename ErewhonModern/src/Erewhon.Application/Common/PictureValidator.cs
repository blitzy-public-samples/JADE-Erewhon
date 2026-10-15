namespace Erewhon.Application.Common;

/// <summary>
/// Picture format of a photo's content. The numeric values are the legacy picture type codes;
/// the value 2 is unassigned.
/// </summary>
public enum PictureType
{
    /// <summary>The content matches none of the accepted signatures.</summary>
    Invalid = 0,

    /// <summary>Windows bitmap (BMP).</summary>
    Bitmap = 1,

    /// <summary>Windows icon (ICO).</summary>
    Icon = 3,

    /// <summary>Windows metafile: placeable WMF or enhanced metafile (EMF).</summary>
    Metafile = 4,

    /// <summary>Windows cursor (CUR).</summary>
    Cursor = 5,

    /// <summary>Tagged Image File Format (TIFF), little- or big-endian.</summary>
    Tiff = 6,

    /// <summary>JPEG image.</summary>
    Jpeg = 7,

    /// <summary>Portable Network Graphics (PNG).</summary>
    Png = 8,
}

/// <summary>
/// Classifies photo content by its leading file signature and decides whether a photo is a valid picture.
/// </summary>
/// <remarks>
/// Accepted signatures, checked in this order (hexadecimal bytes from offset 0 unless stated):
/// <list type="bullet">
/// <item><description>PNG: <c>89 50 4E 47 0D 0A 1A 0A</c>.</description></item>
/// <item><description>JPEG: <c>FF D8 FF</c>.</description></item>
/// <item><description>TIFF: <c>49 49 2A 00</c> or <c>4D 4D 00 2A</c>.</description></item>
/// <item><description>Icon: <c>00 00 01 00</c>.</description></item>
/// <item><description>Cursor: <c>00 00 02 00</c>.</description></item>
/// <item><description>Placeable WMF: <c>D7 CD C6 9A</c>.</description></item>
/// <item><description>EMF: <c>01 00 00 00</c>, and <c>20 45 4D 46</c> (" EMF") at offset 40.</description></item>
/// <item><description>BMP: <c>42 4D</c>.</description></item>
/// </list>
/// A signature matches only when the content is at least as long as the bytes it compares.
/// Content after the signature is not decoded.
/// </remarks>
public static class PictureValidator
{
    private const int EmfSignatureOffset = 40;

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static ReadOnlySpan<byte> JpegSignature => new byte[] { 0xFF, 0xD8, 0xFF };

    private static ReadOnlySpan<byte> TiffLittleEndianSignature => new byte[] { 0x49, 0x49, 0x2A, 0x00 };

    private static ReadOnlySpan<byte> TiffBigEndianSignature => new byte[] { 0x4D, 0x4D, 0x00, 0x2A };

    private static ReadOnlySpan<byte> IconSignature => new byte[] { 0x00, 0x00, 0x01, 0x00 };

    private static ReadOnlySpan<byte> CursorSignature => new byte[] { 0x00, 0x00, 0x02, 0x00 };

    private static ReadOnlySpan<byte> PlaceableWmfSignature => new byte[] { 0xD7, 0xCD, 0xC6, 0x9A };

    private static ReadOnlySpan<byte> EmfRecordType => new byte[] { 0x01, 0x00, 0x00, 0x00 };

    private static ReadOnlySpan<byte> EmfSignature => new byte[] { 0x20, 0x45, 0x4D, 0x46 };

    private static ReadOnlySpan<byte> BitmapSignature => new byte[] { 0x42, 0x4D };

    /// <summary>
    /// Returns the picture type whose signature the content starts with, or <see cref="PictureType.Invalid"/>
    /// when it matches none, including when the content is empty.
    /// </summary>
    /// <param name="data">The photo content.</param>
    public static PictureType GetPictureType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
        {
            return PictureType.Png;
        }

        if (data.StartsWith(JpegSignature))
        {
            return PictureType.Jpeg;
        }

        if (data.StartsWith(TiffLittleEndianSignature) || data.StartsWith(TiffBigEndianSignature))
        {
            return PictureType.Tiff;
        }

        if (data.StartsWith(IconSignature))
        {
            return PictureType.Icon;
        }

        if (data.StartsWith(CursorSignature))
        {
            return PictureType.Cursor;
        }

        if (data.StartsWith(PlaceableWmfSignature) || IsEnhancedMetafile(data))
        {
            return PictureType.Metafile;
        }

        if (data.StartsWith(BitmapSignature))
        {
            return PictureType.Bitmap;
        }

        return PictureType.Invalid;
    }

    /// <summary>
    /// Returns the picture type of the content; <see langword="null"/> is classified as empty content,
    /// which is <see cref="PictureType.Invalid"/>.
    /// </summary>
    /// <param name="data">The photo content, or <see langword="null"/>.</param>
    public static PictureType GetPictureType(byte[]? data) =>
        GetPictureType(new ReadOnlySpan<byte>(data));

    /// <summary>
    /// Returns <see langword="true"/> when there is no photo (<see langword="null"/> or empty) or when the photo
    /// matches one of the accepted signatures; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="photo">The photo content, or <see langword="null"/> when there is none.</param>
    public static bool IsValidPicture(byte[]? photo) =>
        photo is null || photo.Length == 0 || GetPictureType(photo) != PictureType.Invalid;

    private static bool IsEnhancedMetafile(ReadOnlySpan<byte> data) =>
        data.Length >= EmfSignatureOffset + EmfSignature.Length
        && data.StartsWith(EmfRecordType)
        && data[EmfSignatureOffset..].StartsWith(EmfSignature);
}
