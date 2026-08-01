using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsIdentityKeysBackupAndProtectionLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The only non-additive step, and it is required: this was the unique index behind the
            // old one-to-one device->backup mapping, which made every backup write an overwrite of
            // the single copy of that device's signing key. It is replaced below by a unique index
            // on (device_id, version). Nothing is dropped that holds data.
            //
            // Raw IF EXISTS rather than DropIndex: dev and test databases in this repo are
            // provisioned from the EF *model* by JasperFx resource setup, not by replaying
            // migrations, so the old index legitimately may never have existed there. A migration
            // that hard-fails on that difference blocks the whole test suite from booting.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_user_device_backups_device_id;");

            // Scaffolded without a default, which would fail outright on a non-empty user_devices
            // table. Existing rows mean "this device has told us nothing", which is exactly the
            // empty set - and is treated as "supports nothing new" everywhere it gates anything.
            migrationBuilder.AddColumn<List<string>>(
                name: "capabilities",
                table: "user_devices",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<byte[]>(
                name: "certificate",
                table: "user_devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "certificate_expires_at",
                table: "user_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "certificate_identity_key_version",
                table: "user_devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "certificate_issued_at",
                table: "user_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "e_tag",
                table: "user_device_backups",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "recovery_key_version",
                table: "user_device_backups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                table: "user_device_backups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "user_device_backups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "kdf",
                table: "encrypted_master_key",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "public_verifier",
                table: "encrypted_master_key",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "account_identity_key_rotation_signature",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "account_identity_key_updated_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "account_identity_key_version",
                table: "asp_net_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "account_identity_public_key",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "master_key_password_wrapping_invalidated_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "protection_level",
                table: "asp_net_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "protection_level_assertion",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "protection_level_updated_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "protection_level_version",
                table: "asp_net_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "recovery_code_wrapped_master_key_argon2iterations",
                table: "asp_net_users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recovery_code_wrapped_master_key_argon2memory",
                table: "asp_net_users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recovery_code_wrapped_master_key_argon2parallelism",
                table: "asp_net_users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "recovery_code_wrapped_master_key_cipher_text",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "recovery_code_wrapped_master_key_iv",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recovery_code_wrapped_master_key_kdf",
                table: "asp_net_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "recovery_code_wrapped_master_key_public_verifier",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "recovery_code_wrapped_master_key_salt",
                table: "asp_net_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recovery_code_wrapped_master_key_version",
                table: "asp_net_users",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "identity_audit_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    client_device_id = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_audit_events_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_backup_transfers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    source_device_id = table.Column<string>(type: "text", nullable: false),
                    target_device_id = table.Column<string>(type: "text", nullable: false),
                    wrapped_to = table.Column<byte[]>(type: "bytea", nullable: false),
                    cipher_text = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_backup_transfers", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_backup_transfers_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_device_backups_device_id_version",
                table: "user_device_backups",
                columns: new[] { "device_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_audit_events_user_id_created_at",
                table: "identity_audit_events",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_backup_transfers_expires_at",
                table: "user_backup_transfers",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_backup_transfers_user_id_target_device_id",
                table: "user_backup_transfers",
                columns: new[] { "user_id", "target_device_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_audit_events");

            migrationBuilder.DropTable(
                name: "user_backup_transfers");

            migrationBuilder.DropIndex(
                name: "ix_user_device_backups_device_id_version",
                table: "user_device_backups");

            migrationBuilder.DropColumn(
                name: "capabilities",
                table: "user_devices");

            migrationBuilder.DropColumn(
                name: "certificate",
                table: "user_devices");

            migrationBuilder.DropColumn(
                name: "certificate_expires_at",
                table: "user_devices");

            migrationBuilder.DropColumn(
                name: "certificate_identity_key_version",
                table: "user_devices");

            migrationBuilder.DropColumn(
                name: "certificate_issued_at",
                table: "user_devices");

            migrationBuilder.DropColumn(
                name: "e_tag",
                table: "user_device_backups");

            migrationBuilder.DropColumn(
                name: "recovery_key_version",
                table: "user_device_backups");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                table: "user_device_backups");

            migrationBuilder.DropColumn(
                name: "version",
                table: "user_device_backups");

            migrationBuilder.DropColumn(
                name: "kdf",
                table: "encrypted_master_key");

            migrationBuilder.DropColumn(
                name: "public_verifier",
                table: "encrypted_master_key");

            migrationBuilder.DropColumn(
                name: "account_identity_key_rotation_signature",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "account_identity_key_updated_at",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "account_identity_key_version",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "account_identity_public_key",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "master_key_password_wrapping_invalidated_at",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "protection_level",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "protection_level_assertion",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "protection_level_updated_at",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "protection_level_version",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_argon2iterations",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_argon2memory",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_argon2parallelism",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_cipher_text",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_iv",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_kdf",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_public_verifier",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_salt",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "recovery_code_wrapped_master_key_version",
                table: "asp_net_users");

            migrationBuilder.CreateIndex(
                name: "ix_user_device_backups_device_id",
                table: "user_device_backups",
                column: "device_id",
                unique: true);
        }
    }
}
