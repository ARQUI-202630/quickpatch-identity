using System.Text.Json;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Aprobación, rechazo y suspensión de técnicos (RF-19, RF-20, DD 8.3.13).</summary>
public class TechnicianVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly MutableClock clock = new(Now);
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid otherTenant = Guid.NewGuid();
    private readonly Guid admin = Guid.NewGuid();

    public TechnicianVerificationTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
        store.Tenants.Add(new Tenant(otherTenant, "Beta", "901", Tenant.Active, Now));
    }

    private ChangeVerificationHandler Handler() => new(store, store, store, store, clock);

    private async Task<Guid> RegisterAsync(Guid? tenantId = null, string role = Roles.Tecnico)
    {
        clock.Now = clock.Now.AddMinutes(1);
        var handler = new RegisterTechnicianHandler(store, store, store, store, store, new FakeHasher(), clock);
        var result = await handler.HandleAsync(
            new RegisterTechnicianCommand(
                tenantId ?? tenant, $"t{Guid.NewGuid():N}@correo.co", "Segura123", "Carlos Pérez", "3001234567",
                Guid.NewGuid().ToString("N")[..10], Guid.NewGuid(), role),
            CancellationToken.None);
        store.Events.Clear();
        return Assert.IsType<RegisterTechnicianResult.Registered>(result).User.Id;
    }

    private Task<ChangeVerificationResult> ActAsync(Guid technician, VerificationAction action, string? reason = null, Guid? tenantId = null) =>
        Handler().HandleAsync(
            new ChangeVerificationCommand(tenantId ?? tenant, admin, technician, action, reason, Guid.NewGuid()),
            CancellationToken.None);

    private TechnicianProfile Profile(Guid id) => store.Profiles.Single(p => p.UserId == id);

    [Theory]
    [InlineData(VerificationStatus.Pendiente, VerificationAction.Approve, true)]
    [InlineData(VerificationStatus.Pendiente, VerificationAction.Reject, true)]
    [InlineData(VerificationStatus.Pendiente, VerificationAction.Suspend, false)]
    [InlineData(VerificationStatus.Aprobado, VerificationAction.Approve, false)]
    [InlineData(VerificationStatus.Aprobado, VerificationAction.Reject, false)]
    [InlineData(VerificationStatus.Aprobado, VerificationAction.Suspend, true)]
    [InlineData(VerificationStatus.Rechazado, VerificationAction.Approve, true)]
    [InlineData(VerificationStatus.Rechazado, VerificationAction.Reject, false)]
    [InlineData(VerificationStatus.Rechazado, VerificationAction.Suspend, false)]
    [InlineData(VerificationStatus.Suspendido, VerificationAction.Approve, true)]
    [InlineData(VerificationStatus.Suspendido, VerificationAction.Reject, false)]
    [InlineData(VerificationStatus.Suspendido, VerificationAction.Suspend, false)]
    public async Task Transiciones_SiguenLaTablaDelDd(string from, VerificationAction action, bool allowed)
    {
        var id = await RegisterAsync();
        await BringToAsync(id, from);
        store.Audit.Clear();
        store.Events.Clear();

        var result = await ActAsync(id, action, "Motivo suficiente");

        if (allowed)
        {
            Assert.IsType<ChangeVerificationResult.Changed>(result);
            Assert.Single(store.Audit);
            Assert.Single(store.Events);
        }
        else
        {
            Assert.IsType<ChangeVerificationResult.InvalidTransition>(result);
            Assert.Equal(from, Profile(id).VerificationStatus);
            Assert.Empty(store.Audit);
            Assert.Empty(store.Events);
        }
    }

    private async Task BringToAsync(Guid id, string status)
    {
        switch (status)
        {
            case VerificationStatus.Aprobado:
                await ActAsync(id, VerificationAction.Approve);
                break;
            case VerificationStatus.Rechazado:
                await ActAsync(id, VerificationAction.Reject, "Documento ilegible");
                break;
            case VerificationStatus.Suspendido:
                await ActAsync(id, VerificationAction.Approve);
                await ActAsync(id, VerificationAction.Suspend, "Reclamos repetidos");
                break;
        }

        Assert.Equal(status, Profile(id).VerificationStatus);
    }

    [Fact]
    public async Task Rechazar_GuardaMotivo_AuditaYPublicaElEvento()
    {
        var id = await RegisterAsync();
        var correlation = Guid.NewGuid();
        clock.Now = Now.AddHours(1);

        var result = await Handler().HandleAsync(
            new ChangeVerificationCommand(tenant, admin, id, VerificationAction.Reject, "  El documento no es legible.  ", correlation),
            CancellationToken.None);

        var profile = Assert.IsType<ChangeVerificationResult.Changed>(result).Profile;
        Assert.Equal(VerificationStatus.Rechazado, profile.VerificationStatus);
        Assert.Equal("El documento no es legible.", profile.VerificationReason);

        var entry = Assert.Single(store.Audit);
        Assert.Equal(AuditEntry.RejectTechnician, entry.Action);
        Assert.Equal(tenant, entry.TenantId);
        Assert.Equal(admin, entry.ActorUserId);
        Assert.Equal(id, entry.TargetId);
        Assert.Equal(clock.Now, entry.CreatedAt);
        using (var metadata = JsonDocument.Parse(entry.Metadata!))
        {
            Assert.Equal("pendiente", metadata.RootElement.GetProperty("anterior").GetString());
            Assert.Equal("rechazado", metadata.RootElement.GetProperty("nuevo").GetString());
            Assert.Equal(correlation, metadata.RootElement.GetProperty("correlationId").GetGuid());
        }

        var message = Assert.Single(store.Events);
        Assert.Equal("technician.verification-changed", message.EventType);
        Assert.Equal(id, message.AggregateId);
        using var json = JsonDocument.Parse(message.Payload);
        var root = json.RootElement;
        Assert.Equal(message.Id, root.GetProperty("eventId").GetGuid());
        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal(correlation, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("identity-service", root.GetProperty("producer").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(id, data.GetProperty("technicianId").GetGuid());
        Assert.Equal(profile.SpecialtyId, data.GetProperty("specialtyId").GetGuid());
        Assert.Equal("pendiente", data.GetProperty("previousStatus").GetString());
        Assert.Equal("rechazado", data.GetProperty("verificationStatus").GetString());
        Assert.Equal("El documento no es legible.", data.GetProperty("reason").GetString());
        Assert.Equal(clock.Now, data.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Aprobar_TrasSuspension_LimpiaElMotivo()
    {
        var id = await RegisterAsync();
        await ActAsync(id, VerificationAction.Approve);
        await ActAsync(id, VerificationAction.Suspend, "Reclamos repetidos");
        store.Events.Clear();

        await ActAsync(id, VerificationAction.Approve);

        Assert.Equal(VerificationStatus.Aprobado, Profile(id).VerificationStatus);
        Assert.Null(Profile(id).VerificationReason);
        using var json = JsonDocument.Parse(Assert.Single(store.Events).Payload);
        Assert.Equal("suspendido", json.RootElement.GetProperty("data").GetProperty("previousStatus").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").GetProperty("reason").ValueKind);
    }

    [Theory]
    [InlineData(VerificationAction.Reject, null)]
    [InlineData(VerificationAction.Reject, "   ")]
    [InlineData(VerificationAction.Suspend, "abcd")]
    public async Task MotivoAusenteOCorto_EsValidacion(VerificationAction action, string? reason)
    {
        var id = await RegisterAsync();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => ActAsync(id, action, reason));

        Assert.Contains("reason", error.Errors.Keys);
        Assert.Equal(VerificationStatus.Pendiente, Profile(id).VerificationStatus);
    }

    [Fact]
    public async Task MotivoDeMasDe500Caracteres_EsValidacion()
    {
        var id = await RegisterAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => ActAsync(id, VerificationAction.Reject, new string('x', 501)));
    }

    [Fact]
    public async Task TecnicoDeOtroTenant_NoExiste()
    {
        var ajeno = await RegisterAsync(otherTenant);

        var result = await ActAsync(ajeno, VerificationAction.Approve);

        Assert.IsType<ChangeVerificationResult.NotFound>(result);
        Assert.Equal(VerificationStatus.Pendiente, Profile(ajeno).VerificationStatus);
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task Listar_FiltraPorEstadoYTenant_YPagina()
    {
        var primero = await RegisterAsync();
        var segundo = await RegisterAsync(role: Roles.Proveedor);
        var tercero = await RegisterAsync();
        await RegisterAsync(otherTenant);
        await ActAsync(segundo, VerificationAction.Approve);
        var handler = new ListTechniciansHandler(store, store);

        var pendientes = await handler.HandleAsync(new ListTechniciansQuery(tenant, VerificationStatus.Pendiente, 1, 20), CancellationToken.None);
        var todos = await handler.HandleAsync(new ListTechniciansQuery(tenant, null, 2, 2), CancellationToken.None);

        Assert.Equal([primero, tercero], pendientes.Items.Select(x => x.UserId));
        Assert.Equal(2, pendientes.TotalItems);
        Assert.Equal(3, todos.TotalItems);
        Assert.Equal(tercero, Assert.Single(todos.Items).UserId);
        Assert.Equal(2, todos.Page);
    }

    [Theory]
    [InlineData("desconocido", 1, 20, "verificationStatus")]
    [InlineData(null, 0, 20, "page")]
    [InlineData(null, 1, 0, "size")]
    [InlineData(null, 1, 101, "size")]
    public async Task Listar_ParametrosInvalidos_EsValidacion(string? status, int page, int size, string field)
    {
        var handler = new ListTechniciansHandler(store, store);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            handler.HandleAsync(new ListTechniciansQuery(tenant, status, page, size), CancellationToken.None));

        Assert.Contains(field, error.Errors.Keys);
    }
}