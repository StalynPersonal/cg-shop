using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;

namespace CgShop.Application.Orders;

public sealed record CartLine(Guid VariantId, int Quantity);

public sealed class PlaceOrderRequest
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public DeliveryMethod DeliveryMethod { get; set; } = DeliveryMethod.Shipping;

    /// <summary>Obligatoria solo si <see cref="DeliveryMethod"/> es envío.</summary>
    public string? ShippingAddress { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;
    public List<CartLine> Lines { get; set; } = [];
    public string? CustomerUserId { get; set; }
}

public sealed record PlaceOrderResult(Guid OrderId, string Number, string AccessToken, decimal Total,
    DateTime ReservationExpiresAtUtc);

public sealed record OrderItemDto(string Sku, string ProductName, string VariantDescription, decimal UnitPrice,
    int Quantity, decimal LineTotal);

public sealed record OrderHistoryDto(OrderStatus? From, OrderStatus To, string ChangedBy, string? Note,
    Guid? ReceiptId, DateTime AtUtc);

public sealed record ReceiptDto(Guid Id, string FileName, string ContentType, long SizeBytes, string? Reference,
    DateTime UploadedAtUtc);

public sealed record OrderDetailDto(
    Guid Id,
    string Number,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    DeliveryMethod DeliveryMethod,
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string? ShippingAddress,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    string Currency,
    DateTime CreatedAtUtc,
    DateTime ReservationExpiresAtUtc,
    DateTime? PaymentValidatedAtUtc,
    string? PaymentValidatedBy,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<OrderHistoryDto> History,
    IReadOnlyList<ReceiptDto> Receipts,
    IReadOnlyList<OrderStatus> NextStatuses);

public sealed record OrderRowDto(
    Guid Id,
    string Number,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    DeliveryMethod DeliveryMethod,
    string CustomerName,
    string CustomerEmail,
    decimal Total,
    int ItemCount,
    int ReceiptCount,
    DateTime CreatedAtUtc,
    DateTime ReservationExpiresAtUtc);

public sealed record PaymentInstructionsDto(
    string StoreName,
    string Currency,
    PaymentMethod Method,
    IReadOnlyList<BankAccount> BankAccounts,
    string? PaymentLinkUrl,
    string? Instructions,
    string? PickupAddress,
    string? WhatsAppNumber);

/// <summary>Pedido en "Mis pedidos". Incluye el token porque el dueño autenticado puede abrir su pedido.</summary>
public sealed record CustomerOrderRowDto(
    string Number,
    string AccessToken,
    OrderStatus Status,
    DeliveryMethod DeliveryMethod,
    decimal Total,
    string Currency,
    int ItemCount,
    DateTime CreatedAtUtc,
    DateTime ReservationExpiresAtUtc);

public sealed record OrderQuery(OrderStatus? Status = null, string? Search = null, int Page = 1, int PageSize = 25);

public sealed record DashboardDto(
    int PendingValidation,
    int ToPrepare,
    int Shipped,
    int OrdersToday,
    decimal ValidatedRevenue,
    int ActiveProducts,
    int LowStockVariants,
    IReadOnlyDictionary<OrderStatus, int> ByStatus);
