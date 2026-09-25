namespace CgShop.Infrastructure.Tenancy;

public sealed class TenantResolutionOptions
{
    public const string Section = "Tenancy";
    public const string HeaderName = "X-Tenant";

    /// <summary>Dominios raíz bajo los cuales cada subdominio es un tenant (ej. "cgshop.com", "localhost").</summary>
    public List<string> RootDomains { get; set; } = ["localhost"];

    /// <summary>Subdominio reservado para el panel de Super Admin.</summary>
    public string AdminSubdomain { get; set; } = "admin";

    /// <summary>Permite resolver el tenant con el header X-Tenant (desarrollo y API).</summary>
    public bool AllowHeaderFallback { get; set; }

    /// <summary>
    /// Extrae el slug del host. Devuelve null si el host es un dominio raíz o no pertenece a ninguno.
    /// Solo acepta un único nivel de subdominio (tienda.cgshop.com; no a.b.cgshop.com).
    /// </summary>
    public string? ExtractSubdomain(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var root in RootDomains.Select(r => r.Trim().ToLowerInvariant()).OrderByDescending(r => r.Length))
        {
            if (!h.EndsWith("." + root, StringComparison.Ordinal))
                continue;
            var sub = h[..^(root.Length + 1)];
            return sub.Length == 0 || sub.Contains('.', StringComparison.Ordinal) ? null : sub;
        }

        return null;
    }
}
