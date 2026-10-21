using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Consulta interna por lote (contrato identity-internal 1.0.0, DD V4 8.3.4, SCRUM-91).</summary>
public class UsersLookupTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private User AddUser(Guid tenant, string role, string fullName, string? phone)
    {
        var user = User.Create(tenant, $"{role}{Guid.NewGuid():N}@quickpatch.co", "hash", role, fullName, phone, Now);
        factory.Store.Users.Add(user);
        return user;
    }

    private HttpClient ClientAs(string role, Guid? tenant = null)
    {
        var user = User.Create(
            tenant ?? IdentityApiFactory.ActiveTenant, $"{role}{Guid.NewGuid():N}@quickpatch.co", "hash", role, "Persona", null, Now,
            companyId: role == Roles.EmpresaContacto ? Guid.NewGuid() : null);
        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Uri Lookup(params object[] ids) =>
        new("/internal/v1/users" + (ids.Length == 0 ? string.Empty : "?" + string.Join('&', ids.Select(i => $"ids={i}"))), UriKind.Relative);

    [Theory]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.EmpresaContacto)]
    [InlineData(Roles.AdminTenant)]
    public async Task DevuelveNombreYTelefonoDelTenantDelToken(string role)
    {
        var cliente = AddUser(IdentityApiFactory.ActiveTenant, Roles.Cliente, "Ana Gómez", "+573009876543");
        var tecnico = AddUser(IdentityApiFactory.ActiveTenant, Roles.Tecnico, "Carlos Pérez", "3101234567");
        using var client = ClientAs(role);

        using var response = await client.GetAsync(Lookup(cliente.Id, tecnico.Id, cliente.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var ana = items.Single(x => x.GetProperty("id").GetGuid() == cliente.Id);
        Assert.Equal("Ana Gómez", ana.GetProperty("fullName").GetString());
        Assert.Equal("+573009876543", ana.GetProperty("phone").GetString());

        // Solo nombre y teléfono: nunca el correo, el documento ni el rol.
        Assert.Equal(["fullName", "id", "phone"], ana.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task UsuarioDeOtroTenantOInexistente_SeOmiteSinError()
    {
        var propio = AddUser(IdentityApiFactory.ActiveTenant, Roles.Cliente, "Ana Gómez", null);
        var ajeno = AddUser(IdentityApiFactory.InactiveTenant, Roles.Cliente, "Otra Persona", "3000000000");
        using var client = ClientAs(Roles.Tecnico);

        using var response = await client.GetAsync(Lookup(propio.Id, ajeno.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(propio.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("phone").ValueKind);
    }

    [Fact]
    public async Task SinIds_400ConErroresEnIds()
    {
        using var client = ClientAs(Roles.Tecnico);

        using var response = await client.GetAsync(Lookup());

        await AssertValidationAsync(response);
    }

    [Fact]
    public async Task MasDeVeinteIds_400()
    {
        using var client = ClientAs(Roles.Tecnico);

        using var response = await client.GetAsync(Lookup(Enumerable.Range(0, 21).Select(_ => (object)Guid.NewGuid()).ToArray()));

        await AssertValidationAsync(response);
    }

    [Fact]
    public async Task IdQueNoEsUuid_400()
    {
        using var client = ClientAs(Roles.Tecnico);

        using var response = await client.GetAsync(Lookup(Guid.NewGuid(), "no-es-uuid"));

        await AssertValidationAsync(response);
    }

    [Theory]
    [InlineData(Roles.Proveedor)]
    [InlineData(Roles.AdminPlataforma)]
    public async Task RolNoAdmitido_403(string role)
    {
        using var client = ClientAs(role);

        using var response = await client.GetAsync(Lookup(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Lookup(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://quickpatch.internal/problems/validacion", problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("ids", out _));
    }
}