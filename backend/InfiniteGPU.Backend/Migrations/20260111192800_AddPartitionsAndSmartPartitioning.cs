using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InfiniteGPU.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPartitionsAndSmartPartitioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add partitioning columns to Subtasks table
            migrationBuilder.AddColumn<bool>(
                name: "RequiresPartitioning",
                table: "Subtasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // PartitionCount is computed from Partitions.Count() - no database column needed

            // Create Partitions table
            migrationBuilder.CreateTable(
                name: "Partitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubtaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedProviderId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PartitionIndex = table.Column<int>(type: "int", nullable: false),
                    TotalPartitions = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OnnxSubgraphBlobUri = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    InputTensorNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutputTensorNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExecutionConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EstimatedMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedComputeCost = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedTransferBytes = table.Column<long>(type: "bigint", nullable: false),
                    UpstreamConnectionState = table.Column<int>(type: "int", nullable: false),
                    DownstreamConnectionState = table.Column<int>(type: "int", nullable: false),
                    UpstreamPartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DownstreamPartitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConnectionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Progress = table.Column<int>(type: "int", nullable: false),
                    MeasuredBandwidthBps = table.Column<long>(type: "bigint", nullable: true),
                    MeasuredRttMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    MaxRetries = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Partitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Partitions_AspNetUsers_AssignedProviderId",
                        column: x => x.AssignedProviderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Partitions_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Partitions_Partitions_DownstreamPartitionId",
                        column: x => x.DownstreamPartitionId,
                        principalTable: "Partitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Partitions_Partitions_UpstreamPartitionId",
                        column: x => x.UpstreamPartitionId,
                        principalTable: "Partitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Partitions_Subtasks_SubtaskId",
                        column: x => x.SubtaskId,
                        principalTable: "Subtasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_Partitions_AssignedProviderId",
                table: "Partitions",
                column: "AssignedProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_DeviceId",
                table: "Partitions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_DownstreamPartitionId",
                table: "Partitions",
                column: "DownstreamPartitionId",
                unique: true,
                filter: "[DownstreamPartitionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_PartitionIndex",
                table: "Partitions",
                column: "PartitionIndex");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_Status",
                table: "Partitions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_SubtaskId",
                table: "Partitions",
                column: "SubtaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_SubtaskId_PartitionIndex",
                table: "Partitions",
                columns: new[] { "SubtaskId", "PartitionIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Partitions_UpstreamPartitionId",
                table: "Partitions",
                column: "UpstreamPartitionId",
                unique: true,
                filter: "[UpstreamPartitionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Partitions");

            migrationBuilder.DropColumn(
                name: "RequiresPartitioning",
                table: "Subtasks");
        }
    }
}
