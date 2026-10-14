using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvitacionEquipo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "password_setup_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_setup_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_setup_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_password_setup_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_password_setup_tokens_tenant_id",
                table: "password_setup_tokens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_password_setup_tokens_user",
                table: "password_setup_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_password_setup_tokens_hash",
                table: "password_setup_tokens",
                column: "token_hash",
                unique: true);

            // Aislamiento por tenant con RLS (DD, sección 10.2), igual que users.
            migrationBuilder.Sql(TenantIsolationSql);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE password_setup_tokens ENABLE ROW LEVEL SECURITY;
            ALTER TABLE password_setup_tokens FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON password_setup_tokens USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_setup_tokens");
        }
    }
}
