using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NamingConventions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserPublicKeys_Users_UserId",
                table: "UserPublicKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_UserPreferences_UserPreferencesId",
                table: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Users",
                table: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserPublicKeys",
                table: "UserPublicKeys");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserPreferences",
                table: "UserPreferences");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "users");

            migrationBuilder.RenameTable(
                name: "UserPublicKeys",
                newName: "user_public_keys");

            migrationBuilder.RenameTable(
                name: "UserPreferences",
                newName: "user_preferences");

            migrationBuilder.RenameColumn(
                name: "Username",
                table: "users",
                newName: "username");

            migrationBuilder.RenameColumn(
                name: "Hash",
                table: "users",
                newName: "hash");

            migrationBuilder.RenameColumn(
                name: "Bio",
                table: "users",
                newName: "bio");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "users",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserPreferencesId",
                table: "users",
                newName: "user_preferences_id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "users",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "PhoneVerifiedAt",
                table: "users",
                newName: "phone_verified_at");

            migrationBuilder.RenameColumn(
                name: "PhoneNumber",
                table: "users",
                newName: "phone_number");

            migrationBuilder.RenameColumn(
                name: "EmailVerifiedAt",
                table: "users",
                newName: "email_verified_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "users",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                table: "users",
                newName: "correlation_id");

            migrationBuilder.RenameColumn(
                name: "BirthDate",
                table: "users",
                newName: "birth_date");

            migrationBuilder.RenameColumn(
                name: "AgeVerification_SelfDeclarationCompletedAt",
                table: "users",
                newName: "age_verification_self_declaration_completed_at");

            migrationBuilder.RenameColumn(
                name: "AgeVerification_Level",
                table: "users",
                newName: "age_verification_level");

            migrationBuilder.RenameColumn(
                name: "AgeVerification_GovermentIdCompletedAt",
                table: "users",
                newName: "age_verification_goverment_id_completed_at");

            migrationBuilder.RenameColumn(
                name: "AgeVerification_BirthDate",
                table: "users",
                newName: "age_verification_birth_date");

            migrationBuilder.RenameColumn(
                name: "AgeVerification_AiEstimationCompletedAt",
                table: "users",
                newName: "age_verification_ai_estimation_completed_at");

            migrationBuilder.RenameIndex(
                name: "IX_Users_UserPreferencesId",
                table: "users",
                newName: "ix_users_user_preferences_id");

            migrationBuilder.RenameIndex(
                name: "IX_Users_PhoneNumber",
                table: "users",
                newName: "ix_users_phone_number");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "user_public_keys",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "user_public_keys",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "user_public_keys",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "PublicKey",
                table: "user_public_keys",
                newName: "public_key");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "user_public_keys",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_UserPublicKeys_UserId",
                table: "user_public_keys",
                newName: "ix_user_public_keys_user_id");

            migrationBuilder.RenameColumn(
                name: "Theme",
                table: "user_preferences",
                newName: "theme");

            migrationBuilder.RenameColumn(
                name: "Data",
                table: "user_preferences",
                newName: "data");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "user_preferences",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "user_preferences",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "PrivacySettings",
                table: "user_preferences",
                newName: "privacy_settings");

            migrationBuilder.RenameColumn(
                name: "DirectMessageSettings",
                table: "user_preferences",
                newName: "direct_message_settings");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "user_preferences",
                newName: "created_at");

            migrationBuilder.AddPrimaryKey(
                name: "pk_users",
                table: "users",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_public_keys",
                table: "user_public_keys",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_preferences",
                table: "user_preferences",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_users_user_preferences_user_preferences_id",
                table: "users",
                column: "user_preferences_id",
                principalTable: "user_preferences",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_users_user_preferences_user_preferences_id",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_users",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_public_keys",
                table: "user_public_keys");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_preferences",
                table: "user_preferences");

            migrationBuilder.RenameTable(
                name: "users",
                newName: "Users");

            migrationBuilder.RenameTable(
                name: "user_public_keys",
                newName: "UserPublicKeys");

            migrationBuilder.RenameTable(
                name: "user_preferences",
                newName: "UserPreferences");

            migrationBuilder.RenameColumn(
                name: "username",
                table: "Users",
                newName: "Username");

            migrationBuilder.RenameColumn(
                name: "hash",
                table: "Users",
                newName: "Hash");

            migrationBuilder.RenameColumn(
                name: "bio",
                table: "Users",
                newName: "Bio");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Users",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_preferences_id",
                table: "Users",
                newName: "UserPreferencesId");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "Users",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "phone_verified_at",
                table: "Users",
                newName: "PhoneVerifiedAt");

            migrationBuilder.RenameColumn(
                name: "phone_number",
                table: "Users",
                newName: "PhoneNumber");

            migrationBuilder.RenameColumn(
                name: "email_verified_at",
                table: "Users",
                newName: "EmailVerifiedAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "Users",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                table: "Users",
                newName: "CorrelationId");

            migrationBuilder.RenameColumn(
                name: "birth_date",
                table: "Users",
                newName: "BirthDate");

            migrationBuilder.RenameColumn(
                name: "age_verification_self_declaration_completed_at",
                table: "Users",
                newName: "AgeVerification_SelfDeclarationCompletedAt");

            migrationBuilder.RenameColumn(
                name: "age_verification_level",
                table: "Users",
                newName: "AgeVerification_Level");

            migrationBuilder.RenameColumn(
                name: "age_verification_goverment_id_completed_at",
                table: "Users",
                newName: "AgeVerification_GovermentIdCompletedAt");

            migrationBuilder.RenameColumn(
                name: "age_verification_birth_date",
                table: "Users",
                newName: "AgeVerification_BirthDate");

            migrationBuilder.RenameColumn(
                name: "age_verification_ai_estimation_completed_at",
                table: "Users",
                newName: "AgeVerification_AiEstimationCompletedAt");

            migrationBuilder.RenameIndex(
                name: "ix_users_user_preferences_id",
                table: "Users",
                newName: "IX_Users_UserPreferencesId");

            migrationBuilder.RenameIndex(
                name: "ix_users_phone_number",
                table: "Users",
                newName: "IX_Users_PhoneNumber");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "UserPublicKeys",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "UserPublicKeys",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "UserPublicKeys",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "public_key",
                table: "UserPublicKeys",
                newName: "PublicKey");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "UserPublicKeys",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_user_public_keys_user_id",
                table: "UserPublicKeys",
                newName: "IX_UserPublicKeys_UserId");

            migrationBuilder.RenameColumn(
                name: "theme",
                table: "UserPreferences",
                newName: "Theme");

            migrationBuilder.RenameColumn(
                name: "data",
                table: "UserPreferences",
                newName: "Data");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "UserPreferences",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "UserPreferences",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "privacy_settings",
                table: "UserPreferences",
                newName: "PrivacySettings");

            migrationBuilder.RenameColumn(
                name: "direct_message_settings",
                table: "UserPreferences",
                newName: "DirectMessageSettings");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "UserPreferences",
                newName: "CreatedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Users",
                table: "Users",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserPublicKeys",
                table: "UserPublicKeys",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserPreferences",
                table: "UserPreferences",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UserPublicKeys_Users_UserId",
                table: "UserPublicKeys",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_UserPreferences_UserPreferencesId",
                table: "Users",
                column: "UserPreferencesId",
                principalTable: "UserPreferences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
