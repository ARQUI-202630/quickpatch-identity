using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipoProveedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_technician_profiles_provider",
                table: "technician_profiles",
                column: "provider_id",
                filter: "provider_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_technician_profiles_provider",
                table: "technician_profiles",
                column: "provider_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_technician_profiles_provider",
                table: "technician_profiles");

            migrationBuilder.DropIndex(
                name: "ix_technician_profiles_provider",
                table: "technician_profiles");
        }
    }
}
