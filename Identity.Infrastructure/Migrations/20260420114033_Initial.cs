using System;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system");

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Theme = table.Column<Theme>(type: "theme", nullable: false),
                    DirectMessageSettings = table.Column<DirectMessageSettings>(type: "direct_message_settings", nullable: false),
                    PrivacySettings = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<int>(type: "integer", nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PhoneVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgeVerification_SelfDeclarationCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgeVerification_AiEstimationCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgeVerification_GovermentIdCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgeVerification_BirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AgeVerification_Level = table.Column<AgeVertificationLevel>(type: "age_vertification_level", nullable: false),
                    UserPreferencesId = table.Column<string>(type: "text", nullable: false),
                    Bio = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_UserPreferences_UserPreferencesId",
                        column: x => x.UserPreferencesId,
                        principalTable: "UserPreferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PhoneNumber",
                table: "Users",
                column: "PhoneNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserPreferencesId",
                table: "Users",
                column: "UserPreferencesId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "UserPreferences");
        }
    }
}
