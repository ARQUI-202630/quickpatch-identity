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