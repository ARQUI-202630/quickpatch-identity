using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary><c>PATCH /v1/users/me</c> (identity 1.9.0, SCRUM-611): edición del perfil propio.</summary>
public class ProfileEditApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly Uri Me = new("/v1/users/me", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>Cliente registrado y con sesión iniciada; devuelve el cliente HTTP con el token y el correo.</summary>
    private async Task<(HttpClient Client, string Email)> SignedInAsync(string? phone = null)
    {
        var client = factory.CreateClient();
        var email = $"u{Guid.NewGuid():N}@correo.co";
        using (var register = await client.PostAsJsonAsync(
            new Uri("/v1/auth/register/client", UriKind.Relative), new { email, password = "Segura123", fullName = "Ana Pérez", phone }))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        using var login = await client.PostAsJsonAsync(new Uri("/v1/auth/login", UriKind.Relative), new { email, password = "Segura123" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Json(login)).GetProperty("accessToken").GetString());
        return (client, email);
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, string json) =>
        client.PatchAsync(Me, new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task EditaNombreYTelefono_YElGetLoRefleja()
    {
        var (client, email) = await SignedInAsync();
        using (client)
        {
            using var response = await Patch(client, """{"fullName":"  Ana María Pérez ","phone":"3001234567"}""");
            using var me = await client.GetAsync(Me);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await Json(response);
            Assert.Equal("Ana María Pérez", body.GetProperty("fullName").GetString());
            Assert.Equal("3001234567", body.GetProperty("phone").GetString());
            Assert.Equal(email, body.GetProperty("email").GetString());
            Assert.Equal("Ana María Pérez", (await Json(me)).GetProperty("fullName").GetString());
        }
    }

    [Fact]
    public async Task SoloElTelefono_NoCambiaElNombre_YNullLoQuita()
    {
        var (client, _) = await SignedInAsync("3001112233");
        using (client)
        {
            using var sinNombre = await Patch(client, """{"phone":"3109998877"}""");
            using var quitar = await Patch(client, """{"phone":null}""");

            Assert.Equal("Ana Pérez", (await Json(sinNombre)).GetProperty("fullName").GetString());
            Assert.Equal("3109998877", (await Json(sinNombre)).GetProperty("phone").GetString());
            Assert.Equal(HttpStatusCode.OK, quitar.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await Json(quitar)).GetProperty("phone").ValueKind);
        }
    }

    [Theory]
    [InlineData("""{"email":"otro@correo.co"}""", "email")]
    [InlineData("""{"role":"admin_tenant"}""", "role")]
    [InlineData("""{"tenantId":"00000000-0000-0000-0000-000000000001"}""", "tenantId")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000001","fullName":"Otro"}""", "id")]
    [InlineData("""{"verificationStatus":"aprobado"}""", "verificationStatus")]
    [InlineData("""{"fullName":"A"}""", "fullName")]
    [InlineData("""{"fullName":null}""", "fullName")]
    [InlineData("""{"phone":"123"}""", "phone")]
    [InlineData("""{"phone":12345678}""", "phone")]
    [InlineData("""{}""", "body")]
    [InlineData("""[]""", "body")]
    public async Task CamposNoEditablesOInvalidos_400_SinCambios(string json, string field)
    {
        var (client, email) = await SignedInAsync();
        using (client)
        {
            using var response = await Patch(client, json);
            using var me = await client.GetAsync(Me);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await Json(response)).GetProperty("errors").TryGetProperty(field, out _));
            var perfil = await Json(me);
            Assert.Equal("Ana Pérez", perfil.GetProperty("fullName").GetString());
            Assert.Equal(email, perfil.GetProperty("email").GetString());
            Assert.Equal("cliente", perfil.GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var client = factory.CreateClient();

        using var response = await Patch(client, """{"fullName":"Ana"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Dominio_TecnicoNoPuedeQuitarElTelefono()
    {
        var user = User.Create(Guid.NewGuid(), "t@correo.co", "hash", Roles.Tecnico, "Carlos Ruiz", "3001234567", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<DomainValidationException>(() => user.UpdateProfile(null, true, null));

        Assert.Contains("phone", ex.Errors.Keys);
        Assert.Equal("3001234567", user.Phone);
    }

    [Fact]
    public void Dominio_SinCambios_DejaTodoIgual()
    {
        var user = User.Create(Guid.NewGuid(), "c@correo.co", "hash", Roles.Cliente, "Ana Pérez", null, DateTimeOffset.UtcNow);

        user.UpdateProfile(null, false, null);

        Assert.Equal(("Ana Pérez", (string?)null), (user.FullName, user.Phone));
    }
}