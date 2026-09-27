namespace CgShop.Domain.Tenants;

public sealed class PaymentSettings
{
    public List<BankAccount> BankAccounts { get; set; } = [];
    public string? PaymentLinkUrl { get; set; }
    public string? Instructions { get; set; }

    /// <summary>Dirección donde el cliente retira su pedido (opción "Retiro en tienda").</summary>
    public string? PickupAddress { get; set; }

    /// <summary>WhatsApp de la tienda para que el cliente confirme su pago.</summary>
    public string? WhatsAppNumber { get; set; }

    /// <summary>Muestra en toda la tienda un botón flotante para escribir por WhatsApp (requiere número).</summary>
    public bool ShowWhatsAppButton { get; set; }

    public bool AcceptsBankTransfer => BankAccounts.Count > 0;
    public bool AcceptsPaymentLink => !string.IsNullOrWhiteSpace(PaymentLinkUrl);
}

public sealed class BankAccount
{
    public string BankName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string AccountHolder { get; set; } = "";
    public string AccountType { get; set; } = "Corriente";
    public string? HolderDocument { get; set; }
}
