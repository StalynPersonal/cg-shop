using CgShop.Domain.Catalog;

namespace CgShop.Application.Catalog;

public sealed record CatalogQuery(
    ProductCategory? Category = null,
    string? Search = null,
    string? Size = null,
    string? Color = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    bool InStockOnly = false,
    CatalogSort Sort = CatalogSort.Newest,
    int Page = 1,
    int PageSize = 24);

public enum CatalogSort
{
    Newest,
    PriceAsc,
    PriceDesc,
    Name
}

public sealed record ProductCardDto(
    Guid Id,
    string Slug,
    string Name,
    string? Brand,
    ProductCategory Category,
    string? ImageUrl,
    decimal MinPrice,
    int Available);

public sealed record VariantDto(
    Guid Id,
    string Sku,
    string? Size,
    string? Color,
    int? VolumeMl,
    decimal Price,
    int Available,
    string Description);

public sealed record CartVariantDto(
    VariantDto Variant,
    Guid ProductId,
    string ProductName,
    string ProductSlug,
    string? ImageUrl,
    bool ProductActive);

public sealed record ProductDetailDto(
    Guid Id,
    string Slug,
    string Name,
    string? Brand,
    string? Description,
    ProductCategory Category,
    string? ImageUrl,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<VariantDto> Variants);

public sealed record CatalogFacetsDto(
    IReadOnlyList<ProductCategory> Categories,
    IReadOnlyList<string> Sizes,
    IReadOnlyList<string> Colors);

public sealed record ProductAdminRowDto(
    Guid Id,
    string Name,
    string? Brand,
    ProductCategory Category,
    bool IsActive,
    int VariantCount,
    int StockOnHand,
    int StockReserved,
    decimal? MinPrice);

public sealed class ProductUpsertDto
{
    public string Name { get; set; } = "";
    public ProductCategory Category { get; set; } = ProductCategory.Clothing;
    public string? Brand { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = [];
}

public sealed class VariantUpsertDto
{
    public string Sku { get; set; } = "";
    public string? Size { get; set; }
    public string? Color { get; set; }
    public int? VolumeMl { get; set; }
    public decimal Price { get; set; }
    public int InitialStock { get; set; }
}

public sealed record StockMovementQuery(
    StockMovementType? Type = null,
    Guid? VariantId = null,
    string? Search = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 50);

public sealed record StockMovementDto(
    Guid Id,
    DateTime CreatedAtUtc,
    Guid VariantId,
    string Sku,
    string ProductName,
    StockMovementType Type,
    int Quantity,
    int StockBefore,
    int StockAfter,
    string? Reason,
    string? OrderNumber,
    string UserName,
    string? UserRole);

public sealed record InventoryRowDto(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    ProductCategory Category,
    string Sku,
    string Description,
    decimal Price,
    int StockOnHand,
    int StockReserved,
    int Available);
