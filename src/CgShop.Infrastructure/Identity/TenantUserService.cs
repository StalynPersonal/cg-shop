using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Infrastructure.Identity;

public sealed record RegisterCustomerRequest(string FullName, string Email, string Phone, string Password);

/// <summary>
/// Usuarios con alcance de tienda. El correo es único POR TIENDA (un cliente puede registrarse
/// con el mismo correo en tiendas distintas); por eso el login busca por correo + tenant.
/// </summary>
public sealed class TenantUserService(UserManager<ApplicationUser> users)
{
    /// <summary>
    /// Usuario que puede iniciar sesión en el host actual: en admin.* solo Super Admin;
    /// en una tienda, solo usuarios de esa tienda.
    /// </summary>
    public async Task<ApplicationUser?> FindForLoginAsync(string? email, ITenantContext tenant,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var normalized = users.NormalizeEmail(email.Trim());

        if (tenant.IsAdminHost)
        {
            var candidates = await users.Users.Where(u => u.NormalizedEmail == normalized && u.TenantId == null)
                .ToListAsync(ct);
            foreach (var user in candidates)
                if (await users.IsInRoleAsync(user, Roles.SuperAdmin))
                    return user;
            return null;
        }

        return tenant.TenantId is { } tenantId
            ? await users.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && u.TenantId == tenantId, ct)
            : null;
    }

    /// <summary>Registra un cliente de la tienda actual con rol Customer.</summary>
    public async Task<ApplicationUser> RegisterCustomerAsync(RegisterCustomerRequest request, ITenantContext tenant,
        CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId ?? throw new ForbiddenException("El registro solo está disponible en una tienda.");

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.FullName)) errors.Add("El nombre es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) errors.Add("Correo electrónico inválido.");
        if (string.IsNullOrWhiteSpace(request.Phone)) errors.Add("El teléfono es obligatorio.");
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8)
            errors.Add("La contraseña debe tener al menos 8 caracteres.");
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var email = request.Email.Trim();
        var normalized = users.NormalizeEmail(email);
        if (await users.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.TenantId == tenantId, ct))
            throw new ValidationException(["Ya existe una cuenta con ese correo en esta tienda. Inicia sesión."]);

        var user = new ApplicationUser
        {
            // UserName es único global en Identity: se hace único por tienda con un prefijo.
            UserName = $"c.{tenantId:N}.{email}",
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            PhoneNumber = request.Phone.Trim(),
            TenantId = tenantId
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => e.Description).ToList());
        await users.AddToRoleAsync(user, Roles.Customer);
        return user;
    }
}
