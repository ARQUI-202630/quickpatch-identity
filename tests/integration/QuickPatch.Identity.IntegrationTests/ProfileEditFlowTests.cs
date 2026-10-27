using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// SCRUM-611 contra PostgreSQL real, con el servicio como <c>identity_app</c> bajo RLS: <c>PATCH /v1/users/me</c> solo
/// cambia al usuario del token, en el tenant del token. Prueba de fuga entre usuarios y entre tenants (SCRUM-609).
/// </summary>
[Collection(IdentityDefinition.Name)]
public class ProfileEditFlowTests(IdentityEnvironment env)
{
    private static readonly Uri Me = new("/v1/users/me", UriKind.Relative);

    private static async Task<HttpClient> SignedInAsync(HttpClient client, string email)
    {
        using (var register = await client.PostAsJsonAsync(
            new Uri("/v1/auth/register/client", UriKind.Relative), new { email, password = "Segura123", fullName = "Ana Pérez" }))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        using var login = await client.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "Segura123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, string json) =>
        client.PatchAsync(Me, new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<string?> NameAsync(string email, Guid tenant) =>
        (string?)await env.ScalarAdminAsync("SELECT full_name FROM users WHERE email = @e AND tenant_id = @t", ("e", email), ("t", tenant));

    [Fact]
    public async Task SoloCambiaAlUsuarioDelToken_NoAOtroDelMismoTenant()
    {
        var (ana, beto) = ($"u{Guid.NewGuid():N}@correo.co", $"u{Guid.NewGuid():N}@correo.co");
        using var clienteAna = await SignedInAsync(env.App.CreateClient(), ana);
        using var clienteBeto = await SignedInAsync(env.App.CreateClient(), beto);

        using var response = await Patch(clienteAna, """{"fullName":"Ana María Pérez","phone":"3001234567"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ana María Pérez", await NameAsync(ana, IdentityEnvironment.ActiveTenant));
        Assert.Equal("3001234567", (string?)await env.ScalarAdminAsync("SELECT phone FROM users WHERE email = @e", ("e", ana)));
        Assert.Equal("Ana Pérez", await NameAsync(beto, IdentityEnvironment.ActiveTenant));
    }

    [Fact]
    public async Task MismoCorreoEnOtroTenant_NoSeToca()
    {
        var gama = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{gama}', 'Gama', '9{Guid.NewGuid().ToString("N")[..8]}-1', 'activo', now());");
        var email = $"u{Guid.NewGuid():N}@correo.co";
        using var enAlfa = await SignedInAsync(env.App.CreateClient(), email);
        using var appGama = env.AppFor(gama);
        using var enGama = await SignedInAsync(appGama.CreateClient(), email);

        using var response = await Patch(enGama, """{"fullName":"Ana de Gama"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ana de Gama", await NameAsync(email, gama));
        Assert.Equal("Ana Pérez", await NameAsync(email, IdentityEnvironment.ActiveTenant));
    }

    [Fact]
    public async Task CorreoRolYTenantNoSeEditan()
    {
        var email = $"u{Guid.NewGuid():N}@correo.co";
        using var client = await SignedInAsync(env.App.CreateClient(), email);

        using var response = await Patch(client, $$"""{"email":"otro@correo.co","role":"admin_tenant","tenantId":"{{Guid.NewGuid()}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cliente", (string?)await env.ScalarAdminAsync("SELECT role FROM users WHERE email = @e", ("e", email)));
        Assert.Equal(IdentityEnvironment.ActiveTenant, (Guid)(await env.ScalarAdminAsync("SELECT tenant_id FROM users WHERE email = @e", ("e", email)))!);
    }
}