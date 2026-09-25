using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;

namespace CgShop.Tests.Shared;

/// <summary>
/// Generador determinista de datos de prueba. Por convención cada funcionalidad
/// se prueba con lotes de <see cref="BatchSize"/> (100) registros.
/// </summary>
public static class TestData
{
    public const int BatchSize = 100;

    public static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    public static readonly ActorInfo TenantAdmin = new("admin-1", "Dueño Tienda", [Roles.TenantAdmin]);
    public static readonly ActorInfo TenantStaff = new("staff-1", "Empleado", [Roles.TenantStaff]);
    public static readonly ActorInfo Customer = new("cust-1", "Cliente", [Roles.Customer]);
    public static readonly ActorInfo SuperAdmin = new("super-1", "Super Admin", [Roles.SuperAdmin]);

    private static readonly string[] Colors = ["Negro", "Blanco", "Rojo", "Azul", "Verde", "Gris", "Beige"];
    private static readonly string[] ClothingSizes = ["XS", "S", "M", "L", "XL", "XXL"];
    private static readonly string[] ShoeSizes = ["36", "37", "38", "39", "40", "41", "42", "43", "44"];
    private static readonly string[] CapSizes = ["S/M", "L/XL", "Ajustable"];
    private static readonly int[] Volumes = [30, 50, 75, 100, 125];
    private static readonly string[] Brands = ["Nike", "Adidas", "Puma", "Casio", "Seiko", "Dior", "Chanel", "New Era", "Zara", "Levi's"];

    public static string HexColorFor(int i) => $"#{(i * 2654435761u) & 0xFFFFFF:X6}";

    public static IEnumerable<Tenant> Tenants(int count = BatchSize) =>
        Enumerable.Range(1, count).Select(i =>
            Tenant.Create($"Empresa {i:000}", $"tienda-{i:000}", HexColorFor(i), $"contacto{i}@tienda{i}.com", Now));

    public static ProductCategory CategoryFor(int i) => (ProductCategory)(i % 5 + 1);

    /// <summary>Producto válido de cualquier categoría con 1-3 variantes y stock suficiente.</summary>
    public static Product Product(int i, Guid tenantId = default, int stockPerVariant = 50)
    {
        var category = CategoryFor(i);
        var attributes = category switch
        {
            ProductCategory.Perfumes => new Dictionary<string, string>
            {
                ["NotasSalida"] = "Bergamota, Limón", ["NotasCorazon"] = "Jazmín", ["NotasFondo"] = "Ámbar, Almizcle"
            },
            ProductCategory.Watches => new Dictionary<string, string>
            {
                ["Movimiento"] = i % 2 == 0 ? "Automático" : "Cuarzo", ["Resistencia"] = "50m"
            },
            ProductCategory.Clothing => new Dictionary<string, string> { ["Material"] = "Algodón 100%" },
            _ => new Dictionary<string, string>()
        };

        var product = Domain.Catalog.Product.Create($"{category.DisplayName()} Modelo {i:000}", category,
            Brands[i % Brands.Length], $"Descripción del producto {i}", null, attributes);
        product.TenantId = tenantId;

        var variants = 1 + i % 3;
        for (var v = 0; v < variants; v++)
        {
            var sku = $"SKU-{i:000}-{v}";
            var price = 500m + i * 25m + v * 100m;
            var color = Colors[(i + v) % Colors.Length];
            var variant = category switch
            {
                ProductCategory.Clothing => product.AddVariant(sku, price, stockPerVariant, ClothingSizes[(i + v) % ClothingSizes.Length], color),
                ProductCategory.Footwear => product.AddVariant(sku, price, stockPerVariant, ShoeSizes[(i + v) % ShoeSizes.Length], color),
                ProductCategory.Caps => product.AddVariant(sku, price, stockPerVariant, CapSizes[(i + v) % CapSizes.Length], color),
                ProductCategory.Perfumes => product.AddVariant(sku, price, stockPerVariant, volumeMl: Volumes[(i + v) % Volumes.Length]),
                _ => product.AddVariant(sku, price, stockPerVariant, color: color)
            };
            variant.TenantId = tenantId;
        }

        return product;
    }

    public static IEnumerable<Product> Products(int count = BatchSize, Guid tenantId = default, int stockPerVariant = 50) =>
        Enumerable.Range(1, count).Select(i => Product(i, tenantId, stockPerVariant));

    public static CustomerInfo CustomerInfo(int i) =>
        new($"Cliente {i:000}", $"cliente{i}@correo.com", $"809-555-{i:0000}", $"Calle {i}, Santo Domingo");

    public static OrderLine LineFor(ProductVariant variant, Product product, int quantity) =>
        new(variant.Id, variant.Sku, product.Name, variant.Description, variant.Price, quantity);

    /// <summary>Orden pendiente de validación con 1-3 líneas.</summary>
    public static Order Order(int i, Guid tenantId = default, TimeSpan? ttl = null)
    {
        var product = Product(i, tenantId);
        var lines = product.Variants.Select((v, idx) => LineFor(v, product, 1 + (i + idx) % 3)).ToList();
        var order = Domain.Orders.Order.Place($"ORD-{i:00000}", CustomerInfo(i), lines,
            i % 2 == 0 ? PaymentMethod.BankTransfer : PaymentMethod.PaymentLink, Tenant.DefaultTaxRate, "DOP", Now,
            ttl ?? TimeSpan.FromHours(48));
        order.TenantId = tenantId;
        return order;
    }

    public static IEnumerable<Order> Orders(int count = BatchSize, Guid tenantId = default) =>
        Enumerable.Range(1, count).Select(i => Order(i, tenantId));
}
