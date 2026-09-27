using System.Text.RegularExpressions;
using CgShop.Domain.Common;

namespace CgShop.Domain.Tenants;

/// <summary>Empresa/tienda registrada en la plataforma. No es ITenantEntity: es la raíz del aislamiento.</summary>
public sealed partial class Tenant : Entity
{
    public const decimal DefaultTaxRate = 0.18m; // ITBIS RD
    public const string DefaultCurrency = "DOP";

    private static readonly HashSet<string> ReservedSlugs = ["admin", "www", "api", "app", "static"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    private Tenant() { }

    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string PrimaryColor { get; private set; } = "";
    public string? SecondaryColor { get; private set; }
    public string? LogoUrl { get; private set; }
    public TenantStatus Status { get; private set; } = TenantStatus.Active;
    public decimal TaxRate { get; private set; } = DefaultTaxRate;
    public string Currency { get; private set; } = DefaultCurrency;
    public string? ContactEmail { get; private set; }
    public PaymentSettings PaymentSettings { get; private set; } = new();
    public DateTime CreatedAtUtc { get; private set; }

    public bool IsActive => Status == TenantStatus.Active;

    /// <summary>Alta de empresa. El color primario es obligatorio (branding definido por el Super Admin).</summary>
    public static Tenant Create(string name, string slug, string primaryColor, string? contactEmail = null,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("El nombre de la empresa es obligatorio.");

        var tenant = new Tenant
        {
            Name = name.Trim(),
            Slug = ValidateSlug(slug),
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
            CreatedAtUtc = nowUtc ?? DateTime.UtcNow
        };
        tenant.SetBranding(primaryColor, null, null);
        return tenant;
    }

    public static bool IsValidSlug(string? slug) =>
        slug is not null && SlugPattern().IsMatch(slug) && !ReservedSlugs.Contains(slug);

    private static string ValidateSlug(string? slug)
    {
        var normalized = slug?.Trim().ToLowerInvariant() ?? "";
        if (!IsValidSlug(normalized))
            throw new DomainException(
                $"El subdominio '{slug}' no es válido: use 3-40 caracteres a-z, 0-9 o '-' y no use nombres reservados.");
        return normalized;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("El nombre de la empresa es obligatorio.");
        Name = name.Trim();
    }

    public void SetBranding(string primaryColor, string? secondaryColor, string? logoUrl)
    {
        if (!HexColor.IsValid(primaryColor))
            throw new DomainException("El color primario es obligatorio y debe tener formato hexadecimal #RRGGBB.");
        if (!string.IsNullOrWhiteSpace(secondaryColor) && !HexColor.IsValid(secondaryColor))
            throw new DomainException("El color secundario debe tener formato hexadecimal #RRGGBB.");

        PrimaryColor = HexColor.Normalize(primaryColor);
        SecondaryColor = string.IsNullOrWhiteSpace(secondaryColor) ? null : HexColor.Normalize(secondaryColor);
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
    }

    public void SetTaxRate(decimal rate)
    {
        if (rate is < 0 or > 1)
            throw new DomainException("La tasa de impuesto debe estar entre 0 y 1.");
        TaxRate = rate;
    }

    public void UpdatePaymentSettings(PaymentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PaymentLinkUrl is { Length: > 0 } url &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new DomainException("El enlace de pago debe ser una URL https válida.");

        if (!string.IsNullOrWhiteSpace(settings.WhatsAppNumber) && !WhatsApp.IsValid(settings.WhatsAppNumber))
            throw new DomainException("El número de WhatsApp no es válido (use código de país, ej. +1 809 555 1234).");
        if (settings.ShowWhatsAppButton && string.IsNullOrWhiteSpace(settings.WhatsAppNumber))
            throw new DomainException("Para mostrar el botón flotante de WhatsApp indique el número.");

        foreach (var account in settings.BankAccounts)
        {
            if (string.IsNullOrWhiteSpace(account.BankName) || string.IsNullOrWhiteSpace(account.AccountNumber) ||
                string.IsNullOrWhiteSpace(account.AccountHolder))
                throw new DomainException("Cada cuenta bancaria requiere banco, número y titular.");
        }

        // Copia defensiva: nunca reutilizar instancias ya rastreadas por EF (columna JSON propia);
        // reasignarlas a un nuevo PaymentSettings rompe la clave sintetizada del owned collection.
        PaymentSettings = new PaymentSettings
        {
            BankAccounts = settings.BankAccounts.Select(b => new BankAccount
            {
                BankName = b.BankName.Trim(),
                AccountNumber = b.AccountNumber.Trim(),
                AccountHolder = b.AccountHolder.Trim(),
                AccountType = b.AccountType,
                HolderDocument = string.IsNullOrWhiteSpace(b.HolderDocument) ? null : b.HolderDocument.Trim()
            }).ToList(),
            PaymentLinkUrl = string.IsNullOrWhiteSpace(settings.PaymentLinkUrl) ? null : settings.PaymentLinkUrl.Trim(),
            Instructions = string.IsNullOrWhiteSpace(settings.Instructions) ? null : settings.Instructions.Trim(),
            PickupAddress = string.IsNullOrWhiteSpace(settings.PickupAddress) ? null : settings.PickupAddress.Trim(),
            WhatsAppNumber = string.IsNullOrWhiteSpace(settings.WhatsAppNumber) ? null : settings.WhatsAppNumber.Trim(),
            ShowWhatsAppButton = settings.ShowWhatsAppButton
        };
    }

    public void Suspend() => Status = TenantStatus.Suspended;
    public void Activate() => Status = TenantStatus.Active;
}
