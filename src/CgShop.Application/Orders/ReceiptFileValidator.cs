namespace CgShop.Application.Orders;

/// <summary>Valida comprobantes: solo JPG/PNG/PDF, tamaño máximo y firma binaria real (no solo la extensión).</summary>
public static class ReceiptFileValidator
{
    private static readonly Dictionary<string, (string ContentType, byte[] Signature)> Allowed = new()
    {
        [".jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".png"] = ("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        [".pdf"] = ("application/pdf", [0x25, 0x50, 0x44, 0x46]) // %PDF
    };

    public static IReadOnlyCollection<string> AllowedExtensions => Allowed.Keys;

    public static string AcceptAttribute => ".jpg,.jpeg,.png,.pdf";

    /// <summary>Devuelve (extensión normalizada, content-type) o lanza <see cref="Common.ValidationException"/>.</summary>
    public static (string Extension, string ContentType) Validate(string fileName, ReadOnlySpan<byte> header,
        long size, long maxBytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Allowed.TryGetValue(ext, out var spec))
            throw new Common.ValidationException(["Formato no permitido. Use JPG, PNG o PDF."]);
        if (size <= 0)
            throw new Common.ValidationException(["El archivo está vacío."]);
        if (size > maxBytes)
            throw new Common.ValidationException([$"El archivo supera el máximo de {maxBytes / (1024 * 1024)} MB."]);
        if (header.Length < spec.Signature.Length || !header[..spec.Signature.Length].SequenceEqual(spec.Signature))
            throw new Common.ValidationException(["El contenido del archivo no corresponde a su extensión."]);
        return (ext == ".jpeg" ? ".jpg" : ext, spec.ContentType);
    }
}
