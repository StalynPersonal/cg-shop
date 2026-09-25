using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CgShop.Api.Endpoints;
using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Tenants;
using CgShop.Domain.Orders;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;

namespace CgShop.IntegrationTests.Api;

/// <summary>Pruebas extremo a extremo de la API HTTP: resolución de tenant, JWT, aislamiento y flujo de pagos.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ApiTests(SqlServerFixture fx) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private ApiFactory _api = null!;

    public Task InitializeAsync()
    {
        _api = new ApiFactory(fx);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static PlaceOrderRequest Order(int i, Guid variantId) => new()
    {
        FullName = $"Cliente API {i}", Email = $"api{i}@correo.com", Phone = "809-000-0000",
        ShippingAddress = $"Calle {i}", PaymentMethod = PaymentMethod.BankTransfer,
        Lines = [new CartLine(variantId, 1)]
    };

    private async Task<List<Guid>> VariantIdsAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<PagedResult<ProductCardDto>>("/api/store/catalog?pageSize=200", Json);
        var ids = new List<Guid>();
        foreach (var p in page!.Items.Take(20))
        {
            var detail = await client.GetFromJsonAsync<ProductDetailDto>($"/api/store/catalog/{p.Slug}", Json);
            ids.AddRange(detail!.Variants.Select(v => v.Id));
        }

        return ids;
    }

    [Fact]
    public async Task Catalog_is_resolved_by_subdomain_or_header_and_isolated_per_tenant()
    {
        var a = await _api.CreateTenantAsync();
        var b = await _api.CreateTenantAsync(products: 10);

        var bySubdomain = await _api.ClientFor(a.Slug).GetFromJsonAsync<PagedResult<ProductCardDto>>("/api/store/catalog?pageSize=200", Json);
        var byHeader = await _api.ClientFor(a.Slug, viaHeader: true).GetFromJsonAsync<PagedResult<ProductCardDto>>("/api/store/catalog?pageSize=200", Json);
        var other = await _api.ClientFor(b.Slug).GetFromJsonAsync<PagedResult<ProductCardDto>>("/api/store/catalog?pageSize=200", Json);

        bySubdomain!.TotalCount.Should().Be(TestData.BatchSize);
        byHeader!.TotalCount.Should().Be(TestData.BatchSize);
        other!.TotalCount.Should().Be(10);
        bySubdomain.Items.Select(i => i.Id).Should().NotIntersectWith(other.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Unknown_tenant_returns_404_and_missing_tenant_returns_400()
    {
        (await _api.ClientFor("no-existe").GetAsync("/api/store/catalog")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var noTenant = _api.CreateClient(new() { BaseAddress = new Uri($"http://{ApiFactory.RootDomain}/") });
        (await noTenant.GetAsync("/api/store/catalog")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Full_flow_100_orders_placed_and_payments_validated_by_owner_via_api()
    {
        var tenant = await _api.CreateTenantAsync();
        var store = _api.ClientFor(tenant.Slug);
        var variants = await VariantIdsAsync(store);

        var placed = new List<PlaceOrderResult>();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var response = await store.PostAsJsonAsync("/api/store/orders", Order(i, variants[i % variants.Count]), Json);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            placed.Add((await response.Content.ReadFromJsonAsync<PlaceOrderResult>(Json))!);
        }

        var admin = await _api.AuthenticatedClientAsync(tenant.Slug, $"admin@{tenant.Slug}.test");
        var dashboard = await admin.GetFromJsonAsync<DashboardDto>("/api/admin/dashboard", Json);
        dashboard!.PendingValidation.Should().Be(TestData.BatchSize);

        foreach (var order in placed)
        {
            var response = await admin.PostAsJsonAsync($"/api/admin/orders/{order.OrderId}/validate-payment",
                new NoteRequest("Transferencia verificada"), Json);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<OrderDetailDto>(Json))!.Status.Should().Be(OrderStatus.PaymentValidated);
        }

        dashboard = await admin.GetFromJsonAsync<DashboardDto>("/api/admin/dashboard", Json);
        dashboard!.PendingValidation.Should().Be(0);
        dashboard.ToPrepare.Should().Be(TestData.BatchSize);
        dashboard.ValidatedRevenue.Should().Be(placed.Sum(p => p.Total));

        // El cliente ve su pedido validado con el token.
        var customerView = await store.GetFromJsonAsync<OrderDetailDto>(
            $"/api/store/orders/{placed[0].Number}?token={placed[0].AccessToken}", Json);
        customerView!.Status.Should().Be(OrderStatus.PaymentValidated);
    }

    [Fact]
    public async Task Staff_and_anonymous_cannot_validate_payments()
    {
        var tenant = await _api.CreateTenantAsync(products: 5);
        var store = _api.ClientFor(tenant.Slug);
        var variants = await VariantIdsAsync(store);
        var order = await (await store.PostAsJsonAsync("/api/store/orders", Order(1, variants[0]), Json))
            .Content.ReadFromJsonAsync<PlaceOrderResult>(Json);

        (await store.PostAsJsonAsync($"/api/admin/orders/{order!.OrderId}/validate-payment", new NoteRequest(null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var staff = await _api.AuthenticatedClientAsync(tenant.Slug, $"staff@{tenant.Slug}.test");
        (await staff.PostAsJsonAsync($"/api/admin/orders/{order.OrderId}/validate-payment", new NoteRequest(null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Tampoco puede despacharse sin pago validado (regla de dominio => 422).
        (await staff.PostAsync($"/api/admin/orders/{order.OrderId}/prepare", null))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Token_of_one_tenant_is_rejected_on_another_tenant()
    {
        var a = await _api.CreateTenantAsync(products: 3);
        var b = await _api.CreateTenantAsync(products: 3);
        var adminA = await _api.AuthenticatedClientAsync(a.Slug, $"admin@{a.Slug}.test");

        var crossClient = _api.ClientFor(b.Slug);
        crossClient.DefaultRequestHeaders.Authorization = adminA.DefaultRequestHeaders.Authorization;

        (await crossClient.GetAsync("/api/admin/orders")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await crossClient.GetAsync("/api/store/catalog")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Y un admin de A no puede obtener token en B.
        var login = await _api.ClientFor(b.Slug).PostAsJsonAsync("/api/auth/token",
            new LoginRequest($"admin@{a.Slug}.test", ApiFactory.Password));
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Customer_uploads_receipt_via_multipart()
    {
        var tenant = await _api.CreateTenantAsync(products: 3);
        var store = _api.ClientFor(tenant.Slug);
        var variants = await VariantIdsAsync(store);
        var order = await (await store.PostAsJsonAsync("/api/store/orders", Order(1, variants[0]), Json))
            .Content.ReadFromJsonAsync<PlaceOrderResult>(Json);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x25, 0x50, 0x44, 0x46, 0x2D, 0x31]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "comprobante.pdf");
        form.Add(new StringContent("REF-777"), "reference");

        var response = await store.PostAsync($"/api/store/orders/{order!.Number}/receipts?token={order.AccessToken}", form);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var bad = new MultipartFormDataContent();
        bad.Add(new ByteArrayContent([0x4D, 0x5A]), "file", "malware.pdf");
        (await store.PostAsync($"/api/store/orders/{order.Number}/receipts?token={order.AccessToken}", bad))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var detail = await store.GetFromJsonAsync<OrderDetailDto>($"/api/store/orders/{order.Number}?token={order.AccessToken}", Json);
        detail!.Receipts.Should().ContainSingle(r => r.Reference == "REF-777");
    }

    [Fact]
    public async Task Super_admin_creates_tenant_with_primary_color_only_on_admin_host()
    {
        var admin = _api.AdminHostClient();
        var login = await admin.PostAsJsonAsync("/api/auth/token",
            new LoginRequest(DataSeeder.SuperAdminEmail, DataSeeder.DemoPassword));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var slug = $"api-{Guid.NewGuid():N}"[..14];
        var request = new CreateTenantRequest
        {
            Name = "Tienda API", Slug = slug, PrimaryColor = "#6A1B9A", AdminFullName = "Dueño",
            AdminEmail = $"admin@{slug}.test", AdminPassword = "ClaveSegura1"
        };
        var created = await admin.PostAsJsonAsync("/api/superadmin/tenants", request, Json);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<TenantSummaryDto>(Json))!.PrimaryColor.Should().Be("#6A1B9A");

        var noColor = await admin.PostAsJsonAsync("/api/superadmin/tenants",
            new CreateTenantRequest { Name = "Sin color", Slug = "sin-color-x", AdminFullName = "x", AdminEmail = "x@x.test", AdminPassword = "ClaveSegura1" }, Json);
        noColor.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // En un subdominio de tienda los endpoints de Super Admin no existen.
        var tenantHost = _api.ClientFor(slug);
        tenantHost.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await tenantHost.GetAsync("/api/superadmin/tenants")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // El nuevo tenant ya sirve su catálogo (vacío) por su subdominio.
        (await _api.ClientFor(slug).GetAsync("/api/store/catalog")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
