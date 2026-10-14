using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Endpoints del equipo del proveedor (contrato identity 1.4.0, DD 8.3.14).</summary>
public class ProviderTeamApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Team = new("/v1/providers/me/technicians", UriKind.Relative);

    /// <summary>Cliente con el token de un usuario; si <paramref name="persist"/>, el usuario existe en el tenant.</summary>
    private HttpClient ClientAs(string role, bool persist = true)
    {
        var user = User.Create(IdentityApiFactory.ActiveTenant, $"{role}{Guid.NewGuid():N}@quickpatch.co", "hash", role, "Persona", null, Now);
        if (persist)
        {
            factory.Store.Users.Add(user);
        }

        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Body(string? email = null, string? document = null) => new
    {
        email = email ?? $"t{Guid.NewGuid():N}@correo.co",
        fullName = "Carlos Pérez",
        phone = "3001234567",
        documentId = document ?? Guid.NewGuid().ToString("N")[..10],
        specialtyId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Proveedor_DaDeAltaYListaSuEquipo()
    {
        using var client = ClientAs(Roles.Proveedor);

        using var alta = await client.PostAsJsonAsync(Team, Body());

        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creado = await alta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tecnico", creado.GetProperty("role").GetString());
        Assert.Equal("pendiente", creado.GetProperty("verificationStatus").GetString());
        var id = creado.GetProperty("id").GetGuid();

        using var lista = await client.GetAsync(new Uri("/v1/providers/me/technicians?status=inactivo", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        var page = await lista.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("totalItems").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("userId").GetGuid());
        Assert.Equal("Carlos Pérez", item.GetProperty("fullName").GetString());
        Assert.Equal("pendiente", item.GetProperty("verificationStatus").GetString());
        Assert.True(item.TryGetProperty("specialtyId", out _));
        Assert.False(item.TryGetProperty("email", out _));

        using var activos = await client.GetAsync(new Uri("/v1/providers/me/technicians?status=activo", UriKind.Relative));
        Assert.Equal(0, (await activos.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task OtroProveedor_NoVeElEquipoAjeno()
    {
        using var proveedor = ClientAs(Roles.Proveedor);
        using var otro = ClientAs(Roles.Proveedor);
        using var alta = await proveedor.PostAsJsonAsync(Team, Body());
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        using var lista = await otro.GetAsync(Team);

        Assert.Equal(0, (await lista.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Alta_CorreoODocumentoRepetidos_409()
    {
        using var client = ClientAs(Roles.Proveedor);
        var email = $"r{Guid.NewGuid():N}@correo.co";
        var document = Guid.NewGuid().ToString("N")[..10];
        using var primero = await client.PostAsJsonAsync(Team, Body(email, document));

        using var correo = await client.PostAsJsonAsync(Team, Body(email: email));
        using var documento = await client.PostAsJsonAsync(Team, Body(document: document));

        Assert.Equal(HttpStatusCode.Conflict, correo.StatusCode);
        Assert.EndsWith("correo-registrado", (await correo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, documento.StatusCode);
        Assert.EndsWith("documento-registrado", (await documento.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alta_DatosInvalidos_400()
    {
        using var client = ClientAs(Roles.Proveedor);

        using var response = await client.PostAsJsonAsync(Team, new { email = "x", fullName = "C" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("documentId", out _));
    }

    [Fact]
    public async Task Listar_EstadoInvalido_400()
    {
        using var client = ClientAs(Roles.Proveedor);

        using var response = await client.GetAsync(new Uri("/v1/providers/me/technicians?status=aprobado", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProveedorQueYaNoExiste_403()
    {
        using var client = ClientAs(Roles.Proveedor, persist: false);

        using var response = await client.PostAsJsonAsync(Team, Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.EndsWith("no-autorizado", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.AdminTenant)]
    public async Task OtrosRoles_Reciben403(string role)
    {
        using var client = ClientAs(role);

        using var alta = await client.PostAsJsonAsync(Team, Body());
        using var lista = await client.GetAsync(Team);

        Assert.Equal(HttpStatusCode.Forbidden, alta.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lista.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Team);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}