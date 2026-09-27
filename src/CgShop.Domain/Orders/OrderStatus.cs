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

public enum DeliveryMethod
{
    /// <summary>Envío a domicilio (costo adicional según zona, coordinado por la tienda).</summary>
    Shipping = 1,

    /// <summary>Retiro en tienda (sin dirección de envío).</summary>
    Pickup = 2
}

public static class OrderStatusExtensions
{
    public static string DisplayName(this DeliveryMethod method) => method switch
    {
        DeliveryMethod.Shipping => "Envío a domicilio",
        DeliveryMethod.Pickup => "Retiro en tienda",
        _ => method.ToString()
    };

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
