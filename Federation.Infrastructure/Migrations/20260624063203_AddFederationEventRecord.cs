using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Federation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFederationEventRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "federated_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<long>(type: "bigint", nullable: false),
                    previous_event_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_federated_events", x => x.event_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "federated_events");
        }
    }
}
