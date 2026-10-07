using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

public class TechnicianRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly FakeHasher hasher = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid specialty = Guid.NewGuid();

    public TechnicianRegistrationTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
    }

    private RegisterTechnicianHandler Handler() => new(store, store, store, store, hasher, new MutableClock(Now));

    private RegisterTechnicianCommand Command(string email = "carlos@correo.co", string document = "1020304050", string role = Roles.Tecnico, Guid? tenantId = null) =>
        new(tenantId ?? tenant, email, "Segura123", "Carlos Pérez", "3001234567", document, specialty, role);

    [Theory]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.Proveedor)]
    public async Task Registrar_CreaUsuarioYPerfilPendiente(string role)
    {
        var result = await Handler().HandleAsync(Command(role: role), CancellationToken.None);

        var ok = Assert.IsType<RegisterTechnicianResult.Registered>(result);
        Assert.Equal(role, ok.User.Role);
        Assert.Equal("1020304050", ok.User.DocumentId);
        Assert.Equal(VerificationStatus.Pendiente, ok.Profile.VerificationStatus);
        Assert.Equal(specialty, ok.Profile.SpecialtyId);
        Assert.Equal(ok.User.Id, ok.Profile.UserId);
        Assert.Null(ok.Profile.ProviderId);
        Assert.Single(store.Profiles);
    }

    [Fact]
    public async Task Registrar_DocumentoRepetido_EsConflicto()
    {
        await Handler().HandleAsync(Command(), CancellationToken.None);

        var result = await Handler().HandleAsync(Command(email: "otro@correo.co"), CancellationToken.None);

        Assert.IsType<RegisterTechnicianResult.DocumentTaken>(result);
    }

    [Fact]
    public async Task Registrar_CorreoRepetido_EsConflicto()
    {
        await Handler().HandleAsync(Command(), CancellationToken.None);

        var result = await Handler().HandleAsync(Command(document: "99999999"), CancellationToken.None);

        Assert.IsType<RegisterTechnicianResult.EmailTaken>(result);
    }

    [Fact]
    public async Task Registrar_CarrerasConLaBase_SonConflictos()
    {
        store.FailNextSaveWithDuplicate = true;
        var porCorreo = await Handler().HandleAsync(Command(), CancellationToken.None);
        store.FailNextSaveWithDuplicateDocument = true;
        var porDocumento = await Handler().HandleAsync(Command(email: "b@correo.co", document: "88888888"), CancellationToken.None);

        Assert.IsType<RegisterTechnicianResult.EmailTaken>(porCorreo);
        Assert.IsType<RegisterTechnicianResult.DocumentTaken>(porDocumento);
    }

    [Fact]
    public async Task Registrar_TenantInactivo_NoRegistra()
    {
        var inactivo = Guid.NewGuid();
        store.Tenants.Add(new Tenant(inactivo, "Beta", "901", Tenant.Inactive, Now));

        var tecnico = await Handler().HandleAsync(Command(tenantId: inactivo), CancellationToken.None);
        var cliente = await new RegisterClientHandler(store, store, store, hasher, new MutableClock(Now))
            .HandleAsync(new RegisterClientCommand(inactivo, "ana@correo.co", "Segura123", "Ana", null), CancellationToken.None);

        Assert.IsType<RegisterTechnicianResult.TenantDisabled>(tecnico);
        Assert.IsType<RegisterClientResult.TenantDisabled>(cliente);
        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task Registrar_DatosInvalidos_ReportaCadaCampo()
    {
        var command = new RegisterTechnicianCommand(tenant, "malo", "corta", "C", null, "12", Guid.Empty, "cliente");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Handler().HandleAsync(command, CancellationToken.None));

        Assert.Equal(["documentId", "email", "fullName", "password", "phone", "role", "specialtyId"], ex.Errors.Keys.Order(StringComparer.Ordinal));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task PerfilPropio_DeTecnico_IncluyeVerificacion_YDeClienteNo()
    {
        var tecnico = (RegisterTechnicianResult.Registered)await Handler().HandleAsync(Command(), CancellationToken.None);
        var cliente = (RegisterClientResult.Registered)await new RegisterClientHandler(store, store, store, hasher, new MutableClock(Now))
            .HandleAsync(new RegisterClientCommand(tenant, "ana@correo.co", "Segura123", "Ana", null), CancellationToken.None);
        var handler = new GetCurrentUserHandler(store, store, store);

        var deTecnico = await handler.HandleAsync(tenant, tecnico.User.Id, CancellationToken.None);
        var deCliente = await handler.HandleAsync(tenant, cliente.User.Id, CancellationToken.None);

        Assert.Equal(VerificationStatus.Pendiente, deTecnico!.Profile!.VerificationStatus);
        Assert.Null(deCliente!.Profile);
    }

    [Fact]
    public void DuplicateDocumentException_ConstructoresEstandar()
    {
        Assert.Equal("x", new DuplicateDocumentException("x").Message);
        Assert.NotNull(new DuplicateDocumentException("x", new InvalidOperationException()).InnerException);
        Assert.NotEmpty(new DuplicateDocumentException().Message);
    }
}