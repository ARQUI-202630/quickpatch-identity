using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Creación de tenants por el administrador de la plataforma (RF-21, contrato 1.5.0).</summary>
public class CreateTenantTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Tenants = new("/v1/platform/tenants", UriKind.Relative);

    private HttpClient ClientAs(string role, out Guid userId)
    {
        var user = User.Create(IdentityApiFactory.ActiveTenant, $"{role}@quickpatch.co", "hash", role, "Persona", null, Now);
        userId = user.Id;
        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string NewNit() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-7";

    [Fact]
    public async Task AdminPlataforma_CreaTenantActivoYLoAudita()
    {
        using var client = ClientAs(Roles.AdminPlataforma, out var adminId);
        var nit = NewNit();

        using var response = await client.PostAsJsonAsync(Tenants, new { name = "  Servicios Andinos S.A.S.  ", nit });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("Servicios Andinos S.A.S.", body.GetProperty("name").GetString());
        Assert.Equal(nit, body.GetProperty("nit").GetString());
        Assert.Equal("activo", body.GetProperty("status").GetString());
        Assert.Contains(factory.Store.Tenants, t => t.Id == id && t.IsActive);
        var auditoria = Assert.Single(factory.Store.Audit, a => a.TargetId == id);
        Assert.Equal(AuditEntry.CreateTenant, auditoria.Action);
        Assert.Equal(adminId, auditoria.ActorUserId);
        Assert.Null(auditoria.TenantId);

        using var lista = await client.GetAsync(Tenants);
        var ids = (await lista.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(t => t.GetProperty("id").GetGuid());
        Assert.Contains(id, ids);
    }

    [Fact]
    public async Task NitRepetido_409()
    {
        using var client = ClientAs(Roles.AdminPlataforma, out _);
        var nit = NewNit();
        using var primero = await client.PostAsJsonAsync(Tenants, new { name = "Primero", nit });

        using var segundo = await client.PostAsJsonAsync(Tenants, new { name = "Segundo", nit });

        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        Assert.EndsWith("nit-registrado", (await segundo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatosInvalidos_400ConCadaCampo()
    {
        using var client = ClientAs(Roles.AdminPlataforma, out _);

        using var response = await client.PostAsJsonAsync(Tenants, new { name = "A", nit = "90A12" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("nit", out _));
    }

    [Theory]
    [InlineData(Roles.AdminTenant)]
    [InlineData(Roles.Proveedor)]
    [InlineData(Roles.Cliente)]
    public async Task OtrosRoles_Reciben403(string role)
    {
        using var client = ClientAs(role, out _);

        using var response = await client.PostAsJsonAsync(Tenants, new { name = "Intruso", nit = NewNit() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("900123456", true)]
    [InlineData("900123456-7", true)]
    [InlineData("1234", false)]
    [InlineData("900.123.456-7", false)]
    [InlineData("900123456-78", false)]
    [InlineData(null, false)]
    public void Nit_SoloDigitosConVerificacionOpcional(string? nit, bool valido)
    {
        if (valido)
        {
            Assert.Equal(nit, Tenant.Create("Empresa", $" {nit} ", Now).Nit);
        }
        else
        {
            var ex = Assert.Throws<DomainValidationException>(() => Tenant.Create("Empresa", nit, Now));
            Assert.Equal("nit", Assert.Single(ex.Errors).Key);
        }
    }

    [Fact]
    public async Task CarreraConLaBase_TraduceLaUnicidadDelNit()
    {
        var store = new InMemoryIdentityStore { FailNextPlatformSaveWithDuplicateNit = true };
        var handler = new CreateTenantHandler(store, store, store, new MutableClock(Now));

        var result = await handler.HandleAsync(new CreateTenantCommand("Empresa", "900123456", Guid.NewGuid(), null), CancellationToken.None);

        Assert.IsType<CreateTenantResult.NitTaken>(result);
    }

    [Fact]
    public async Task ComandoNulo_LanzaExcepcion()
    {
        var store = new InMemoryIdentityStore();
        var handler = new CreateTenantHandler(store, store, store, new MutableClock(Now));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}