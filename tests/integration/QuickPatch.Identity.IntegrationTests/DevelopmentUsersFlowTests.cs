using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using QuickPatch.Identity.Api.Bootstrap;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Usuarios de desarrollo (SCRUM-450) contra PostgreSQL real: quedan en el tenant del canal, bajo RLS, y cada uno
/// inicia sesión con la contraseña documentada.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class DevelopmentUsersFlowTests(IdentityEnvironment env)
{
    [Fact]
    public async Task CadaUsuarioDeDesarrolloIniciaSesionConSuRol()
    {
        await using var app = env.App.WithWebHostBuilder(b => b.UseSetting("DevelopmentUsers:Enabled", "true"));
        using var client = app.CreateClient();

        foreach (var usuario in DevelopmentUsers.All)
        {
            using var login = await client.PostAsJsonAsync(
                new Uri("/v1/auth/login", UriKind.Relative),
                new { email = usuario.Email, password = DevelopmentUsers.Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var body = await login.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(usuario.Role, body.GetProperty("user").GetProperty("role").GetString());
        }

        Assert.Equal(DevelopmentUsers.All.Count, Convert.ToInt32(await env.ScalarAdminAsync(
            "SELECT count(*) FROM users WHERE email LIKE '%@quickpatch.dev' AND tenant_id = @id",
            ("id", IdentityEnvironment.ActiveTenant))));
    }
}