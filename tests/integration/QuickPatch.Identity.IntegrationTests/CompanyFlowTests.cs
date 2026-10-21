using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

using Npgsql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Cuenta corporativa contra PostgreSQL real (RF-06, DD 5.19 y 8.3.18): la empresa y su primer usuario quedan en el
/// tenant del canal bajo RLS, la base exige RN-CC1 y RN-CC2, el usuario agregado define su contraseña con la invitación
/// y su token trae <c>company_id</c>, y otro tenant no ve la empresa.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class CompanyFlowTests(IdentityEnvironment env)
{
    private static readonly Uri Register = new("/v1/auth/register/company", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);
    private static readonly Uri Users = new("/v1/companies/me/users", UriKind.Relative);

    private static string Nit() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture) + "-7";

    private static object Body(string nit, string email) => new
    {
        company = new { legalName = "Inversiones Andinas S.A.S.", nit, contactEmail = "mantenimiento@andinas.com.co" },
        user = new { email, password = "Segura123", fullName = "Laura Mora" },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var login = await client.PostAsJsonAsync(Login, new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await Json(login)).GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task Registro_InvitacionYLogin_ConCompanyIdEnElToken()
    {
        using var client = env.App.CreateClient();
        var email = $"f{Guid.NewGuid():N}@andinas.com.co";
        using var registro = await client.PostAsJsonAsync(Register, Body(Nit(), email));
        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);
        var registered = await Json(registro);
        var companyId = registered.GetProperty("company").GetProperty("id").GetGuid();
        var founderId = registered.GetProperty("user").GetProperty("id").GetGuid();

        Assert.Equal(IdentityEnvironment.ActiveTenant, (Guid)(await env.ScalarAdminAsync("SELECT tenant_id FROM client_companies WHERE id = @c", ("c", companyId)))!);
        Assert.Equal(companyId, (Guid)(await env.ScalarAdminAsync("SELECT company_id FROM users WHERE id = @u", ("u", founderId)))!);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, email, "Segura123"));
        var invitedEmail = $"a{Guid.NewGuid():N}@andinas.com.co";
        using var alta = await client.PostAsJsonAsync(Users, new { email = invitedEmail, fullName = "Andrés Peña" });
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var token = (await Json(alta)).GetProperty("invitation").GetProperty("token").GetString()!;

        using var anonimo = env.App.CreateClient();
        using var sinClave = await anonimo.PostAsJsonAsync(Login, new { email = invitedEmail, password = "Segura123" });
        Assert.Equal(HttpStatusCode.Unauthorized, sinClave.StatusCode);
        using var definir = await anonimo.PostAsJsonAsync(new Uri("/v1/auth/password/setup", UriKind.Relative), new { token, password = "MiClave2026" });
        Assert.Equal(HttpStatusCode.NoContent, definir.StatusCode);

        var invitedToken = await LoginAsync(anonimo, invitedEmail, "MiClave2026");
        Assert.Equal(companyId.ToString(), new JsonWebTokenHandler().ReadJsonWebToken(invitedToken).GetClaim("company_id").Value);

        using var lista = await client.GetAsync(Users);
        var items = (await Json(lista)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.GetProperty("passwordDefined").GetBoolean()));
    }

    [Fact]
    public async Task NitUnicoPorTenant_EnLaBase()
    {
        using var client = env.App.CreateClient();
        var nit = Nit();
        using var primero = await client.PostAsJsonAsync(Register, Body(nit, $"f{Guid.NewGuid():N}@andinas.com.co"));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        using var segundo = await client.PostAsJsonAsync(Register, Body(nit, $"g{Guid.NewGuid():N}@andinas.com.co"));
        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.ExecuteAdminAsync($"""
            INSERT INTO client_companies (id, tenant_id, legal_name, nit, contact_email, created_at)
            VALUES ('{Guid.NewGuid()}', '{IdentityEnvironment.ActiveTenant}', 'Copia', '{nit}', 'c@copia.co', now());
            """));
        Assert.Equal("ux_client_companies_tenant_nit", ex.ConstraintName);
    }

    [Fact]
    public async Task RnCc2_LaBaseRechazaEmpresaContactoSinEmpresaYClienteConEmpresa()
    {
        using var client = env.App.CreateClient();
        var email = $"f{Guid.NewGuid():N}@andinas.com.co";
        using var registro = await client.PostAsJsonAsync(Register, Body(Nit(), email));
        var companyId = (await Json(registro)).GetProperty("company").GetProperty("id").GetGuid();

        var sinEmpresa = await Assert.ThrowsAsync<PostgresException>(() => env.ExecuteAdminAsync($"""
            INSERT INTO users (id, tenant_id, email, password_hash, role, full_name, failed_login_attempts, created_at)
            VALUES ('{Guid.NewGuid()}', '{IdentityEnvironment.ActiveTenant}', 'x{Guid.NewGuid():N}@a.co', 'h', 'empresa_contacto', 'X', 0, now());
            """));
        var clienteConEmpresa = await Assert.ThrowsAsync<PostgresException>(() => env.ExecuteAdminAsync($"""
            INSERT INTO users (id, tenant_id, email, password_hash, role, full_name, failed_login_attempts, company_id, created_at)
            VALUES ('{Guid.NewGuid()}', '{IdentityEnvironment.ActiveTenant}', 'y{Guid.NewGuid():N}@a.co', 'h', 'cliente', 'Y', 0, '{companyId}', now());
            """));

        Assert.Equal("ck_users_company_role", sinEmpresa.ConstraintName);
        Assert.Equal("ck_users_company_role", clienteConEmpresa.ConstraintName);
    }

    [Fact]
    public async Task OtroTenant_NoVeLaEmpresa_NiPorRlsNiPorElCanal()
    {
        using var client = env.App.CreateClient();
        var nit = Nit();
        using var registro = await client.PostAsJsonAsync(Register, Body(nit, $"f{Guid.NewGuid():N}@andinas.com.co"));
        var companyId = (await Json(registro)).GetProperty("company").GetProperty("id").GetGuid();

        // RLS: con otro tenant en la sesión, el rol del servicio no ve la empresa.
        await using (var connection = new NpgsqlConnection(env.AppConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("t", Guid.NewGuid().ToString());
                await set.ExecuteNonQueryAsync();
            }

            await using var count = new NpgsqlCommand("SELECT count(*) FROM client_companies WHERE id = @c", connection, transaction);
            count.Parameters.AddWithValue("c", companyId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // El mismo NIT se registra en otro tenant: la empresa es una cuenta dentro de cada tenant (DEP-13).
        var gama = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{gama}', 'Gama', '{Random.Shared.Next(100_000_000, 999_999_999)}-4', 'activo', now());");
        await using var app = env.App.WithWebHostBuilder(b => b.UseSetting("Channel:Tenants:canal-gama", gama.ToString()));
        using var gamaClient = app.CreateClient();
        gamaClient.DefaultRequestHeaders.Add("X-Channel-Id", "canal-gama");
        var email = $"g{Guid.NewGuid():N}@andinas.com.co";
        using var enGama = await gamaClient.PostAsJsonAsync(Register, Body(nit, email));
        Assert.Equal(HttpStatusCode.Created, enGama.StatusCode);
        Assert.Equal(gama, (Guid)(await env.ScalarAdminAsync(
            "SELECT tenant_id FROM client_companies WHERE id = @c", ("c", (await Json(enGama)).GetProperty("company").GetProperty("id").GetGuid())))!);

        gamaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(gamaClient, email, "Segura123"));
        using var lista = await gamaClient.GetAsync(Users);
        Assert.Equal(1, (await Json(lista)).GetProperty("totalItems").GetInt32());
    }
}