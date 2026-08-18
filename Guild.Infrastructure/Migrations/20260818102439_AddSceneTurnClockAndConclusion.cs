using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneTurnClockAndConclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conclusion_note",
                table: "scene_states",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "post_count",
                table: "scene_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "turn_number",
                table: "scene_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "turn_started_at",
                table: "scene_states",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conclusion_note",
                table: "scene_states");

            migrationBuilder.DropColumn(
                name: "post_count",
                table: "scene_states");

            migrationBuilder.DropColumn(
                name: "turn_number",
                table: "scene_states");

            migrationBuilder.DropColumn(
                name: "turn_started_at",
                table: "scene_states");
        }
    }
}
