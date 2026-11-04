using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Verificación de técnicos contra PostgreSQL y Kafka reales (RF-19, RF-20, DD 8.3.13): el admin del tenant opera
/// como <c>identity_app</c> bajo RLS, la acción queda en <c>audit_logs</c> y el Outbox publica
/// <c>technician.verification-changed</c>.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class TechniciansAdminFlowTests(IdentityEnvironment env)
{
    private const string AdminEmail = "admin.verificacion@quickpatch.co";
    private const string AdminPassword = "Admin12345";

    private WebApplicationFactory<Program> AppWithTenantAdmin() => env.App.WithWebHostBuilder(b =>
    {
        b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
        b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
    });

    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/v1/auth/login", UriKind.Relative), new { email = AdminEmail, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<Guid> RegisterAsync(WebApplicationFactory<Program> app)
    {
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), new
        {
            email = $"t{Guid.NewGuid():N}@correo.co",
            password = "Segura123",
            fullName = "Carlos Pérez",
            phone = "3001234567",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
            role = "tecnico",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task AprobarYSuspender_AuditaYPublicaVerificationChanged()
    {
        await using var app = AppWithTenantAdmin();
        using var client = await LoggedInAsync(app);
        var id = await RegisterAsync(app);

        using var lista = await client.GetAsync(new Uri("/v1/admin/technicians?verificationStatus=pendiente&size=100", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        var items = (await lista.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        Assert.Contains(items, x => x.GetProperty("userId").GetGuid() == id);

        using var aprobar = await client.PostAsync(new Uri($"/v1/admin/technicians/{id}/approve", UriKind.Relative), null);
        var correlation = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/admin/technicians/{id}/suspend", UriKind.Relative))
        {
            Content = JsonContent.Create(new { reason = "Reclamos repetidos por incumplimiento." }),
        };
        request.Headers.Add("X-Correlation-Id", correlation.ToString());
        using var suspender = await client.SendAsync(request);
        using var repetido = await client.PostAsJsonAsync(new Uri($"/v1/admin/technicians/{id}/suspend", UriKind.Relative), new { reason = "Otra vez el mismo motivo" });

        Assert.Equal(HttpStatusCode.OK, aprobar.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suspender.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("suspendido", await env.ScalarAdminAsync("SELECT verification_status FROM technician_profiles WHERE user_id = @u", ("u", id)));
        Assert.Equal("Reclamos repetidos por incumplimiento.", await env.ScalarAdminAsync("SELECT verification_reason FROM technician_profiles WHERE user_id = @u", ("u", id)));
        Assert.Equal(2L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM audit_logs WHERE target_id = @u AND tenant_id = @t AND action IN ('aprobar_tecnico', 'suspender_tecnico')",
            ("u", id), ("t", IdentityEnvironment.ActiveTenant)));
        Assert.Equal(correlation.ToString(), await env.ScalarAdminAsync(
            "SELECT metadata->>'correlationId' FROM audit_logs WHERE target_id = @u AND action = 'suspender_tecnico'", ("u", id)));

        // Esta prueba levanta su propia app además de la del entorno, y cada una corre su publicador del Outbox: con SKIP
        // LOCKED cada uno puede tomar un evento y publicarlo antes que el otro, así que el orden en Kafka no está garantizado
        // aquí. Lo que importa es que lleguen los dos y que la suspensión diga de qué estado venía.
        var eventos = env.ConsumeAll("technician.verification-changed", id.ToString(), 2);
        Assert.Equal(
            ["aprobado", "suspendido"],
            eventos.Select(e => e.GetProperty("data").GetProperty("verificationStatus").GetString()).Order(StringComparer.Ordinal));
        var ultimo = eventos.Single(e => e.GetProperty("data").GetProperty("verificationStatus").GetString() == "suspendido");
        Assert.Equal(correlation, ultimo.GetProperty("correlationId").GetGuid());
        Assert.Equal("aprobado", ultimo.GetProperty("data").GetProperty("previousStatus").GetString());
        Assert.Equal("Reclamos repetidos por incumplimiento.", ultimo.GetProperty("data").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TecnicoDeOtroTenant_404_YNoCambia()
    {
        var otro = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{otro}', 'Delta', '{otro.ToString("N")[..12]}', 'activo', now())");
        await using var appOtro = env.AppFor(otro);
        var ajeno = await RegisterAsync(appOtro);
        await using var app = AppWithTenantAdmin();
        using var client = await LoggedInAsync(app);

        using var aprobar = await client.PostAsync(new Uri($"/v1/admin/technicians/{ajeno}/approve", UriKind.Relative), null);
        using var lista = await client.GetAsync(new Uri("/v1/admin/technicians?size=100", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, aprobar.StatusCode);
        Assert.Equal("pendiente", await env.ScalarAdminAsync("SELECT verification_status FROM technician_profiles WHERE user_id = @u", ("u", ajeno)));
        var items = (await lista.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(items, x => x.GetProperty("userId").GetGuid() == ajeno);
    }

    [Fact]
    public async Task Rls_ElServicioNoAuditaEnOtroTenant()
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{IdentityEnvironment.ActiveTenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            $"INSERT INTO audit_logs (id, tenant_id, action, created_at) VALUES ('{Guid.NewGuid()}', '{IdentityEnvironment.InactiveTenant}', 'aprobar_tecnico', now())",
            connection,
            transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal("42501", error.SqlState);
    }
}