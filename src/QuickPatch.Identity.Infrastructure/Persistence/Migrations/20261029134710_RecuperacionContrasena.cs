using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecuperacionContrasena : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "password_setup_tokens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "invitacion");

            migrationBuilder.CreateTable(
                name: "password_recovery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_recovery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_recovery_attempts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "password_recovery_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_recovery_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_recovery_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_password_recovery_requests_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_password_recovery_attempts_tenant_id",
                table: "password_recovery_attempts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_password_recovery_attempts_email",
                table: "password_recovery_attempts",
                columns: new[] { "email_hash", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_password_recovery_requests_tenant_id",
                table: "password_recovery_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_password_recovery_requests_status",
                table: "password_recovery_requests",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_password_recovery_requests_pending",
                table: "password_recovery_requests",
                column: "user_id",
                unique: true,
                filter: "status = 'pendiente'");

            // Valores permitidos y aislamiento por tenant con RLS (DD, sección 10.2), igual que password_setup_tokens.
            migrationBuilder.Sql(ConstraintsAndTenantIsolationSql);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string ConstraintsAndTenantIsolationSql = $"""
            ALTER TABLE password_setup_tokens ADD CONSTRAINT ck_password_setup_tokens_purpose CHECK (purpose IN ('invitacion', 'recuperacion'));
            ALTER TABLE password_recovery_requests ADD CONSTRAINT ck_password_recovery_requests_status CHECK (status IN ('pendiente', 'cerrada'));
            ALTER TABLE password_recovery_requests ENABLE ROW LEVEL SECURITY;
            ALTER TABLE password_recovery_requests FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON password_recovery_requests USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});
            ALTER TABLE password_recovery_attempts ENABLE ROW LEVEL SECURITY;
            ALTER TABLE password_recovery_attempts FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON password_recovery_attempts USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_recovery_attempts");

            migrationBuilder.DropTable(
                name: "password_recovery_requests");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "password_setup_tokens");
        }
    }
}
