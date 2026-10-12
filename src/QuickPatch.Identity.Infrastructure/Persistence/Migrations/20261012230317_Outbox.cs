using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_pending",
                table: "outbox_events",
                column: "created_at",
                filter: "published_at IS NULL");

            // El servicio solo inserta con el tenant de la sesión; lee y marca como publicado únicamente el rol del
            // publicador (identity_outbox), que tiene BYPASSRLS (db/roles.sql).
            migrationBuilder.Sql(OutboxIsolationSql);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string OutboxIsolationSql = $"""
            ALTER TABLE outbox_events ALTER COLUMN tenant_id SET DEFAULT {CurrentTenant};
            ALTER TABLE outbox_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE outbox_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY outbox_insert ON outbox_events FOR INSERT WITH CHECK (tenant_id = {CurrentTenant});

            -- El registro de técnicos escribe en esta tabla desde este despliegue; sin el permiso fallaría hasta que se
            -- vuelva a aplicar db/roles.sql. El dueño de la tabla (el migrador) concede el INSERT si el rol ya existe.
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_app') THEN
                    GRANT INSERT ON outbox_events TO identity_app;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_events");
        }
    }
}
