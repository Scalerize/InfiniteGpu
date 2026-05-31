using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InfiniteGPU.Backend.Migrations
{
    /// <inheritdoc />
    public partial class ChangeDeviceIdentifierUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_DeviceIdentifier",
                table: "Devices");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceIdentifier_ProviderUserId",
                table: "Devices",
                columns: new[] { "DeviceIdentifier", "ProviderUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_DeviceIdentifier_ProviderUserId",
                table: "Devices");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceIdentifier",
                table: "Devices",
                column: "DeviceIdentifier",
                unique: true);
        }
    }
}
