using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CgShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationHours = table.Column<int>(type: "int", nullable: false),
                    ExpiryCheckMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxReceiptMb = table.Column<int>(type: "int", nullable: false),
                    MaxImageMb = table.Column<int>(type: "int", nullable: false),
                    MaxImagesPerProduct = table.Column<int>(type: "int", nullable: false),
                    MinImageDimension = table.Column<int>(type: "int", nullable: false),
                    MaxImageDimension = table.Column<int>(type: "int", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    SessionWarningSeconds = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSettings", x => x.Id);
                    table.CheckConstraint("CK_PlatformSettings_Singleton", "[Id] = '00000000-0000-0000-0000-00000000c0f1'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformSettings");
        }
    }
}
