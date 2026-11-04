using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Cuenta corporativa en la API (contrato identity 1.8.0, DD 8.3.18).</summary>
public class CompanyApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly Uri RegisterCompany = new("/v1/auth/register/company", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);
    private static readonly Uri MyCompany = new("/v1/companies/me", UriKind.Relative);
    private static readonly Uri Users = new("/v1/companies/me/users", UriKind.Relative);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Nit() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-7";

    private static object Body(string? nit = null, string? email = null) => new
    {
        company = new { legalName = "Inversiones Andinas S.A.S.", nit = nit ?? Nit(), contactEmail = "mantenimiento@andinas.com.co", contactPhone = "6012345" },
        user = new { email = email ?? $"u{Guid.NewGuid():N}@andinas.com.co", password = "Segura123", fullName = "Laura Mora", phone = "3001112233" },
    };

    /// <summary>Registra una empresa e inicia sesión con su primer usuario.</summary>
    private async Task<(HttpClient Client, JsonElement Registered, string Token)> FounderAsync()
    {
        var email = $"u{Guid.NewGuid():N}@andinas.com.co";
        var client = factory.CreateClient();
        using var registro = await client.PostAsJsonAsync(RegisterCompany, Body(email: email));
        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);
        using var login = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await Json(login)).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, await Json(registro), token);
    }

    [Fact]
    public async Task Registro_201ConEmpresaYUsuario_YElLoginTraeCompanyIdEnPerfilYToken()
    {
        var (client, registered, token) = await FounderAsync();

        var company = registered.GetProperty("company");
        var user = registered.GetProperty("user");
        Assert.Equal(["contactEmail", "contactPhone", "createdAt", "id", "legalName", "nit"], company.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("empresa_contacto", user.GetProperty("role").GetString());
        Assert.Equal(company.GetProperty("id").GetGuid(), user.GetProperty("companyId").GetGuid());
        Assert.False(user.TryGetProperty("password", out _));

        var claims = new JsonWebTokenHandler().ReadJsonWebToken(token);
        Assert.Equal(company.GetProperty("id").GetString(), claims.GetClaim("company_id").Value);

        using var me = await client.GetAsync(new Uri("/v1/users/me", UriKind.Relative));
        Assert.Equal(company.GetProperty("id").GetGuid(), (await Json(me)).GetProperty("companyId").GetGuid());
        client.Dispose();
    }

    [Fact]
    public async Task Registro_NitRepetido_409NitRegistrado_YCorreoRepetido_409()
    {
        using var client = factory.CreateClient();
        var nit = Nit();
        var email = $"u{Guid.NewGuid():N}@andinas.com.co";
        using var primero = await client.PostAsJsonAsync(RegisterCompany, Body(nit, email));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        using var nitRepetido = await client.PostAsJsonAsync(RegisterCompany, Body(nit));
        using var correoRepetido = await client.PostAsJsonAsync(RegisterCompany, Body(email: email));

        Assert.Equal(HttpStatusCode.Conflict, nitRepetido.StatusCode);
        Assert.EndsWith("/nit-registrado", (await Json(nitRepetido)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, correoRepetido.StatusCode);
        Assert.EndsWith("/correo-registrado", (await Json(correoRepetido)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registro_Invalido_400ConErroresPorCampo_YSinCuerpo_400()
    {
        using var client = factory.CreateClient();

        using var invalido = await client.PostAsJsonAsync(RegisterCompany, new { company = new { legalName = "A", nit = "1" }, user = new { email = "x" } });
        using var vacio = await client.PostAsync(RegisterCompany, new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        var errors = (await Json(invalido)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("company.nit", out _));
        Assert.True(errors.TryGetProperty("user.password", out _));
        Assert.Equal(HttpStatusCode.BadRequest, vacio.StatusCode);
    }

    [Fact]
    public async Task Registro_CanalSinTenant_503()
    {
        // Con canales configurados, uno que no está en la lista no tiene tenant.
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Channel:Tenants:canal-alfa", IdentityApiFactory.ActiveTenant.ToString()));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Channel-Id", "canal-desconocido");

        using var response = await client.PostAsJsonAsync(RegisterCompany, Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Empresa_AgregarListarYRenovar()
    {
        var (client, registered, _) = await FounderAsync();

        using var empresa = await client.GetAsync(MyCompany);
        Assert.Equal(HttpStatusCode.OK, empresa.StatusCode);
        Assert.Equal(registered.GetProperty("company").GetProperty("id").GetGuid(), (await Json(empresa)).GetProperty("id").GetGuid());

        using var alta = await client.PostAsJsonAsync(Users, new { email = $"a{Guid.NewGuid():N}@andinas.com.co", fullName = "Andrés Peña" });
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creado = await Json(alta);
        Assert.Equal("empresa_contacto", creado.GetProperty("role").GetString());
        Assert.Equal(registered.GetProperty("company").GetProperty("id").GetGuid(), creado.GetProperty("companyId").GetGuid());
        Assert.Contains("#token=", creado.GetProperty("invitation").GetProperty("link").GetString(), StringComparison.Ordinal);
        var id = creado.GetProperty("id").GetGuid();

        using var lista = await client.GetAsync(Users);
        var page = await Json(lista);
        Assert.Equal(2, page.GetProperty("totalItems").GetInt32());
        var item = page.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == id);
        Assert.False(item.GetProperty("passwordDefined").GetBoolean());
        Assert.Equal(["createdAt", "email", "fullName", "passwordDefined", "phone", "userId"], item.EnumerateObject().Select(p => p.Name).Order());

        using var renovada = await client.PostAsync(new Uri($"/v1/companies/me/users/{id}/invitation", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, renovada.StatusCode);

        var fundador = registered.GetProperty("user").GetProperty("id").GetGuid();
        using var delFundador = await client.PostAsync(new Uri($"/v1/companies/me/users/{fundador}/invitation", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Conflict, delFundador.StatusCode);
        Assert.EndsWith("/contrasena-definida", (await Json(delFundador)).GetProperty("type").GetString(), StringComparison.Ordinal);

        using var ajeno = await client.PostAsync(new Uri($"/v1/companies/me/users/{Guid.NewGuid()}/invitation", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);

        using var repetido = await client.PostAsJsonAsync(Users, new { email = registered.GetProperty("user").GetProperty("email").GetString(), fullName = "Otra" });
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);

        using var invalido = await client.PostAsJsonAsync(Users, new { email = "malo", fullName = "A" });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        using var pagina = await client.GetAsync(new Uri("/v1/companies/me/users?size=500", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, pagina.StatusCode);
        client.Dispose();
    }

    [Fact]
    public async Task OtraEmpresa_NoVeNiRenuevaUsuariosAjenos()
    {
        var (uno, _, _) = await FounderAsync();
        var (dos, _, _) = await FounderAsync();
        using var alta = await uno.PostAsJsonAsync(Users, new { email = $"a{Guid.NewGuid():N}@andinas.com.co", fullName = "Andrés Peña" });
        var id = (await Json(alta)).GetProperty("id").GetGuid();

        using var lista = await dos.GetAsync(Users);
        using var renovar = await dos.PostAsync(new Uri($"/v1/companies/me/users/{id}/invitation", UriKind.Relative), null);

        Assert.Equal(1, (await Json(lista)).GetProperty("totalItems").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, renovar.StatusCode);
        uno.Dispose();
        dos.Dispose();
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.AdminTenant)]
    public async Task OtrosRoles_403(string role)
    {
        var user = User.Create(IdentityApiFactory.ActiveTenant, $"{role}{Guid.NewGuid():N}@correo.co", "hash", role, "Persona", null, DateTimeOffset.UtcNow);
        factory.Store.Users.Add(user);
        var token = factory.Services.GetRequiredService<ITokenIssuer>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Issue(user).AccessToken);

        using var empresa = await client.GetAsync(MyCompany);
        using var alta = await client.PostAsJsonAsync(Users, new { email = "x@andinas.com.co", fullName = "Andrés" });

        Assert.Equal(HttpStatusCode.Forbidden, empresa.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, alta.StatusCode);
        Assert.False(new JsonWebTokenHandler().ReadJsonWebToken(token.Issue(user).AccessToken).TryGetClaim("company_id", out _));
    }

    [Fact]
    public async Task EmpresaContactoQueYaNoExisteEnElTenant_403()
    {
        var fantasma = User.Create(IdentityApiFactory.ActiveTenant, $"f{Guid.NewGuid():N}@andinas.com.co", "hash", Roles.EmpresaContacto, "Fantasma", null, DateTimeOffset.UtcNow, companyId: Guid.NewGuid());
        var token = factory.Services.GetRequiredService<ITokenIssuer>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Issue(fantasma).AccessToken);

        using var empresa = await client.GetAsync(MyCompany);
        using var lista = await client.GetAsync(Users);
        using var alta = await client.PostAsJsonAsync(Users, new { email = "x@andinas.com.co", fullName = "Andrés" });

        Assert.Equal(HttpStatusCode.Forbidden, empresa.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lista.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, alta.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(MyCompany);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}