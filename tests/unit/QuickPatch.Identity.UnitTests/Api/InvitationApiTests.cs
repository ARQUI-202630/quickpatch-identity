using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Invitación del técnico del equipo del proveedor (contrato identity 1.7.0, DD 8.3.14).</summary>
public class InvitationApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Team = new("/v1/providers/me/technicians", UriKind.Relative);
    private static readonly Uri Setup = new("/v1/auth/password/setup", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private HttpClient ProviderClient()
    {
        var user = User.Create(IdentityApiFactory.ActiveTenant, $"p{Guid.NewGuid():N}@quickpatch.co", "hash", Roles.Proveedor, "Proveedor", null, Now);
        factory.Store.Users.Add(user);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken);
        return client;
    }

    private static object Body(string email) => new
    {
        email,
        fullName = "Carlos Pérez",
        phone = "3001234567",
        documentId = Guid.NewGuid().ToString("N")[..10],
        specialtyId = Guid.NewGuid(),
    };

    private static async Task<(Guid Id, string Token, string Link)> AltaAsync(HttpClient provider, string email)
    {
        using var alta = await provider.PostAsJsonAsync(Team, Body(email));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var body = await alta.Content.ReadFromJsonAsync<JsonElement>();
        var invitation = body.GetProperty("invitation");
        return (body.GetProperty("id").GetGuid(), invitation.GetProperty("token").GetString()!, invitation.GetProperty("link").GetString()!);
    }

    [Fact]
    public async Task Alta_DevuelveLaInvitacion_YElTecnicoDefineSuContrasenaYEntra()
    {
        using var provider = ProviderClient();
        var email = $"t{Guid.NewGuid():N}@correo.co";
        var (_, token, link) = await AltaAsync(provider, email);
        using var anonimo = factory.CreateClient();

        using var antes = await anonimo.PostAsJsonAsync(Login, new { email, password = "MiClave2026" });
        using var definir = await anonimo.PostAsJsonAsync(Setup, new { token, password = "MiClave2026" });
        using var otraVez = await anonimo.PostAsJsonAsync(Setup, new { token, password = "OtraClave2026" });
        using var despues = await anonimo.PostAsJsonAsync(Login, new { email, password = "MiClave2026" });

        Assert.Equal(43, token.Length);
        Assert.Equal($"https://quickpatch.internal/activar#token={token}", link);
        Assert.Equal(HttpStatusCode.Unauthorized, antes.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, definir.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        Assert.EndsWith("enlace-invalido", (await otraVez.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, despues.StatusCode);

        // Solo se guarda el hash del token.
        Assert.DoesNotContain(factory.Store.Invitations, i => i.TokenHash == token);
    }

    [Fact]
    public async Task InvitacionNueva_AnulaLaAnterior_YDespuesDeDefinirlaDa409()
    {
        using var provider = ProviderClient();
        var (id, viejo, _) = await AltaAsync(provider, $"t{Guid.NewGuid():N}@correo.co");
        var renovar = new Uri($"/v1/providers/me/technicians/{id}/invitation", UriKind.Relative);
        using var anonimo = factory.CreateClient();

        using var nueva = await provider.PostAsync(renovar, null);
        var nuevo = (await nueva.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var conViejo = await anonimo.PostAsJsonAsync(Setup, new { token = viejo, password = "MiClave2026" });
        using var conNuevo = await anonimo.PostAsJsonAsync(Setup, new { token = nuevo, password = "MiClave2026" });
        using var yaDefinida = await provider.PostAsync(renovar, null);

        Assert.Equal(HttpStatusCode.Created, nueva.StatusCode);
        Assert.NotEqual(viejo, nuevo);
        Assert.Equal(HttpStatusCode.BadRequest, conViejo.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, conNuevo.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, yaDefinida.StatusCode);
    }

    [Fact]
    public async Task OtroProveedor_NoPuedeRenovarLaInvitacion_404()
    {
        using var duenio = ProviderClient();
        using var otro = ProviderClient();
        var (id, _, _) = await AltaAsync(duenio, $"t{Guid.NewGuid():N}@correo.co");

        using var response = await otro.PostAsync(new Uri($"/v1/providers/me/technicians/{id}/invitation", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ContrasenaCorta_400ConElCampo_YTokenVacio_400EnlaceInvalido()
    {
        using var anonimo = factory.CreateClient();

        using var corta = await anonimo.PostAsJsonAsync(Setup, new { token = "x", password = "corta" });
        using var sinToken = await anonimo.PostAsJsonAsync(Setup, new { token = " ", password = "MiClave2026" });
        using var inventado = await anonimo.PostAsJsonAsync(Setup, new { token = new string('a', 43), password = "MiClave2026" });

        Assert.Equal(HttpStatusCode.BadRequest, corta.StatusCode);
        Assert.True((await corta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("password", out _));
        Assert.EndsWith("enlace-invalido", (await sinToken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("enlace-invalido", (await inventado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtroRol_NoRenuevaInvitaciones_403()
    {
        var admin = User.Create(IdentityApiFactory.ActiveTenant, $"a{Guid.NewGuid():N}@quickpatch.co", "hash", Roles.AdminTenant, "Admin", null, Now);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(admin).AccessToken);

        using var response = await client.PostAsync(new Uri($"/v1/providers/me/technicians/{Guid.NewGuid()}/invitation", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}