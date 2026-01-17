using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InfiniteGPU.Backend.Migrations
{
    /// <inheritdoc />
    public partial class UrlCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderModelCaches");

            migrationBuilder.CreateTable(
                name: "DeviceModelCaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CachedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LastAccessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceModelCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceModelCaches_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceModelCaches_DeviceId_ModelUrl",
                table: "DeviceModelCaches",
                columns: new[] { "DeviceId", "ModelUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceModelCaches");

            migrationBuilder.CreateTable(
                name: "ProviderModelCaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AccessCount = table.Column<int>(type: "int", nullable: false),
                    LastAccessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StoredFileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderModelCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderModelCaches_AspNetUsers_ProviderUserId",
                        column: x => x.ProviderUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderModelCaches_ProviderUserId_StoredFileName",
                table: "ProviderModelCaches",
                columns: new[] { "ProviderUserId", "StoredFileName" },
                unique: true);
        }
    }
}
