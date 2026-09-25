using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

/// <summary>Comprobante de transferencia/pago subido por el cliente.</summary>
public sealed class PaymentReceipt : Entity, ITenantEntity
{
    private PaymentReceipt() { }

    internal PaymentReceipt(Guid orderId, string originalFileName, string storagePath, string contentType,
        long sizeBytes, string? reference, DateTime uploadedAtUtc)
    {
        OrderId = orderId;
        OriginalFileName = originalFileName;
        StoragePath = storagePath;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Reference = reference;
        UploadedAtUtc = uploadedAtUtc;
    }

    public Guid TenantId { get; set; }
    public Guid OrderId { get; private set; }
    public string OriginalFileName { get; private set; } = "";
    public string StoragePath { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string? Reference { get; private set; }
    public DateTime UploadedAtUtc { get; private set; }
}
