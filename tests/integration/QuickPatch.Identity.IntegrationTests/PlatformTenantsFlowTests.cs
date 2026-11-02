using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Gestión de tenants contra PostgreSQL real: el servicio opera como <c>identity_app</c> y solo adopta
/// <c>identity_platform</c> (BYPASSRLS) dentro de la transacción de plataforma (DD 10.4).
/// </summary>
[Collection(IdentityDefinition.Name)]
public class PlatformTenantsFlowTests(IdentityEnvironment env)
{
    private const string PlatformEmail = "plataforma@quickpatch.co";
    private const string PlatformPassword = "Plataforma123";

    private WebApplicationFactory<Program> AppWithPlatformAdmin() => env.App.WithWebHostBuilder(b =>
    {
        b.UseSetting("Bootstrap:PlatformAdminEmail", PlatformEmail);
        b.UseSetting("Bootstrap:PlatformAdminPassword", PlatformPassword);
    });

    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/v1/auth/login", UriKind.Relative), new { email = PlatformEmail, password = PlatformPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin_plataforma", body.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task AdminPlataforma_VeTodosLosTenants_YLosActivaConAuditoria()
    {
        await using var app = AppWithPlatformAdmin();
        using var client = await LoggedInAsync(app);

        using var lista = await client.GetAsync(new Uri("/v1/platform/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        var ids = (await lista.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(t => t.GetProperty("id").GetGuid());
        Assert.Contains(IdentityEnvironment.ActiveTenant, ids);
        Assert.Contains(IdentityEnvironment.InactiveTenant, ids);

        var otro = Guid.NewGuid();
        var nit = otro.ToString("N")[..12];
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{otro}', 'Gamma', '{nit}', 'activo', now())");

        using var desactivar = await client.PatchAsJsonAsync(new Uri($"/v1/platform/tenants/{otro}", UriKind.Relative), new { status = "inactivo" });
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);

        Assert.Equal("inactivo", await env.ScalarAdminAsync("SELECT status FROM tenants WHERE id = @id", ("id", otro)));
        Assert.Equal(1L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM audit_logs WHERE target_id = @id AND tenant_id IS NULL AND action = 'desactivar_tenant'", ("id", otro)));
    }

    [Fact]
    public async Task CambiarElEstadoDelTenant_PublicaTenantStatusChanged_EnLaMismaTransaccionYSoloSiCambia()
    {
        await using var app = AppWithPlatformAdmin();
        using var client = await LoggedInAsync(app);
        var otro = Guid.NewGuid();
        var nit = otro.ToString("N")[..12];
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{otro}', 'Epsilon', '{nit}', 'activo', now())");
        var uri = new Uri($"/v1/platform/tenants/{otro}", UriKind.Relative);

        using var desactivar = await client.PatchAsJsonAsync(uri, new { status = "inactivo" });
        using var repetir = await client.PatchAsJsonAsync(uri, new { status = "inactivo" });
        using var activar = await client.PatchAsJsonAsync(uri, new { status = "activo" });

        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repetir.StatusCode);
        Assert.Equal(HttpStatusCode.OK, activar.StatusCode);

        // En outbox_events: dos eventos (repetir el estado no publica), del tenant que cambió y no del administrador.
        Assert.Equal(2L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'tenant.status-changed' AND tenant_id = @id",
            ("id", otro)));

        // En Kafka: el publicador los envía al topic con la clave data.tenantId y el estado completo, en orden.
        var mensajes = env.ConsumeAll("tenant.status-changed", otro.ToString(), 2);
        Assert.Equal(2, mensajes.Count);
        Assert.All(mensajes, m =>
        {
            Assert.Equal("tenant.status-changed", m.GetProperty("eventType").GetString());
            Assert.Equal(1, m.GetProperty("eventVersion").GetInt32());
            Assert.Equal("identity-service", m.GetProperty("producer").GetString());
            Assert.Equal(otro, m.GetProperty("tenantId").GetGuid());
            Assert.Equal(otro, m.GetProperty("data").GetProperty("tenantId").GetGuid());
            Assert.NotEqual(IdentityEnvironment.ActiveTenant, m.GetProperty("tenantId").GetGuid());
        });
        Assert.Equal("inactivo", mensajes[0].GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("activo", mensajes[1].GetProperty("data").GetProperty("status").GetString());
        Assert.True(
            mensajes[1].GetProperty("data").GetProperty("updatedAt").GetDateTimeOffset()
                > mensajes[0].GetProperty("data").GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task SiElEventoNoSePuedeGuardar_ElCambioDeEstadoNoQuedaAplicado()
    {
        await using var app = AppWithPlatformAdmin();
        using var client = await LoggedInAsync(app);
        var otro = Guid.NewGuid();
        var nit = otro.ToString("N")[..12];
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{otro}', 'Zeta', '{nit}', 'activo', now())");
        // Un trigger hace fallar el insert en outbox_events: el cambio de estado y la auditoría deben deshacerse juntos.
        await env.ExecuteAdminAsync($"""
            CREATE OR REPLACE FUNCTION falla_outbox_{otro:N}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                IF NEW.aggregate_id = '{otro}' THEN RAISE EXCEPTION 'fallo simulado del outbox'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER falla_outbox_{otro:N} BEFORE INSERT ON outbox_events FOR EACH ROW EXECUTE FUNCTION falla_outbox_{otro:N}();
            """);

        try
        {
            using var respuesta = await client.PatchAsJsonAsync(new Uri($"/v1/platform/tenants/{otro}", UriKind.Relative), new { status = "inactivo" });

            Assert.True((int)respuesta.StatusCode >= 500);
            Assert.Equal("activo", await env.ScalarAdminAsync("SELECT status FROM tenants WHERE id = @id", ("id", otro)));
            Assert.Equal(0L, await env.ScalarAdminAsync("SELECT count(*) FROM audit_logs WHERE target_id = @id", ("id", otro)));
            Assert.Equal(0L, await env.ScalarAdminAsync("SELECT count(*) FROM outbox_events WHERE aggregate_id = @id", ("id", otro)));
        }
        finally
        {
            await env.ExecuteAdminAsync($"DROP TRIGGER IF EXISTS falla_outbox_{otro:N} ON outbox_events; DROP FUNCTION IF EXISTS falla_outbox_{otro:N}();");
        }
    }

    [Fact]
    public async Task AdminPlataforma_CreaTenantConAuditoria_YElNitEsUnico()
    {
        await using var app = AppWithPlatformAdmin();
        using var client = await LoggedInAsync(app);
        var nit = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-3";

        using var crear = await client.PostAsJsonAsync(new Uri("/v1/platform/tenants", UriKind.Relative), new { name = "Delta Servicios", nit });
        using var repetido = await client.PostAsJsonAsync(new Uri("/v1/platform/tenants", UriKind.Relative), new { name = "Delta Otra", nit });

        Assert.Equal(HttpStatusCode.Created, crear.StatusCode);
        var id = (await crear.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("activo", await env.ScalarAdminAsync("SELECT status FROM tenants WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM audit_logs WHERE target_id = @id AND tenant_id IS NULL AND action = 'crear_tenant'", ("id", id)));
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal(1L, await env.ScalarAdminAsync("SELECT count(*) FROM tenants WHERE nit = @nit", ("nit", nit)));
    }

    [Fact]
    public async Task ElRolDelServicioNoModificaTenantsFueraDeLaTransaccionDePlataforma()
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE tenants SET status = 'activo' WHERE id = '{IdentityEnvironment.InactiveTenant}'", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task AdminDelTenant_NoGestionaTenants()
    {
        await using var app = env.App.WithWebHostBuilder(b =>
        {
            b.UseSetting("Bootstrap:AdminEmail", "admin@quickpatch.co");
            b.UseSetting("Bootstrap:AdminPassword", "Admin12345");
        });
        using var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/v1/auth/login", UriKind.Relative), new { email = "admin@quickpatch.co", password = "Admin12345" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var lista = await client.GetAsync(new Uri("/v1/platform/tenants", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, lista.StatusCode);
    }
}