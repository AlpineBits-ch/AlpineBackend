using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Social.Domain.Enums;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    bio = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    hash = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relationships",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<RelationshipStatus>(type: "relationship_status", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relationships", x => x.id);
                    table.ForeignKey(
                        name: "fk_relationships_profiles_owner_id",
                        column: x => x.owner_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_relationships_profiles_target_id",
                        column: x => x.target_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_relationships_owner_id",
                table: "relationships",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_target_id",
                table: "relationships",
                column: "target_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relationships");

            migrationBuilder.DropTable(
                name: "profiles");
        }
    }
}
