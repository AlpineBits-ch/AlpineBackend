using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLSCompliantModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "mls_epoch",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "mls_group_id",
                table: "conversations",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "mls_group_info",
                table: "conversations",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "member_devices",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    conversation_member_id = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    mls_leaf_index = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_devices_members_conversation_member_id",
                        column: x => x.conversation_member_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_conversation_member_id",
                table: "member_devices",
                column: "conversation_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_device_id",
                table: "member_devices",
                column: "device_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_devices");

            migrationBuilder.DropColumn(
                name: "mls_epoch",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "mls_group_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "mls_group_info",
                table: "conversations");
        }
    }
}
