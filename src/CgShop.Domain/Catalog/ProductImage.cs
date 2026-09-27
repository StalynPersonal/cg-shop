using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

/// <summary>Foto de un producto. La de menor <see cref="SortOrder"/> es la principal.</summary>
public sealed class ProductImage : Entity, ITenantEntity
{
    private ProductImage() { }

    internal ProductImage(Guid productId, string storagePath, string contentType, long sizeBytes, int width,
        int height, string originalFileName, int sortOrder, DateTime uploadedAtUtc)
    {
        ProductId = productId;
        StoragePath = storagePath;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Width = width;
        Height = height;
        OriginalFileName = originalFileName;
        SortOrder = sortOrder;
        UploadedAtUtc = uploadedAtUtc;
    }

    public Guid TenantId { get; set; }
    public Guid ProductId { get; private set; }
    public string StoragePath { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string OriginalFileName { get; private set; } = "";
    public int SortOrder { get; internal set; }

    /// <summary>Color de la variante que muestra la foto; null = aplica a todos los colores.</summary>
    public string? Color { get; internal set; }
    public DateTime UploadedAtUtc { get; private set; }
}
