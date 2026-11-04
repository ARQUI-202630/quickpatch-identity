using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Equipo del proveedor contra PostgreSQL y Kafka reales (RF-16, DD 8.3.14): el alta queda con <c>provider_id</c> bajo
/// RLS y la FK física (P-02), el técnico no puede iniciar sesión sin definir su contraseña, el Outbox publica
/// <c>technician.registered</c> con <c>providerId</c> y cada proveedor solo ve su equipo (RN-TP4).
/// </summary>
[Collection(IdentityDefinition.Name)]
public class ProviderTeamFlowTests(IdentityEnvironment env)
{
    private static readonly Uri Team = new("/v1/providers/me/technicians", UriKind.Relative);

    /// <summary>Registra un proveedor por el canal público e inicia sesión con él.</summary>
    private static async Task<(HttpClient Client, Guid Id)> ProviderAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var email = $"p{Guid.NewGuid():N}@correo.co";
        using var registro = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), new
        {
            email,
            password = "Segura123",
            fullName = "Proveedor Uno",
            phone = "3001234567",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
            role = "proveedor",
        });
        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);
        var id = (await registro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var login = await client.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "Segura123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }

    [Fact]
    public async Task Alta_GuardaElProveedorYPublicaTechnicianRegistered()
    {
        var (client, providerId) = await ProviderAsync(env.App);
        var email = $"t{Guid.NewGuid():N}@correo.co";
        var correlation = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, Team)
        {
            Content = JsonContent.Create(new
            {
                email,
                fullName = "Técnico del Equipo",
                phone = "3009876543",
                documentId = Guid.NewGuid().ToString("N")[..12],
                specialtyId = Guid.NewGuid(),
            }),
        };
        request.Headers.Add("X-Correlation-Id", correlation.ToString());

        using var alta = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var id = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(providerId, (Guid)(await env.ScalarAdminAsync("SELECT provider_id FROM technician_profiles WHERE user_id = @u", ("u", id)))!);
        Assert.Equal(IdentityEnvironment.ActiveTenant, (Guid)(await env.ScalarAdminAsync("SELECT tenant_id FROM users WHERE id = @u", ("u", id)))!);
        Assert.Equal("tecnico", await env.ScalarAdminAsync("SELECT role FROM users WHERE id = @u", ("u", id)));

        var mensaje = env.Consume("technician.registered", id.ToString());
        Assert.NotNull(mensaje);
        var evento = mensaje.Value.Value;
        Assert.Equal(correlation, evento.GetProperty("correlationId").GetGuid());
        Assert.Equal(providerId, evento.GetProperty("data").GetProperty("providerId").GetGuid());

        // Sin contraseña utilizable: ninguna contraseña inicia sesión hasta que el técnico defina la suya.
        using var anonimo = env.App.CreateClient();
        using var login = await anonimo.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "Segura123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        client.Dispose();
    }

    [Fact]
    public async Task Listar_CadaProveedorVeSoloSuEquipo()
    {
        var (uno, _) = await ProviderAsync(env.App);
        var (dos, _) = await ProviderAsync(env.App);
        using var alta = await uno.PostAsJsonAsync(Team, new
        {
            email = $"t{Guid.NewGuid():N}@correo.co",
            fullName = "Técnico de Uno",
            phone = "3009876543",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var id = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var propio = await uno.GetAsync(new Uri("/v1/providers/me/technicians?status=inactivo", UriKind.Relative));
        using var ajeno = await dos.GetAsync(Team);

        var items = (await propio.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(id, Assert.Single(items).GetProperty("userId").GetGuid());
        Assert.Equal(0, (await ajeno.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalItems").GetInt32());
        uno.Dispose();
        dos.Dispose();
    }

    [Fact]
    public async Task Fk_ProviderIdDebeSerUnUsuarioExistente()
    {
        var (client, _) = await ProviderAsync(env.App);
        using var alta = await client.PostAsJsonAsync(Team, new
        {
            email = $"t{Guid.NewGuid():N}@correo.co",
            fullName = "Técnico Fk",
            phone = "3009876543",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
        });
        var id = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.ExecuteAdminAsync(
            $"UPDATE technician_profiles SET provider_id = '{Guid.NewGuid()}' WHERE user_id = '{id}'"));

        Assert.Equal("fk_technician_profiles_provider", ex.ConstraintName);
        client.Dispose();
    }

    [Fact]
    public async Task Invitacion_ElTecnicoDefineSuContrasenaUnaVez_BajoRls()
    {
        var (client, _) = await ProviderAsync(env.App);
        var email = $"t{Guid.NewGuid():N}@correo.co";
        using var alta = await client.PostAsJsonAsync(Team, new
        {
            email,
            fullName = "Técnico Invitado",
            phone = "3009876543",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
        });
        var creado = await alta.Content.ReadFromJsonAsync<JsonElement>();
        var id = creado.GetProperty("id").GetGuid();
        var token = creado.GetProperty("invitation").GetProperty("token").GetString()!;
        using var anonimo = env.App.CreateClient();
        var setup = new Uri("/v1/auth/password/setup", UriKind.Relative);

        using var definir = await anonimo.PostAsJsonAsync(setup, new { token, password = "MiClave2026" });
        using var repetir = await anonimo.PostAsJsonAsync(setup, new { token, password = "OtraClave2026" });
        using var login = await anonimo.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "MiClave2026" });
        using var renovar = await client.PostAsync(new Uri($"/v1/providers/me/technicians/{id}/invitation", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NoContent, definir.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repetir.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, renovar.StatusCode);

        // En la base solo queda el hash, la invitación usada y del tenant del técnico.
        Assert.Equal(0L, await env.ScalarAdminAsync("SELECT count(*) FROM password_setup_tokens WHERE token_hash = @t", ("t", token)));
        Assert.Equal(1L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM password_setup_tokens WHERE user_id = @u AND used_at IS NOT NULL AND tenant_id = @tenant",
            ("u", id),
            ("tenant", IdentityEnvironment.ActiveTenant)));

        // RLS: con otro tenant en la sesión, el rol del servicio no ve la invitación.
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", Guid.NewGuid().ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM password_setup_tokens WHERE user_id = @u", connection, transaction);
        count.Parameters.AddWithValue("u", id);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        client.Dispose();
    }

    [Fact]
    public async Task Invitacion_ConElCanalDeOtroTenant_EsEnlaceInvalido()
    {
        var gama = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{gama}', 'Gama', '{Random.Shared.Next(100_000_000, 999_999_999)}-5', 'activo', now());");
        var (client, _) = await ProviderAsync(env.App);
        using var alta = await client.PostAsJsonAsync(Team, new
        {
            email = $"t{Guid.NewGuid():N}@correo.co",
            fullName = "Técnico Invitado",
            phone = "3009876543",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
        });
        var token = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation").GetProperty("token").GetString();
        await using var app = env.App.WithWebHostBuilder(b => b.UseSetting("Channel:Tenants:canal-gama", gama.ToString()));
        using var gamaClient = app.CreateClient();
        gamaClient.DefaultRequestHeaders.Add("X-Channel-Id", "canal-gama");

        using var response = await gamaClient.PostAsJsonAsync(new Uri("/v1/auth/password/setup", UriKind.Relative), new { token, password = "MiClave2026" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        client.Dispose();
    }
}