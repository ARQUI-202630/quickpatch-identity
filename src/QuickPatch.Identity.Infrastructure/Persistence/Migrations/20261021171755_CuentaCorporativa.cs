using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CuentaCorporativa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    nit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    contact_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_companies", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_companies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_company",
                table: "users",
                column: "company_id",
                filter: "company_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_company_role",
                table: "users",
                sql: "(role = 'empresa_contacto') = (company_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_client_companies_tenant_nit",
                table: "client_companies",
                columns: new[] { "tenant_id", "nit" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_users_client_companies",
                table: "users",
                column: "company_id",
                principalTable: "client_companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Aislamiento por tenant con RLS (DD, sección 10.2), igual que users.
            migrationBuilder.Sql(TenantIsolationSql);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE client_companies ENABLE ROW LEVEL SECURITY;
            ALTER TABLE client_companies FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON client_companies USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_client_companies",
                table: "users");

            migrationBuilder.DropTable(
                name: "client_companies");

            migrationBuilder.DropIndex(
                name: "ix_users_company",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_company_role",
                table: "users");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "users");
        }
    }
}
