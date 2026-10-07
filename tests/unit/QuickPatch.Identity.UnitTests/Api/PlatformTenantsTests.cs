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

/// <summary>Gestión de tenants por el administrador de la plataforma (RF-21, DD 10.4, contrato 1.2.0).</summary>
public class PlatformTenantsTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private HttpClient ClientAs(string role, Guid? tenant = null)
    {
        var user = User.Create(tenant ?? IdentityApiFactory.ActiveTenant, $"{role}@quickpatch.co", "hash", role, "Persona", null, Now);
        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Tenant NewTenant(string status = Tenant.Active)
    {
        var tenant = new Tenant(Guid.NewGuid(), $"Gamma {Guid.NewGuid():N}", Guid.NewGuid().ToString("N")[..10], status, Now);
        factory.Store.Tenants.Add(tenant);
        return tenant;
    }

    private static Uri TenantUri(Guid id) => new($"/v1/platform/tenants/{id}", UriKind.Relative);

    private static readonly Uri Tenants = new("/v1/platform/tenants", UriKind.Relative);

    [Fact]
    public async Task AdminPlataforma_ListaTodosLosTenants()
    {
        using var client = ClientAs(Roles.AdminPlataforma);

        using var response = await client.GetAsync(Tenants);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lista = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = lista.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(IdentityApiFactory.ActiveTenant, ids);
        Assert.Contains(IdentityApiFactory.InactiveTenant, ids);
        var primero = lista.EnumerateArray().First();
        Assert.True(primero.TryGetProperty("nit", out _));
        Assert.True(primero.TryGetProperty("createdAt", out _));
    }

    [Theory]
    [InlineData(Roles.AdminTenant)]
    [InlineData(Roles.Cliente)]
    public async Task OtrosRoles_Reciben403(string role)
    {
        using var client = ClientAs(role);

        using var listar = await client.GetAsync(Tenants);
        using var cambiar = await client.PatchAsJsonAsync(TenantUri(IdentityApiFactory.InactiveTenant), new { status = "activo" });

        Assert.Equal(HttpStatusCode.Forbidden, listar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cambiar.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Tenants);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DesactivarYActivar_CambiaElEstadoYAudita()
    {
        var tenant = NewTenant();
        using var client = ClientAs(Roles.AdminPlataforma);

        using var desactivar = await client.PatchAsJsonAsync(TenantUri(tenant.Id), new { status = "inactivo" });
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);
        Assert.Equal("inactivo", (await desactivar.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        using var activar = await client.PatchAsJsonAsync(TenantUri(tenant.Id), new { status = "activo" });
        Assert.Equal(HttpStatusCode.OK, activar.StatusCode);

        var auditoria = factory.Store.Audit.Where(a => a.TargetId == tenant.Id).ToList();
        Assert.Equal([AuditEntry.DeactivateTenant, AuditEntry.ActivateTenant], auditoria.Select(a => a.Action));
        Assert.All(auditoria, a => Assert.Null(a.TenantId));
        Assert.Contains("\"anterior\":\"activo\"", auditoria[0].Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismoEstado_NoAuditaDeNuevo()
    {
        var tenant = NewTenant(Tenant.Inactive);
        using var client = ClientAs(Roles.AdminPlataforma);

        using var response = await client.PatchAsJsonAsync(TenantUri(tenant.Id), new { status = "inactivo" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(factory.Store.Audit, a => a.TargetId == tenant.Id);
    }

    [Fact]
    public async Task TenantDeLaPlataforma_NoSePuedeDesactivar()
    {
        using var client = ClientAs(Roles.AdminPlataforma);

        using var response = await client.PatchAsJsonAsync(TenantUri(IdentityApiFactory.ActiveTenant), new { status = "inactivo" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "https://quickpatch.internal/problems/tenant-plataforma",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task TenantInexistente_404_YEstadoInvalido_400()
    {
        using var client = ClientAs(Roles.AdminPlataforma);

        using var noExiste = await client.PatchAsJsonAsync(TenantUri(Guid.NewGuid()), new { status = "activo" });
        using var invalido = await client.PatchAsJsonAsync(TenantUri(IdentityApiFactory.InactiveTenant), new { status = "suspendido" });

        Assert.Equal(HttpStatusCode.NotFound, noExiste.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        Assert.True((await invalido.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("status", out _));
    }

    [Fact]
    public void Tenant_CambiaDeEstadoSoloConValoresDelContrato()
    {
        var tenant = new Tenant(Guid.NewGuid(), "Delta", "903", Tenant.Active, Now);

        Assert.False(tenant.ChangeStatus(Tenant.Active));
        Assert.True(tenant.ChangeStatus(Tenant.Inactive));
        Assert.False(tenant.IsActive);
        Assert.Throws<ArgumentException>(() => tenant.ChangeStatus("suspendido"));
    }

    [Fact]
    public async Task Handler_ValidaElComando()
    {
        var store = new InMemoryIdentityStore();
        var handler = new UpdateTenantStatusHandler(store, store, store, new MutableClock(Now));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<DomainValidationException>(() => handler.HandleAsync(
            new UpdateTenantStatusCommand(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null), CancellationToken.None));
        Assert.Equal(0, store.PlatformTransactions);
    }
}