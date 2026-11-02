using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Permite al rol de plataforma encolar <c>tenant.status-changed</c> en <c>outbox_events</c> al activar o desactivar un
    /// tenant (SCRUM-509). Sin el permiso el cambio de estado fallaría en el despliegue hasta volver a aplicar
    /// <c>db/roles.sql</c>; el dueño de la tabla (el migrador) lo concede si el rol ya existe. No cambia el modelo.
    /// </summary>
    [DbContext(typeof(IdentityDbContext))]
    [Migration("20261102140000_PermisoOutboxPlataforma")]
    public partial class PermisoOutboxPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_platform') THEN
                        GRANT INSERT ON outbox_events TO identity_platform;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_platform') THEN
                        REVOKE INSERT ON outbox_events FROM identity_platform;
                    END IF;
                END
                $$;
                """);
        }
    }
}
