using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CgShop.UnitTests.Application;

/// <summary>Historial de cambios de productos: qué, quién y cuándo (visible solo para el propietario).</summary>
public class ProductHistoryTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private InMemoryDb.Factory _factory = null!;
    private ProductAdminService _service = null!;

    public async Task InitializeAsync()
    {
        _factory = _db.For(await _db.AddTenantAsync());
        _service = new ProductAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(),
            TimeProvider.System, NullLogger<ProductAdminService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Guid> Create(int i, ActorInfo actor) => _service.CreateAsync(
        new ProductUpsertDto
        {
            Name = $"Camisa {i:000}", Category = ProductCategory.Clothing, Brand = "Marca",
            Attributes = new Dictionary<string, string> { ["Material"] = "Algodón" }
        },
        [new VariantUpsertDto { Sku = $"CAM-{i:000}-M", Size = "M", Color = "Azul", Price = 1000, InitialStock = 5 }],
        actor);

    [Fact]
    public async Task Records_who_did_what_for_100_products()
    {
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var staff = i % 2 == 0;
            var actor = staff ? TestData.TenantStaff : TestData.TenantAdmin;
            var id = await Create(i, actor);

            var (dto, variants) = await _service.GetForEditAsync(id);
            dto.Name = $"Camisa {i:000} Slim";
            dto.Attributes["Material"] = "Lino";
            dto.Audience = ProductAudience.Men;
            await _service.UpdateAsync(id, dto, TestData.TenantStaff);
            await _service.UpdateAsync(id, dto, TestData.TenantStaff); // sin cambios: no se registra

            await _service.ChangePriceAsync(variants[0].Id, 1250, TestData.TenantStaff);
            var added = await _service.AddVariantAsync(id,
                new VariantUpsertDto { Sku = $"CAM-{i:000}-L", Size = "L", Color = "Azul", Price = 1250, InitialStock = 2 },
                TestData.TenantStaff);
            await _service.RemoveVariantAsync(id, added, TestData.TenantStaff);
            await _service.SetActiveAsync(id, false, TestData.TenantStaff);
            await _service.SetActiveAsync(id, false, TestData.TenantStaff); // ya estaba inactivo: no se registra

            var history = await _service.GetHistoryAsync(id, TestData.TenantAdmin);
            history.Select(h => h.Type).Should().Equal(
                ProductChangeType.Deactivated, ProductChangeType.VariantRemoved, ProductChangeType.VariantAdded,
                ProductChangeType.PriceChanged, ProductChangeType.Updated, ProductChangeType.Created);

            history[^1].UserName.Should().Be(actor.DisplayName);
            history[^1].UserRole.Should().Be(staff ? Roles.TenantStaff : Roles.TenantAdmin);
            history[^1].Details.Should().Contain("Ropa").And.Contain($"CAM-{i:000}-M");
            history[^2].Details.Should().Contain($"Nombre: 'Camisa {i:000}' → 'Camisa {i:000} Slim'")
                .And.Contain("Material: 'Algodón' → 'Lino'").And.Contain("Para: Unisex → Hombre");
            history[^3].Details.Should().Be($"CAM-{i:000}-M: 1,000.00 → 1,250.00");
            history[2].Details.Should().Contain($"CAM-{i:000}-L");
            history.Skip(1).Take(4).Should().OnlyContain(h => h.UserName == TestData.TenantStaff.DisplayName);
        }
    }

    [Fact]
    public async Task History_survives_deletion_and_only_the_owner_can_read_it()
    {
        var id = await Create(1, TestData.TenantStaff);
        (await _service.DeleteAsync(id, TestData.TenantAdmin)).Should().BeTrue();

        var history = await _service.GetHistoryAsync(id, TestData.TenantAdmin);
        history.Select(h => h.Type).Should().Equal(ProductChangeType.Deleted, ProductChangeType.Created);
        history[0].Details.Should().Contain("1 variante(s) y 5 unidad(es)");

        await _service.Invoking(s => s.GetHistoryAsync(id, TestData.TenantStaff)).Should().ThrowAsync<ForbiddenException>();
    }
}
