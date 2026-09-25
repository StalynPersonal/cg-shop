using Microsoft.AspNetCore.Identity;

namespace CgShop.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Tenant al que pertenece el usuario. Nulo para Super Admin.</summary>
    public Guid? TenantId { get; set; }

    public string FullName { get; set; } = "";
}

public static class CgClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string FullName = "full_name";
}
