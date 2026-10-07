using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegistroTecnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "technician_profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verification_status = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    verification_reason = table.Column<string>(type: "text", nullable: true),
                    average_rating = table.Column<decimal>(type: "numeric(2,1)", precision: 2, scale: 1, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_profiles", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_technician_profiles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_technician_profiles_tenant",
                table: "technician_profiles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_profiles_tenant_status",
                table: "technician_profiles",
                columns: new[] { "tenant_id", "verification_status" });

            // Aislamiento por tenant con RLS (DD, sección 10.2), igual que users.
            migrationBuilder.Sql(TenantIsolationSql);
        }

        /// <inheritdoc />

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE technician_profiles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE technician_profiles FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON technician_profiles USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "technician_profiles");
        }
    }
}
