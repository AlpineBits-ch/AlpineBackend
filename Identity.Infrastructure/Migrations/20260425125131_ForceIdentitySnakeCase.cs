using System;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ForceIdentitySnakeCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_keys_users_user_id",
                table: "user_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys");

            migrationBuilder.DropColumn(
                name: "age_verification_ai_estimation_completed_at",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "age_verification_birth_date",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "age_verification_goverment_id_completed_at",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "age_verification_level",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "age_verification_self_declaration_completed_at",
                table: "AspNetUsers");

            migrationBuilder.RenameTable(
                name: "AspNetUserTokens",
                newName: "asp_net_user_tokens");

            migrationBuilder.RenameTable(
                name: "AspNetUsers",
                newName: "asp_net_users");

            migrationBuilder.RenameTable(
                name: "AspNetUserRoles",
                newName: "asp_net_user_roles");

            migrationBuilder.RenameTable(
                name: "AspNetUserLogins",
                newName: "asp_net_user_logins");

            migrationBuilder.RenameTable(
                name: "AspNetUserClaims",
                newName: "asp_net_user_claims");

            migrationBuilder.RenameTable(
                name: "AspNetRoles",
                newName: "asp_net_roles");

            migrationBuilder.RenameTable(
                name: "AspNetRoleClaims",
                newName: "asp_net_role_claims");

            migrationBuilder.CreateTable(
                name: "age_verification",
                columns: table => new
                {
                    application_user_id = table.Column<string>(type: "text", nullable: false),
                    self_declaration_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ai_estimation_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    goverment_id_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: false),
                    level = table.Column<AgeVertificationLevel>(type: "age_vertification_level", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_age_verification", x => x.application_user_id);
                    table.ForeignKey(
                        name: "fk_age_verification_asp_net_users_application_user_id",
                        column: x => x.application_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddForeignKey(
                name: "fk_user_keys_asp_net_users_user_id",
                table: "user_keys",
                column: "user_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_public_keys_asp_net_users_user_id",
                table: "user_public_keys",
                column: "user_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_keys_asp_net_users_user_id",
                table: "user_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_user_public_keys_asp_net_users_user_id",
                table: "user_public_keys");

            migrationBuilder.DropTable(
                name: "age_verification");

            migrationBuilder.RenameTable(
                name: "asp_net_users",
                newName: "AspNetUsers");

            migrationBuilder.RenameTable(
                name: "asp_net_user_tokens",
                newName: "AspNetUserTokens");

            migrationBuilder.RenameTable(
                name: "asp_net_user_roles",
                newName: "AspNetUserRoles");

            migrationBuilder.RenameTable(
                name: "asp_net_user_logins",
                newName: "AspNetUserLogins");

            migrationBuilder.RenameTable(
                name: "asp_net_user_claims",
                newName: "AspNetUserClaims");

            migrationBuilder.RenameTable(
                name: "asp_net_roles",
                newName: "AspNetRoles");

            migrationBuilder.RenameTable(
                name: "asp_net_role_claims",
                newName: "AspNetRoleClaims");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "age_verification_ai_estimation_completed_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "age_verification_birth_date",
                table: "AspNetUsers",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "age_verification_goverment_id_completed_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<AgeVertificationLevel>(
                name: "age_verification_level",
                table: "AspNetUsers",
                type: "age_vertification_level",
                nullable: false,
                defaultValue: AgeVertificationLevel.None);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "age_verification_self_declaration_completed_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_user_keys_users_user_id",
                table: "user_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
