using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Consulta interna por lote contra PostgreSQL real (contrato identity-internal 1.0.0, SCRUM-91): corre como
/// <c>identity_app</c> bajo RLS, así que un usuario de otro tenant nunca aparece aunque se pida su id.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class UsersLookupFlowTests(IdentityEnvironment env)
{
    private static string NewEmail() => $"u{Guid.NewGuid():N}@correo.co";

    [Fact]
    public async Task Lote_DevuelveSoloUsuariosDelTenantDelToken()
    {
        using var client = env.App.CreateClient();
        var email = NewEmail();
        using var registro = await client.PostAsJsonAsync(
            new Uri("/v1/auth/register/client", UriKind.Relative), new { email, password = "Segura123", fullName = "Ana Gómez", phone = "+573009876543" });
        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);
        var anaId = (await registro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var ajeno = Guid.NewGuid();
        await env.ExecuteAdminAsync(
            "INSERT INTO users (id, tenant_id, email, password_hash, role, full_name, phone, failed_login_attempts, created_at) " +
            $"VALUES ('{ajeno}', '{IdentityEnvironment.InactiveTenant}', '{NewEmail()}', 'x', 'tecnico', 'Otro Tenant', '3000000000', 0, now())");

        using var login = await client.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "Segura123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(new Uri($"/internal/v1/users?ids={anaId}&ids={ajeno}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(anaId, item.GetProperty("id").GetGuid());
        Assert.Equal("Ana Gómez", item.GetProperty("fullName").GetString());
        Assert.Equal("+573009876543", item.GetProperty("phone").GetString());
    }
}