using System.Buffers.Binary;

namespace CgShop.Tests.Shared;

/// <summary>
/// Genera archivos con encabezados reales de PNG/JPEG/WebP (dimensiones arbitrarias) para probar
/// la validación de fotos sin depender de librerías de imagen.
/// </summary>
public static class TestImages
{
    public static byte[] Png(int width, int height, int padding = 64)
    {
        var data = new byte[33 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        data[24] = 8; // bit depth
        data[25] = 2; // color type RGB
        return data;
    }

    public static byte[] Jpeg(int width, int height, int padding = 64)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        // APP0 (JFIF) de 16 bytes
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        // SOF0: longitud 17, precisión 8, alto, ancho, 3 componentes
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        bytes.AddRange(new byte[padding]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    public static byte[] WebpVp8X(int width, int height, int padding = 64)
    {
        var data = new byte[30 + padding];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        "VP8X"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 10);
        WriteUInt24(data.AsSpan(24), width - 1);
        WriteUInt24(data.AsSpan(27), height - 1);
        return data;
    }

    public static byte[] WebpVp8L(int width, int height, int padding = 64)
    {
        var data = new byte[30 + padding];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        "VP8L"u8.CopyTo(data.AsSpan(12));
        data[20] = 0x2F;
        var bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), bits);
        return data;
    }

    /// <summary>Imagen válida de formato rotativo (PNG/JPG/WebP) para el índice dado.</summary>
    public static (string FileName, byte[] Content, int Width, int Height) Any(int i)
    {
        var width = 400 + i * 7 % 2000;
        var height = 300 + i * 13 % 1800;
        return (i % 4) switch
        {
            0 => ($"foto-{i}.png", Png(width, height), width, height),
            1 => ($"foto-{i}.jpg", Jpeg(width, height), width, height),
            2 => ($"foto-{i}.webp", WebpVp8X(width, height), width, height),
            _ => ($"foto-{i}.webp", WebpVp8L(width, height), width, height)
        };
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
