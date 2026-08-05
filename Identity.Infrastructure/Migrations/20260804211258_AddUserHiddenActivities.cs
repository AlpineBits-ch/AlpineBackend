using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHiddenActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_hidden_activities",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_hidden_activities", x => x.id);
                    table.CheckConstraint("ck_user_hidden_activities_exactly_one_key", "(application_id IS NULL) <> (name IS NULL)");
                    table.ForeignKey(
                        name: "fk_user_hidden_activities_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_hidden_activities_user_id",
                table: "user_hidden_activities",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_hidden_activities_user_id_application_id",
                table: "user_hidden_activities",
                columns: new[] { "user_id", "application_id" },
                unique: true,
                filter: "application_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_hidden_activities_user_id_name",
                table: "user_hidden_activities",
                columns: new[] { "user_id", "name" },
                unique: true,
                filter: "name IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_hidden_activities");
        }
    }
}
