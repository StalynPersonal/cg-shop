using CgShop.Domain.Orders;

namespace CgShop.Application.Orders;

internal static class OrderMapper
{
    public static OrderDetailDto ToDetail(Order o) => new(
        o.Id, o.Number, o.Status, o.PaymentMethod, o.DeliveryMethod, o.CustomerName, o.CustomerEmail, o.CustomerPhone,
        o.ShippingAddress, o.Subtotal, o.Tax, o.Total, o.Currency, o.CreatedAtUtc, o.ReservationExpiresAtUtc,
        o.PaymentValidatedAtUtc, o.PaymentValidatedBy,
        o.Items.Select(i => new OrderItemDto(i.Sku, i.ProductName, i.VariantDescription, i.UnitPrice, i.Quantity,
            i.LineTotal)).ToList(),
        o.History.OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.Id)
            .Select(h => new OrderHistoryDto(h.FromStatus, h.ToStatus, h.ChangedByName, h.Note, h.ReceiptId,
                h.ChangedAtUtc)).ToList(),
        o.Receipts.OrderBy(r => r.UploadedAtUtc).Select(ToDto).ToList(),
        OrderStateMachine.NextStatuses(o.Status));

    public static ReceiptDto ToDto(PaymentReceipt r) =>
        new(r.Id, r.OriginalFileName, r.ContentType, r.SizeBytes, r.Reference, r.UploadedAtUtc);
}
