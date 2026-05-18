using System;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMLSData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "encrypted_master_key",
                table: "asp_net_users");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system");

            migrationBuilder.DropColumn(
                name: "privacy_settings",
                table: "user_preferences");

            migrationBuilder.AddColumn<PrivacySettings>(
                name: "privacy_settings",
                table: "user_preferences",
                type: "privacy_settings",
                nullable: false,
                defaultValue: PrivacySettings.None);

            migrationBuilder.CreateTable(
                name: "encrypted_master_key",
                columns: table => new
                {
                    application_user_id = table.Column<string>(type: "text", nullable: false),
                    cipher_text = table.Column<byte[]>(type: "bytea", nullable: false),
                    salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    iv = table.Column<byte[]>(type: "bytea", nullable: false),
                    argon2iterations = table.Column<int>(type: "integer", nullable: false),
                    argon2memory = table.Column<int>(type: "integer", nullable: false),
                    argon2parallelism = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encrypted_master_key", x => x.application_user_id);
                    table.ForeignKey(
                        name: "fk_encrypted_master_key_asp_net_users_application_user_id",
                        column: x => x.application_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_devices",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    device_name = table.Column<string>(type: "text", nullable: false),
                    device_type = table.Column<DeviceType>(type: "device_type", nullable: false),
                    identity_public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<DeviceStatus>(type: "device_status", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_key_packages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    key_package = table.Column<byte[]>(type: "bytea", nullable: false),
                    cipher_suite = table.Column<int>(type: "integer", nullable: false),
                    consumed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_key_packages", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_key_packages_user_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "user_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_key_packages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_devices_user_id",
                table: "user_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_key_packages_device_id",
                table: "user_key_packages",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_key_packages_user_id",
                table: "user_key_packages",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "encrypted_master_key");

            migrationBuilder.DropTable(
                name: "user_key_packages");

            migrationBuilder.DropTable(
                name: "user_devices");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system");

            migrationBuilder.AlterColumn<decimal>(
                name: "privacy_settings",
                table: "user_preferences",
                type: "numeric(20,0)",
                nullable: false,
                oldClrType: typeof(PrivacySettings),
                oldType: "privacy_settings");

            migrationBuilder.AddColumn<byte[]>(
                name: "encrypted_master_key",
                table: "asp_net_users",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
