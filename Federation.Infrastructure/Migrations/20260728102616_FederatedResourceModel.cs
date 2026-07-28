using System;
using Federation.Domain.Events;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Federation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FederatedResourceModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "federated_guild");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .Annotation("Npgsql:Enum:federated_resource_type", "conversation,friendship,guild")
                .Annotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended")
                .OldAnnotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .OldAnnotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended");

            migrationBuilder.CreateTable(
                name: "federated_resources",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    resource_type = table.Column<FederatedResourceType>(type: "federated_resource_type", nullable: false),
                    local_id = table.Column<string>(type: "text", nullable: false),
                    remote_id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_federated_resources", x => x.id);
                    table.ForeignKey(
                        name: "fk_federated_resources_federation_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "federation_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_federation_instances_host",
                table: "federation_instances",
                column: "host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_federated_resources_instance_id_resource_type_remote_id",
                table: "federated_resources",
                columns: new[] { "instance_id", "resource_type", "remote_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_federated_resources_resource_type_local_id",
                table: "federated_resources",
                columns: new[] { "resource_type", "local_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "federated_resources");

            migrationBuilder.DropIndex(
                name: "ix_federation_instances_host",
                table: "federation_instances");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .Annotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended")
                .OldAnnotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .OldAnnotation("Npgsql:Enum:federated_resource_type", "conversation,friendship,guild")
                .OldAnnotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended");

            migrationBuilder.CreateTable(
                name: "federated_guild",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    federated_guild_id = table.Column<string>(type: "text", nullable: false),
                    remote_id = table.Column<string>(type: "text", nullable: false),
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
    }
}
