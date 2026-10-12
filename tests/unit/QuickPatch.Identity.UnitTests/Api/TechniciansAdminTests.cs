using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Endpoints de verificación de técnicos (contrato identity 1.3.0, DD 8.3.13).</summary>
public class TechniciansAdminTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private HttpClient ClientAs(string role, Guid? tenant = null)
    {
        var user = User.Create(tenant ?? IdentityApiFactory.ActiveTenant, $"{role}{Guid.NewGuid():N}@quickpatch.co", "hash", role, "Persona", null, Now);
        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> RegisterAsync()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), new
        {
            email = $"t{Guid.NewGuid():N}@correo.co",
            password = "Segura123",
            fullName = "Carlos Pérez",
            phone = "3001234567",
            documentId = Guid.NewGuid().ToString("N")[..10],
            specialtyId = Guid.NewGuid(),
            role = "tecnico",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Uri Action(Guid id, string action) => new($"/v1/admin/technicians/{id}/{action}", UriKind.Relative);

    [Fact]
    public async Task AdminTenant_ListaPendientesConDocumentoYEspecialidad()
    {
        var id = await RegisterAsync();
        using var client = ClientAs(Roles.AdminTenant);

        using var response = await client.GetAsync(new Uri("/v1/admin/technicians?verificationStatus=pendiente&size=100", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(100, page.GetProperty("size").GetInt32());
        var item = page.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == id);
        Assert.Equal("pendiente", item.GetProperty("verificationStatus").GetString());
        Assert.Equal("tecnico", item.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("documentId").GetString()));
        Assert.True(item.TryGetProperty("specialtyId", out _));
        Assert.True(item.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Listar_EstadoInvalido_400()
    {
        using var client = ClientAs(Roles.AdminTenant);

        using var response = await client.GetAsync(new Uri("/v1/admin/technicians?verificationStatus=otro", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AprobarYSuspender_Responden200ConElEstadoNuevo()
    {
        var id = await RegisterAsync();
        using var client = ClientAs(Roles.AdminTenant);

        using var aprobar = await client.PostAsync(Action(id, "approve"), null);
        using var suspender = await client.PostAsJsonAsync(Action(id, "suspend"), new { reason = "Reclamos repetidos por incumplimiento." });

        Assert.Equal(HttpStatusCode.OK, aprobar.StatusCode);
        var aprobado = await aprobar.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aprobado", aprobado.GetProperty("verificationStatus").GetString());
        Assert.Equal(JsonValueKind.Null, aprobado.GetProperty("reason").ValueKind);
        Assert.Equal(HttpStatusCode.OK, suspender.StatusCode);
        var suspendido = await suspender.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, suspendido.GetProperty("userId").GetGuid());
        Assert.Equal("suspendido", suspendido.GetProperty("verificationStatus").GetString());
        Assert.Equal("Reclamos repetidos por incumplimiento.", suspendido.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SuspenderUnPendiente_409TransicionInvalida()
    {
        var id = await RegisterAsync();
        using var client = ClientAs(Roles.AdminTenant);

        using var response = await client.PostAsJsonAsync(Action(id, "suspend"), new { reason = "Motivo suficiente" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.EndsWith("/transicion-invalida", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RechazarSinMotivo_400()
    {
        var id = await RegisterAsync();
        using var client = ClientAs(Roles.AdminTenant);

        using var response = await client.PostAsJsonAsync(Action(id, "reject"), new { reason = "no" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VerificationStatus.Pendiente, factory.Store.Profiles.Single(p => p.UserId == id).VerificationStatus);
    }

    [Fact]
    public async Task TecnicoDeOtroTenant_404()
    {
        var id = await RegisterAsync();
        using var client = ClientAs(Roles.AdminTenant, Guid.NewGuid());

        using var response = await client.PostAsync(Action(id, "approve"), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.AdminPlataforma)]
    public async Task OtrosRoles_Reciben403(string role)
    {
        var id = await RegisterAsync();
        using var client = ClientAs(role);

        using var listar = await client.GetAsync(new Uri("/v1/admin/technicians", UriKind.Relative));
        using var aprobar = await client.PostAsync(Action(id, "approve"), null);

        Assert.Equal(HttpStatusCode.Forbidden, listar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, aprobar.StatusCode);
    }
}