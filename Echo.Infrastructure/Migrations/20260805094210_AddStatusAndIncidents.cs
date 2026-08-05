using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusAndIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "status_components",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    impact_hint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    clusters = table.Column<List<string>>(type: "text[]", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    degraded_rate = table.Column<double>(type: "double precision", nullable: true),
                    outage_rate = table.Column<double>(type: "double precision", nullable: true),
                    minimum_volume = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_components", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "status_incidents",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reference = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    impact = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    template = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    auto_component_id = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    is_retracted = table.Column<bool>(type: "boolean", nullable: false),
                    detection_detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "status_day_rollups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    component_id = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    operational_seconds = table.Column<double>(type: "double precision", nullable: false),
                    degraded_seconds = table.Column<double>(type: "double precision", nullable: false),
                    outage_seconds = table.Column<double>(type: "double precision", nullable: false),
                    maintenance_seconds = table.Column<double>(type: "double precision", nullable: false),
                    incident_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_day_rollups", x => x.id);
                    table.ForeignKey(
                        name: "fk_status_day_rollups_status_components_component_id",
                        column: x => x.component_id,
                        principalTable: "status_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "status_incident_components",
                columns: table => new
                {
                    incident_id = table.Column<string>(type: "text", nullable: false),
                    component_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_incident_components", x => new { x.incident_id, x.component_id });
                    table.ForeignKey(
                        name: "fk_status_incident_components_status_components_component_id",
                        column: x => x.component_id,
                        principalTable: "status_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_status_incident_components_status_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "status_incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "status_incident_updates",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    incident_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    template = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    author_user_id = table.Column<string>(type: "text", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_incident_updates", x => x.id);
                    table.ForeignKey(
                        name: "fk_status_incident_updates_status_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "status_incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_status_components_key",
                table: "status_components",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_status_components_position",
                table: "status_components",
                column: "position");

            migrationBuilder.CreateIndex(
                name: "ix_status_day_rollups_component_id_day",
                table: "status_day_rollups",
                columns: new[] { "component_id", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_status_incident_components_component_id",
                table: "status_incident_components",
                column: "component_id");

            migrationBuilder.CreateIndex(
                name: "ix_status_incident_updates_incident_id_posted_at",
                table: "status_incident_updates",
                columns: new[] { "incident_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_status_incidents_auto_open",
                table: "status_incidents",
                column: "auto_component_id",
                unique: true,
                filter: "resolved_at IS NULL AND origin = 'Automatic' AND is_retracted = false");

            migrationBuilder.CreateIndex(
                name: "ix_status_incidents_is_retracted_resolved_at_started_at",
                table: "status_incidents",
                columns: new[] { "is_retracted", "resolved_at", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_status_incidents_reference",
                table: "status_incidents",
                column: "reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "status_day_rollups");

            migrationBuilder.DropTable(
                name: "status_incident_components");

            migrationBuilder.DropTable(
                name: "status_incident_updates");

            migrationBuilder.DropTable(
                name: "status_components");

            migrationBuilder.DropTable(
                name: "status_incidents");
        }
    }
}
