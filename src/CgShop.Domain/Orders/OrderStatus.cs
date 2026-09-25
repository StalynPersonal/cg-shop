namespace CgShop.Domain.Orders;

public enum OrderStatus
{
    PendingPaymentValidation = 1,
    PaymentValidated = 2,
    Preparing = 3,
    Shipped = 4,
    Delivered = 5,
    PaymentRejected = 10,
    Cancelled = 11,
    Expired = 12
}

public enum PaymentMethod
{
    BankTransfer = 1,
    PaymentLink = 2
}

public static class OrderStatusExtensions
{
    public static string DisplayName(this OrderStatus status) => status switch
    {
        OrderStatus.PendingPaymentValidation => "Pendiente de validación de pago",
        OrderStatus.PaymentValidated => "Pago validado",
        OrderStatus.Preparing => "En preparación",
        OrderStatus.Shipped => "Enviado",
        OrderStatus.Delivered => "Entregado",
        OrderStatus.PaymentRejected => "Pago rechazado",
        OrderStatus.Cancelled => "Cancelado",
        OrderStatus.Expired => "Expirado",
        _ => status.ToString()
    };

    public static string DisplayName(this PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => "Transferencia bancaria",
        PaymentMethod.PaymentLink => "Enlace de pago",
        _ => method.ToString()
    };
}
