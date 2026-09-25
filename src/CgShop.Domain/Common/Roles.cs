namespace CgShop.Domain.Common;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string TenantStaff = "TenantStaff";
    public const string Customer = "Customer";

    public static readonly string[] All = [SuperAdmin, TenantAdmin, TenantStaff, Customer];
}
