using System.Security.Claims;
using CgShop.Application.Common;
using CgShop.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CgShop.Infrastructure.Identity;

/// <summary>Agrega los claims <c>tenant_id</c> y <c>full_name</c> al principal del usuario.</summary>
public sealed class CgUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.TenantId is { } tenantId)
            identity.AddClaim(new Claim(CgClaimTypes.TenantId, tenantId.ToString()));
        identity.AddClaim(new Claim(CgClaimTypes.FullName, user.FullName));
        if (!string.IsNullOrWhiteSpace(user.PhoneNumber))
            identity.AddClaim(new Claim(ClaimTypes.MobilePhone, user.PhoneNumber));
        return identity;
    }
}

public sealed class IdentityTenantAdminProvisioner(UserManager<ApplicationUser> users) : ITenantAdminProvisioner
{
    public async Task ProvisionAsync(Guid tenantId, string fullName, string email, string password,
        CancellationToken ct = default)
    {
        var user = new ApplicationUser
        {
            UserName = email.Trim(),
            Email = email.Trim(),
            EmailConfirmed = true,
            FullName = fullName.Trim(),
            TenantId = tenantId
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => e.Description).ToList());

        var roleResult = await users.AddToRoleAsync(user, Roles.TenantAdmin);
        if (!roleResult.Succeeded)
            throw new ValidationException(roleResult.Errors.Select(e => e.Description).ToList());
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>Convierte el usuario autenticado en el actor de dominio (para autorización y auditoría).</summary>
    public static ActorInfo ToActor(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var name = user.FindFirstValue(CgClaimTypes.FullName) is { Length: > 0 } full
            ? full
            : user.Identity?.Name ?? "Anónimo";
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        return new ActorInfo(id, name, roles);
    }
}
