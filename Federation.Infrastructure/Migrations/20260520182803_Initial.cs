using System;
using Federation.Domain.Events;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Federation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:federation_status", "active,blocked,defederated,suspended");

            migrationBuilder.CreateTable(
                name: "federation_instances",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<FederationStatus>(type: "federation_status", nullable: false),
                    defederation_reason = table.Column<string>(type: "text", nullable: true),
                    last_seen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_federation_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "federated_guild",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    remote_id = table.Column<string>(type: "text", nullable: false),
                    federated_guild_id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_federated_guild", x => x.id);
                    table.ForeignKey(
                        name: "fk_federated_guild_federation_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "federation_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_federated_guild_instance_id",
                table: "federated_guild",
                column: "instance_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "federated_guild");

            migrationBuilder.DropTable(
                name: "federation_instances");
        }
    }
}
