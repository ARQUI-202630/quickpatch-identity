using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    nit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    failed_login_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_status",
                table: "tenants",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_tenants_nit",
                table: "tenants",
                column: "nit",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_role",
                table: "users",
                columns: new[] { "tenant_id", "role" });

            migrationBuilder.CreateIndex(
                name: "ux_users_tenant_document",
                table: "users",
                columns: new[] { "tenant_id", "document_id" },
                unique: true,
                filter: "document_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_users_tenant_email",
                table: "users",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            // Aislamiento por tenant con RLS (DD, sección 10.2). FORCE aplica la política también al dueño de las tablas.
            // Sin el tenant fijado en la transacción, current_setting devuelve NULL y no se ve ni se escribe nada.
            migrationBuilder.Sql(TenantIsolationSql);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE users ENABLE ROW LEVEL SECURITY;
            ALTER TABLE users FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON users USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});

            -- tenants: cada sesión solo ve su propio tenant. El rol del servicio solo tiene SELECT (DD 10.2);
            -- crear, activar o desactivar tenants es una operación de plataforma (identity_platform, DD 10.4).
            ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON tenants USING (id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
