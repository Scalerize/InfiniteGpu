using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InfiniteGPU.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddParentPeerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add IsParentPeer column - indicates if this partition is the parent peer
            // that downloads the full model and distributes subgraphs
            migrationBuilder.AddColumn<bool>(
                name: "IsParentPeer",
                table: "Partitions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Add SubgraphReceiveStatus column - tracks status of receiving subgraph from parent
            migrationBuilder.AddColumn<int>(
                name: "SubgraphReceiveStatus",
                table: "Partitions",
                type: "int",
                nullable: false,
                defaultValue: 0); // 0 = NotApplicable

            // Add OnnxFullModelBlobUri column - URI to full model (only for parent peer)
            migrationBuilder.AddColumn<string>(
                name: "OnnxFullModelBlobUri",
                table: "Partitions",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            // Add OnnxSubgraphSizeBytes column - size of the subgraph in bytes
            migrationBuilder.AddColumn<long>(
                name: "OnnxSubgraphSizeBytes",
                table: "Partitions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Create index on IsParentPeer for efficient parent peer lookups
            migrationBuilder.CreateIndex(
                name: "IX_Partitions_IsParentPeer",
                table: "Partitions",
                column: "IsParentPeer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Partitions_IsParentPeer",
                table: "Partitions");

            migrationBuilder.DropColumn(
                name: "OnnxSubgraphSizeBytes",
                table: "Partitions");

            migrationBuilder.DropColumn(
                name: "OnnxFullModelBlobUri",
                table: "Partitions");

            migrationBuilder.DropColumn(
                name: "SubgraphReceiveStatus",
                table: "Partitions");

            migrationBuilder.DropColumn(
                name: "IsParentPeer",
                table: "Partitions");
        }
    }
}
