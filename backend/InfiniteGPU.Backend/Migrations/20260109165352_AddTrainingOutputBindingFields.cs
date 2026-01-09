using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InfiniteGPU.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainingOutputBindingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckpointBlobUri",
                table: "Tasks",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvalModelBlobUri",
                table: "Tasks",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptimizerModelBlobUri",
                table: "Tasks",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileUrl",
                table: "TaskOutputBindings",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "TaskOutputBindings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointBlobUri",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EvalModelBlobUri",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "OptimizerModelBlobUri",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "FileUrl",
                table: "TaskOutputBindings");

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "TaskOutputBindings");
        }
    }
}
