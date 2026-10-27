using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Reputación del técnico proyectada desde Ranking (ADR-027, SCRUM-498; DEP-04 del DD): cantidad de calificaciones,
    /// marca de reputación baja (AC9-E2) y el <c>changedAt</c> del último evento aplicado, para descartar los atrasados
    /// (ADR-017). <c>average_rating</c> ya existía. La tabla conserva su RLS y los permisos de <c>identity_app</c>.
    /// </summary>
    public partial class ReputacionTecnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "low_reputation",
                table: "technician_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "rating_count",
                table: "technician_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reputation_changed_at",
                table: "technician_profiles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "low_reputation",
                table: "technician_profiles");

            migrationBuilder.DropColumn(
                name: "rating_count",
                table: "technician_profiles");

            migrationBuilder.DropColumn(
                name: "reputation_changed_at",
                table: "technician_profiles");
        }
    }
}
