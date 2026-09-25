using CgShop.Domain.Tenants;

namespace CgShop.Application.Tenants;

public sealed class CreateTenantRequest
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";

    /// <summary>Color primario obligatorio del branding (#RRGGBB).</summary>
    public string PrimaryColor { get; set; } = "";

    public string? SecondaryColor { get; set; }
    public string? LogoUrl { get; set; }
    public string? ContactEmail { get; set; }
    public decimal TaxRate { get; set; } = Tenant.DefaultTaxRate;

    public string AdminFullName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
}

public sealed class UpdateBrandingRequest
{
    public string Name { get; set; } = "";
    public string PrimaryColor { get; set; } = "";
    public string? SecondaryColor { get; set; }
    public string? LogoUrl { get; set; }
    public decimal TaxRate { get; set; } = Tenant.DefaultTaxRate;
}

public sealed record TenantSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryColor,
    string? SecondaryColor,
    string? LogoUrl,
    TenantStatus Status,
    string? ContactEmail,
    decimal TaxRate,
    DateTime CreatedAtUtc,
    int ProductCount,
    int OrderCount,
    int PendingValidationCount);

public sealed record PlatformStatsDto(int Tenants, int ActiveTenants, int Products, int Orders, int PendingValidation);
