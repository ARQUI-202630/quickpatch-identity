using System.Text.Json;

using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Equipo del proveedor (RF-16, DD 8.3.14, RN-TP3 y RN-TP4).</summary>
public class ProviderTeamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly FakeHasher hasher = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid specialty = Guid.NewGuid();
    private readonly User provider;

    public ProviderTeamTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
        provider = AddUser(tenant, Roles.Proveedor);
    }

    private User AddUser(Guid tenantId, string role)
    {
        var user = User.Create(tenantId, $"{role}{Guid.NewGuid():N}@correo.co", "hash:x", role, "Persona", "3000000000", Now, Guid.NewGuid().ToString("N")[..10]);
        store.Users.Add(user);
        return user;
    }

    private CreateTeamTechnicianHandler Create() => new(store, store, store, store, store, hasher, new MutableClock(Now));

    private ListTeamHandler List() => new(store, store);

    private CreateTeamTechnicianCommand Command(Guid? providerId = null, string email = "carlos@correo.co", string document = "1020304050") =>
        new(tenant, providerId ?? provider.Id, email, "Carlos Pérez", "3001234567", document, specialty, Guid.NewGuid());

    [Fact]
    public async Task Alta_CreaTecnicoPendienteEnElEquipoDelProveedor()
    {
        var result = await Create().HandleAsync(Command(), CancellationToken.None);

        var ok = Assert.IsType<CreateTeamTechnicianResult.Created>(result);
        Assert.Equal(Roles.Tecnico, ok.User.Role);
        Assert.Equal(tenant, ok.User.TenantId);
        Assert.Equal(provider.Id, ok.Profile.ProviderId);
        Assert.Equal(tenant, ok.Profile.TenantId);
        Assert.Equal(VerificationStatus.Pendiente, ok.Profile.VerificationStatus);
        Assert.Equal(specialty, ok.Profile.SpecialtyId);
    }

    [Fact]
    public async Task Alta_TecnicoSinContrasenaUtilizable()
    {
        var first = Assert.IsType<CreateTeamTechnicianResult.Created>(await Create().HandleAsync(Command(), CancellationToken.None));
        var second = Assert.IsType<CreateTeamTechnicianResult.Created>(
            await Create().HandleAsync(Command(email: "otro@correo.co", document: "5550001112"), CancellationToken.None));

        // El hash es de un secreto aleatorio distinto en cada alta: nadie conoce la contraseña.
        Assert.StartsWith("hash:", first.User.PasswordHash, StringComparison.Ordinal);
        Assert.NotEqual(first.User.PasswordHash, second.User.PasswordHash);
        Assert.True(first.User.PasswordHash.Length > "hash:".Length + 40);
    }

    [Fact]
    public async Task Alta_EncolaTechnicianRegisteredConProviderId()
    {
        var command = Command();
        var ok = Assert.IsType<CreateTeamTechnicianResult.Created>(await Create().HandleAsync(command, CancellationToken.None));

        var message = Assert.Single(store.Events);
        Assert.Equal("technician.registered", message.EventType);
        Assert.Equal(tenant, message.TenantId);
        using var json = JsonDocument.Parse(message.Payload);
        Assert.Equal(command.CorrelationId, json.RootElement.GetProperty("correlationId").GetGuid());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(ok.User.Id, data.GetProperty("technicianId").GetGuid());
        Assert.Equal(provider.Id, data.GetProperty("providerId").GetGuid());
        Assert.Equal(VerificationStatus.Pendiente, data.GetProperty("verificationStatus").GetString());
    }

    [Fact]
    public async Task Alta_SinCorrelacion_GeneraUna()
    {
        await Create().HandleAsync(Command() with { CorrelationId = Guid.Empty }, CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(store.Events).Payload);
        Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task Alta_UsuarioQueNoEsProveedor_NoCrea()
    {
        var tecnico = AddUser(tenant, Roles.Tecnico);

        var result = await Create().HandleAsync(Command(providerId: tecnico.Id), CancellationToken.None);

        Assert.IsType<CreateTeamTechnicianResult.NotProvider>(result);
        Assert.Empty(store.Profiles);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task Alta_ProveedorDeOtroTenant_NoLoEncuentra()
    {
        var otroTenant = Guid.NewGuid();
        store.Tenants.Add(new Tenant(otroTenant, "Beta", "901", Tenant.Active, Now));
        var ajeno = AddUser(otroTenant, Roles.Proveedor);

        var result = await Create().HandleAsync(Command(providerId: ajeno.Id), CancellationToken.None);

        Assert.IsType<CreateTeamTechnicianResult.NotProvider>(result);
        Assert.Empty(store.Profiles);
    }

    [Fact]
    public async Task Alta_TenantInactivo_NoCrea()
    {
        store.Tenants.Clear();
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Inactive, Now));

        var result = await Create().HandleAsync(Command(), CancellationToken.None);

        Assert.IsType<CreateTeamTechnicianResult.TenantDisabled>(result);
        Assert.Empty(store.Profiles);
    }

    [Fact]
    public async Task Alta_CorreoODocumentoRepetidos_409()
    {
        await Create().HandleAsync(Command(), CancellationToken.None);

        var correo = await Create().HandleAsync(Command(email: "CARLOS@correo.co", document: "9998887776"), CancellationToken.None);
        var documento = await Create().HandleAsync(Command(email: "nuevo@correo.co"), CancellationToken.None);

        Assert.IsType<CreateTeamTechnicianResult.EmailTaken>(correo);
        Assert.IsType<CreateTeamTechnicianResult.DocumentTaken>(documento);
        Assert.Single(store.Profiles);
    }

    [Fact]
    public async Task Alta_CarreraConLaBase_TraduceLaUnicidad()
    {
        store.FailNextSaveWithDuplicate = true;
        var correo = await Create().HandleAsync(Command(), CancellationToken.None);
        store.FailNextSaveWithDuplicateDocument = true;
        var documento = await Create().HandleAsync(Command(email: "otro@correo.co", document: "1112223334"), CancellationToken.None);

        Assert.IsType<CreateTeamTechnicianResult.EmailTaken>(correo);
        Assert.IsType<CreateTeamTechnicianResult.DocumentTaken>(documento);
    }

    [Fact]
    public async Task Alta_DatosInvalidos_LanzaValidacionConCadaCampo()
    {
        var command = new CreateTeamTechnicianCommand(tenant, provider.Id, "no-es-correo", "C", "123", "12", null, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Create().HandleAsync(command, CancellationToken.None));

        Assert.Equal(["documentId", "email", "fullName", "phone", "specialtyId"], ex.Errors.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(store.Users, u => u.Role == Roles.Tecnico);
    }

    [Fact]
    public async Task Listar_SoloElEquipoPropioYPorEstado()
    {
        var a = Assert.IsType<CreateTeamTechnicianResult.Created>(await Create().HandleAsync(Command(), CancellationToken.None));
        var b = Assert.IsType<CreateTeamTechnicianResult.Created>(
            await Create().HandleAsync(Command(email: "b@correo.co", document: "2223334445"), CancellationToken.None));
        a.Profile.TryApprove();

        // Técnico independiente y técnico de otro proveedor del mismo tenant: no aparecen (RN-TP4).
        var otroProveedor = AddUser(tenant, Roles.Proveedor);
        await Create().HandleAsync(Command(providerId: otroProveedor.Id, email: "c@correo.co", document: "3334445556"), CancellationToken.None);
        store.Profiles.Add(TechnicianProfile.CreatePending(AddUser(tenant, Roles.Tecnico).Id, tenant, specialty, Now));

        var todos = await List().HandleAsync(new ListTeamQuery(tenant, provider.Id, null, 1, 20), CancellationToken.None);
        var activos = await List().HandleAsync(new ListTeamQuery(tenant, provider.Id, "activo", 1, 20), CancellationToken.None);
        var inactivos = await List().HandleAsync(new ListTeamQuery(tenant, provider.Id, "inactivo", 1, 20), CancellationToken.None);

        Assert.Equal([a.User.Id, b.User.Id], todos.Items.Select(x => x.UserId));
        Assert.Equal(2, todos.TotalItems);
        var activo = Assert.Single(activos.Items);
        Assert.Equal(a.User.Id, activo.UserId);
        Assert.Equal(VerificationStatus.Aprobado, activo.VerificationStatus);
        Assert.Equal("Carlos Pérez", activo.FullName);
        Assert.Equal(specialty, activo.SpecialtyId);
        Assert.Equal(b.User.Id, Assert.Single(inactivos.Items).UserId);
    }

    [Theory]
    [InlineData("aprobado", 1, 20, "status")]
    [InlineData(null, 0, 20, "page")]
    [InlineData(null, 1, 101, "size")]
    public async Task Listar_ParametrosInvalidos_LanzaValidacion(string? status, int page, int size, string campo)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => List().HandleAsync(new ListTeamQuery(tenant, provider.Id, status, page, size), CancellationToken.None));

        Assert.Equal(campo, Assert.Single(ex.Errors).Key);
    }

    [Fact]
    public async Task ComandosNulos_LanzanExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create().HandleAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => List().HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void PerfilEnEquipo_SinProveedor_Lanza()
    {
        Assert.Throws<ArgumentException>(() => TechnicianProfile.CreatePendingInTeam(Guid.NewGuid(), tenant, Guid.Empty, specialty, Now));
    }
}