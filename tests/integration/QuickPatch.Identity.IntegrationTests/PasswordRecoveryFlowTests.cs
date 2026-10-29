using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// SCRUM-625 contra PostgreSQL real, con el servicio como <c>identity_app</c> bajo RLS: migración, permisos de
/// <c>db/roles.sql</c>, solo hashes guardados, límite por correo con pedidos simultáneos y aislamiento entre tenants.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class PasswordRecoveryFlowTests(IdentityEnvironment env)
{
    private const string AdminEmail = "admin.recuperacion@quickpatch.co";
    private const string AdminPassword = "Admin12345";
    private static readonly Uri Request = new("/v1/auth/password-recovery", UriKind.Relative);
    private static readonly Uri Confirm = new("/v1/auth/password-recovery/confirm", UriKind.Relative);
    private static readonly Uri Admin = new("/v1/admin/password-recovery-requests?size=100", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private WebApplicationFactory<Program> AppWithTenantAdmin() => env.App.WithWebHostBuilder(b =>
    {
        b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
        b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
    });

    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> app, string email, string password)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(Login, new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task RegisterAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync(
            new Uri("/v1/auth/register/client", UriKind.Relative), new { email, password = "Vieja2026", fullName = "Ana Pérez" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    [Fact]
    public async Task PedirGenerarYConfirmar_ConSoloHashes_YLaSolicitudCerrada()
    {
        await using var app = AppWithTenantAdmin();
        using var admin = await LoggedInAsync(app, AdminEmail, AdminPassword);
        using var anonimo = app.CreateClient();
        var email = $"r{Guid.NewGuid():N}@correo.co";
        await RegisterAsync(anonimo, email);

        using var pedido = await anonimo.PostAsJsonAsync(Request, new { email });
        Assert.Equal(HttpStatusCode.Accepted, pedido.StatusCode);
        var lista = await (await admin.GetAsync(Admin)).Content.ReadFromJsonAsync<JsonElement>();
        var solicitud = lista.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();

        using var enlace = await admin.PostAsync(new Uri($"/v1/admin/password-recovery-requests/{solicitud}/link", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, enlace.StatusCode);
        var token = (await enlace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var confirmar = await anonimo.PostAsJsonAsync(Confirm, new { token, password = "Nueva2026" });
        using var entrar = await anonimo.PostAsJsonAsync(Login, new { email, password = "Nueva2026" });

        Assert.Equal(HttpStatusCode.NoContent, confirmar.StatusCode);
        Assert.Equal(HttpStatusCode.OK, entrar.StatusCode);
        Assert.Equal("cerrada", await env.ScalarAdminAsync("SELECT status FROM password_recovery_requests WHERE id = @id", ("id", solicitud)));
        Assert.Equal("recuperacion", await env.ScalarAdminAsync(
            "SELECT t.purpose FROM password_setup_tokens t JOIN users u ON u.id = t.user_id WHERE u.email = @e", ("e", email)));
        Assert.Equal(0L, await env.ScalarAdminAsync("SELECT count(*) FROM password_setup_tokens WHERE token_hash = @t", ("t", token)));
        Assert.Equal(0L, await env.ScalarAdminAsync("SELECT count(*) FROM password_recovery_attempts WHERE email_hash LIKE '%@%'"));
        Assert.Equal(1L, await env.ScalarAdminAsync(
            "SELECT count(*) FROM audit_logs WHERE action = 'generar_enlace_recuperacion' AND target_id = @id", ("id", solicitud)));
    }

    [Fact]
    public async Task PedidosSimultaneosDelMismoCorreo_SoloTresPasan()
    {
        var email = $"x{Guid.NewGuid():N}@correo.co";
        using var anonimo = env.App.CreateClient();

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => anonimo.PostAsJsonAsync(Request, new { email })));

        Assert.Equal(3, respuestas.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, respuestas.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.All(respuestas.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests), r => Assert.True(r.Headers.Contains("Retry-After")));
        foreach (var r in respuestas)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task OtroTenant_NoVeLaSolicitud_NiLaDeLaSesionSinTenant()
    {
        await using var app = AppWithTenantAdmin();
        using var admin = await LoggedInAsync(app, AdminEmail, AdminPassword);
        var gama = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{gama}', 'Gama', '{gama.ToString("N")[..12]}', 'activo', now());");
        await using var appGama = env.AppFor(gama).WithWebHostBuilder(b =>
        {
            b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
        });
        using var adminGama = await LoggedInAsync(appGama, AdminEmail, AdminPassword);
        using var anonimo = app.CreateClient();
        var email = $"r{Guid.NewGuid():N}@correo.co";
        await RegisterAsync(anonimo, email);
        using var pedido = await anonimo.PostAsJsonAsync(Request, new { email });
        var lista = await (await admin.GetAsync(Admin)).Content.ReadFromJsonAsync<JsonElement>();
        var solicitud = lista.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();

        var listaGama = await (await adminGama.GetAsync(Admin)).Content.ReadFromJsonAsync<JsonElement>();
        using var enlaceGama = await adminGama.PostAsync(new Uri($"/v1/admin/password-recovery-requests/{solicitud}/link", UriKind.Relative), null);

        Assert.DoesNotContain(listaGama.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == solicitud);
        Assert.Equal(HttpStatusCode.NotFound, enlaceGama.StatusCode);

        // Como identity_app y sin tenant en la sesión, RLS no deja ver ninguna fila.
        Assert.Equal(0L, await CountAsAppAsync("password_recovery_requests"));
        Assert.Equal(0L, await CountAsAppAsync("password_recovery_attempts"));
    }

    private async Task<long> CountAsAppAsync(string table)
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}