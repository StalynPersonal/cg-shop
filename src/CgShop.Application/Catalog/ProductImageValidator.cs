using System.Buffers.Binary;
using CgShop.Application.Common;

namespace CgShop.Application.Catalog;

public sealed class CatalogOptions
{
    /// <summary>Tamaño máximo por foto de producto (bytes). Por defecto 5 MB.</summary>
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxImagesPerProduct { get; set; } = 10;

    /// <summary>Lado mínimo en píxeles (evita fotos borrosas en la tienda).</summary>
    public int MinImageDimension { get; set; } = 300;

    public int MaxImageDimension { get; set; } = 6000;
}

public sealed record ImageInfo(string Format, string Extension, string ContentType, int Width, int Height);

/// <summary>
/// Valida fotos de producto por su contenido real (no por la extensión):
/// formato JPG/PNG/WebP, tamaño máximo y dimensiones mínimas/máximas leídas del encabezado.
/// </summary>
public static class ProductImageValidator
{
    public const string AcceptAttribute = ".jpg,.jpeg,.png,.webp";

    public static ImageInfo Validate(string fileName, ReadOnlySpan<byte> content, CatalogOptions options)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "archivo" : Path.GetFileName(fileName);
        if (content.Length == 0)
            throw new ValidationException([$"'{name}' está vacío."]);
        if (content.Length > options.MaxImageBytes)
            throw new ValidationException([$"'{name}' pesa {content.Length / 1024d / 1024d:0.0} MB; el máximo es {options.MaxImageBytes / 1024d / 1024d:0.#} MB."]);

        var info = TryRead(content)
                   ?? throw new ValidationException([$"'{name}' no es una imagen JPG, PNG o WebP válida."]);

        var ext = Path.GetExtension(name).ToLowerInvariant();
        var extensionMatches = info.Format switch
        {
            "JPEG" => ext is ".jpg" or ".jpeg",
            "PNG" => ext == ".png",
            "WEBP" => ext == ".webp",
            _ => false
        };
        if (!extensionMatches)
            throw new ValidationException([$"'{name}': la extensión no corresponde al contenido ({info.Format})."]);

        if (info.Width < options.MinImageDimension || info.Height < options.MinImageDimension)
            throw new ValidationException([$"'{name}' mide {info.Width}×{info.Height} px; el mínimo es {options.MinImageDimension}×{options.MinImageDimension} px."]);
        if (info.Width > options.MaxImageDimension || info.Height > options.MaxImageDimension)
            throw new ValidationException([$"'{name}' mide {info.Width}×{info.Height} px; el máximo es {options.MaxImageDimension} px por lado."]);

        return info;
    }

    /// <summary>Identifica el formato y lee las dimensiones del encabezado. Null si no es una imagen soportada.</summary>
    public static ImageInfo? TryRead(ReadOnlySpan<byte> data)
    {
        try
        {
            return ReadPng(data) ?? ReadJpeg(data) ?? ReadWebp(data);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // encabezado truncado o corrupto
        }
    }

    private static ImageInfo? ReadPng(ReadOnlySpan<byte> d)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length < 24 || !d[..8].SequenceEqual(signature) || !d.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
        return width > 0 && height > 0 ? new ImageInfo("PNG", ".png", "image/png", width, height) : null;
    }

    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            return null;

        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
                return null;
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                            // relleno
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; } // sin longitud
            if (marker == 0xD9) return null;                                  // fin sin SOF

            var length = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
            var isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2));
                return width > 0 && height > 0 ? new ImageInfo("JPEG", ".jpg", "image/jpeg", width, height) : null;
            }

            if (length < 2)
                return null;
            i += 2 + length;
        }

        return null;
    }

    private static ImageInfo? ReadWebp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30 || !d[..4].SequenceEqual("RIFF"u8) || !d.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        var chunk = d.Slice(12, 4);
        int width, height;
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (d[24] | d[25] << 8 | d[26] << 16);
            height = 1 + (d[27] | d[28] << 8 | d[29] << 16);
        }
        else if (chunk.SequenceEqual("VP8 "u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            if (d[20] != 0x2F) return null;
            width = 1 + ((d[22] & 0x3F) << 8 | d[21]);
            height = 1 + ((d[24] & 0x0F) << 10 | d[23] << 2 | (d[22] & 0xC0) >> 6);
        }
        else
        {
            return null;
        }

        return width > 0 && height > 0 ? new ImageInfo("WEBP", ".webp", "image/webp", width, height) : null;
    }
}
