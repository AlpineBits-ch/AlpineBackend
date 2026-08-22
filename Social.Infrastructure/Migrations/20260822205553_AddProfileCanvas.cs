using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCanvas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profile_canvas_images",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_canvas_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_profile_canvas_images_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_canvases",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    theme_json = table.Column<string>(type: "jsonb", nullable: false),
                    widgets_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_canvases", x => x.id);
                    table.ForeignKey(
                        name: "fk_profile_canvases_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_profile_canvas_images_profile_id",
                table: "profile_canvas_images",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_canvases_profile_id",
                table: "profile_canvases",
                column: "profile_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_canvas_images");

            migrationBuilder.DropTable(
                name: "profile_canvases");
        }
    }
}
