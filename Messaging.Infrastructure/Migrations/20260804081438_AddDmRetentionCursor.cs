using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDmRetentionCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dm_retention_cursors",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    last_user_id = table.Column<string>(type: "text", nullable: false),
                    rotation_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotations_completed = table.Column<long>(type: "bigint", nullable: false),
                    users_seen_this_rotation = table.Column<int>(type: "integer", nullable: false),
                    lag_warning_issued = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dm_retention_cursors", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dm_retention_cursors");
        }
    }
}
