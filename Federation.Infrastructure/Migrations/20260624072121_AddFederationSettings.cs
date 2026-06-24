using System;
using Federation.Domain.Aggregates;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Federation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFederationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .Annotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended")
                .OldAnnotation("Npgsql:Enum:federation_status", "active,blocked,defederated,suspended");

            migrationBuilder.CreateTable(
                name: "federation_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    acceptance_policy = table.Column<AcceptancePolicy>(type: "acceptance_policy", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_federation_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "federation_settings");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:federation_status", "active,blocked,defederated,suspended")
                .OldAnnotation("Npgsql:Enum:acceptance_policy", "auto_accept,require_approval")
                .OldAnnotation("Npgsql:Enum:federation_status", "active,blocked,defederated,pending,suspended");
        }
    }
}
