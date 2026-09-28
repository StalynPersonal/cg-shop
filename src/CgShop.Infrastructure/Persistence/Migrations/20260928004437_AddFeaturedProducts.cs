using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CgShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeaturedProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewArrivalsRank",
                table: "Products",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrendingRank",
                table: "Products",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_NewArrivalsRank",
                table: "Products",
                columns: new[] { "TenantId", "NewArrivalsRank" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_TrendingRank",
                table: "Products",
                columns: new[] { "TenantId", "TrendingRank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_NewArrivalsRank",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_TrendingRank",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NewArrivalsRank",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TrendingRank",
                table: "Products");
        }
    }
}
