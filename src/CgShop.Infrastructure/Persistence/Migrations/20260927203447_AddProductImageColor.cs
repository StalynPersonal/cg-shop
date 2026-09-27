using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CgShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductImageColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "ProductImages",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "ProductImages");
        }
    }
}
