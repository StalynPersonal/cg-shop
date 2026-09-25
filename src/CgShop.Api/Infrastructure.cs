using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CgShop.Application.Common;
using CgShop.Domain.Common;
using CgShop.Infrastructure.Identity;
using CgShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CgShop.Api;

public static class ApiPolicies
{
    public const string TenantStaff = nameof(TenantStaff);
    public const string TenantAdmin = nameof(TenantAdmin);
    public const string SuperAdmin = nameof(SuperAdmin);
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "cgshop";
    public string Audience { get; set; } = "cgshop-api";
    public string Key { get; set; } = "";
    public int ExpirationMinutes { get; set; } = 120;
}

/// <summary>Emite JWT con los mismos claims que la cookie web (roles, tenant_id, full_name).</summary>
public sealed class JwtTokenService(IUserClaimsPrincipalFactory<ApplicationUser> principals, IOptions<JwtOptions> options,
    TimeProvider clock)
{
    public async Task<(string Token, DateTime ExpiresAtUtc)> CreateAsync(ApplicationUser user)
    {
        var o = options.Value;
        var principal = await principals.CreateAsync(user);
        var claims = principal.Claims.Where(c => c.Type != "AspNet.Identity.SecurityStamp").ToList();
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(o.ExpirationMinutes);
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, clock.GetUtcNow().UtcDateTime, expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler { MapInboundClaims = false }.WriteToken(token), expires);
    }
}

/// <summary>Traduce excepciones de negocio a ProblemDetails (RFC 9457).</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Datos inválidos"),
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Regla de negocio"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Acceso denegado"),
            NotFoundException => (StatusCodes.Status404NotFound, "No encontrado"),
            CrossTenantWriteException => (StatusCodes.Status403Forbidden, "Acceso denegado"),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflicto de concurrencia"),
            _ => (0, "")
        };
        if (status == 0)
            return false;

        http.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title, Detail = exception.Message };
        if (exception is ValidationException ve)
            details.Extensions["errors"] = ve.Errors;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details });
    }
}
