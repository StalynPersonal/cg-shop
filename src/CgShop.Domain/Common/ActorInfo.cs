namespace CgShop.Domain.Common;

/// <summary>Quién ejecuta una acción de dominio (para autorización y auditoría).</summary>
public sealed record ActorInfo(string UserId, string DisplayName, IReadOnlyCollection<string> Roles)
{
    public bool IsInRole(string role) => Roles.Contains(role);
    public bool IsTenantAdmin => IsInRole(Common.Roles.TenantAdmin);
    public bool IsTenantStaff => IsTenantAdmin || IsInRole(Common.Roles.TenantStaff);

    public static ActorInfo System { get; } = new("system", "Sistema", []);
}
