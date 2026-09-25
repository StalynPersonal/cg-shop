using System.Security.Claims;
using System.Text;
using CgShop.Api;
using CgShop.Api.Endpoints;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Infrastructure;
using CgShop.Infrastructure.Persistence;
using CgShop.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration,
    enableBackgroundJobs: builder.Configuration.GetValue("Jobs:Enabled", true));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key debe tener al menos 32 caracteres.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiPolicies.TenantStaff, p => p.RequireRole(Roles.TenantAdmin, Roles.TenantStaff))
    .AddPolicy(ApiPolicies.TenantAdmin, p => p.RequireRole(Roles.TenantAdmin))
    .AddPolicy(ApiPolicies.SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddScoped<JwtTokenService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    scope.ServiceProvider.GetRequiredService<TenantContext>().EnterSystemScope();
    await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { name = "CG Shop API", version = "1.0" }));
app.MapAuthEndpoints();
app.MapStoreEndpoints();
app.MapAdminEndpoints();
app.MapSuperAdminEndpoints();

app.Run();

public partial class Program;
