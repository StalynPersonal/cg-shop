using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CgShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                table: "Products",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Unisex");

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_Audience_IsActive",
                table: "Products",
                columns: new[] { "TenantId", "Audience", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_Audience_IsActive",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "Products");
        }
    }
}
