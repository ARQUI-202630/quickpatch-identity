using QuickPatch.Identity.Application.Companies;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Cuenta corporativa (RF-06, DD 5.19 y 8.3.18, RN-CC1 a RN-CC4).</summary>
public class CompanyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly FakeHasher hasher = new();
    private readonly MutableClock clock = new(Now);
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid otherTenant = Guid.NewGuid();
    private readonly Guid inactiveTenant = Guid.NewGuid();

    public CompanyTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
        store.Tenants.Add(new Tenant(otherTenant, "Gama", "902", Tenant.Active, Now));
        store.Tenants.Add(new Tenant(inactiveTenant, "Beta", "901", Tenant.Inactive, Now));
    }

    private RegisterCompanyHandler Register() => new(store, store, store, store, hasher, clock);

    private CreateCompanyUserHandler CreateUser() => new(store, store, store, store, hasher, clock);

    private ListCompanyUsersHandler ListUsers() => new(store, store, store);

    private RenewCompanyInvitationHandler Renew() => new(store, store, store, clock);

    private GetMyCompanyHandler MyCompany() => new(store, store, store);

    private static RegisterCompanyCommand Command(
        Guid tenantId, string nit = "900123456-7", string email = "laura@andinas.com.co", string? legalName = "Inversiones Andinas S.A.S.") =>
        new(tenantId, legalName, nit, "mantenimiento@andinas.com.co", "6012345", email, "Segura123", "Laura Mora", "3001112233");

    private async Task<(ClientCompany Company, User Founder)> RegisteredAsync(Guid? tenantId = null, string nit = "900123456-7")
    {
        var ok = Assert.IsType<RegisterCompanyResult.Registered>(
            await Register().HandleAsync(Command(tenantId ?? tenant, nit, $"f{Guid.NewGuid():N}@andinas.com.co"), CancellationToken.None));
        return (ok.Company, ok.User);
    }

    [Fact]
    public async Task Registro_CreaEmpresaYPrimerUsuarioEnElTenantDelCanal()
    {
        var result = await Register().HandleAsync(Command(tenant), CancellationToken.None);

        var ok = Assert.IsType<RegisterCompanyResult.Registered>(result);
        Assert.Equal(tenant, ok.Company.TenantId);
        Assert.Equal("Inversiones Andinas S.A.S.", ok.Company.LegalName);
        Assert.Equal("900123456-7", ok.Company.Nit);
        Assert.Equal(Roles.EmpresaContacto, ok.User.Role);
        Assert.Equal(ok.Company.Id, ok.User.CompanyId);
        Assert.Equal(tenant, ok.User.TenantId);
        Assert.Equal("hash:Segura123", ok.User.PasswordHash);
        Assert.Single(store.Companies);
        Assert.Empty(store.Invitations);
    }

    [Fact]
    public async Task Registro_NitRepetidoEnElTenant_409_PeroEnOtroTenantSiSePuede()
    {
        await RegisteredAsync();

        var repetido = await Register().HandleAsync(Command(tenant, email: "otra@andinas.com.co"), CancellationToken.None);
        var otroTenant = await Register().HandleAsync(Command(otherTenant, email: "otra@andinas.com.co"), CancellationToken.None);

        Assert.IsType<RegisterCompanyResult.NitTaken>(repetido);
        Assert.IsType<RegisterCompanyResult.Registered>(otroTenant);
    }

    [Fact]
    public async Task Registro_CorreoRepetido_409_SinCrearLaEmpresa()
    {
        store.Users.Add(User.Create(tenant, "laura@andinas.com.co", "hash:x", Roles.Cliente, "Laura", null, Now));

        var result = await Register().HandleAsync(Command(tenant), CancellationToken.None);

        Assert.IsType<RegisterCompanyResult.EmailTaken>(result);
        Assert.Empty(store.Companies);
    }

    [Fact]
    public async Task Registro_CarreraConElNit_EsNitTaken()
    {
        store.FailNextSaveWithDuplicateCompanyNit = true;

        var result = await Register().HandleAsync(Command(tenant), CancellationToken.None);

        Assert.IsType<RegisterCompanyResult.NitTaken>(result);
    }

    [Fact]
    public async Task Registro_CarreraConElCorreo_EsEmailTaken()
    {
        store.FailNextSaveWithDuplicate = true;

        var result = await Register().HandleAsync(Command(tenant), CancellationToken.None);

        Assert.IsType<RegisterCompanyResult.EmailTaken>(result);
    }

    [Fact]
    public async Task Registro_TenantInactivo_403()
    {
        var result = await Register().HandleAsync(Command(inactiveTenant), CancellationToken.None);

        Assert.IsType<RegisterCompanyResult.TenantDisabled>(result);
        Assert.Empty(store.Companies);
    }

    [Fact]
    public async Task Registro_ErroresDeEmpresaYUsuarioJuntosConPrefijo()
    {
        var command = new RegisterCompanyCommand(tenant, "A", "90-1", "no-es-correo", new string('1', 31), "malo", "corta", " ", null);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Register().HandleAsync(command, CancellationToken.None));

        Assert.Equal(
            ["company.contactEmail", "company.contactPhone", "company.legalName", "company.nit", "user.email", "user.fullName", "user.password"],
            ex.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("900123456")]
    [InlineData("900123456-7")]
    [InlineData("12345")]
    public void Nit_ConOSinDigitoDeVerificacion_EsValido(string nit) =>
        Assert.DoesNotContain("company.nit", ClientCompany.Validate("Andinas", nit, "a@andinas.co", null).Keys);

    [Theory]
    [InlineData("1234")]
    [InlineData("900.123.456-7")]
    [InlineData("900123456-78")]
    [InlineData("abcdefg")]
    public void Nit_ConOtroFormato_NoEsValido(string nit) =>
        Assert.Contains("company.nit", ClientCompany.Validate("Andinas", nit, "a@andinas.co", null).Keys);

    [Fact]
    public void Empresa_InvalidaNoSeCrea() =>
        Assert.Throws<DomainValidationException>(() => ClientCompany.Create(tenant, "A", "1", "x", null, Now));

    [Fact]
    public void Usuario_SoloElEmpresaContactoTieneEmpresa_RNCC2()
    {
        Assert.Throws<ArgumentException>(() => User.Create(tenant, "a@b.co", "h", Roles.EmpresaContacto, "Ana", null, Now));
        Assert.Throws<ArgumentException>(() => User.Create(tenant, "a@b.co", "h", Roles.Cliente, "Ana", null, Now, companyId: Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => User.Create(tenant, "a@b.co", "h", Roles.EmpresaContacto, "Ana", null, Now, companyId: Guid.Empty));
        Assert.Equal(Roles.EmpresaContacto, User.Create(tenant, "a@b.co", "h", Roles.EmpresaContacto, "Ana", null, Now, companyId: Guid.NewGuid()).Role);
    }

    [Fact]
    public async Task AgregarUsuario_HeredaTenantYEmpresaYQuedaConInvitacion()
    {
        var (company, founder) = await RegisteredAsync();

        var result = await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, founder.Id, "Andres@Andinas.com.co", "Andrés Peña", null), CancellationToken.None);

        var ok = Assert.IsType<CreateCompanyUserResult.Created>(result);
        Assert.Equal(Roles.EmpresaContacto, ok.User.Role);
        Assert.Equal(company.Id, ok.User.CompanyId);
        Assert.Equal(tenant, ok.User.TenantId);
        Assert.Equal("andres@andinas.com.co", ok.User.Email);
        Assert.Equal(Now.Add(PasswordSetupToken.Lifetime), ok.Invitation.ExpiresAt);
        var invitation = Assert.Single(store.Invitations);
        Assert.Equal(PasswordSetupToken.Hash(ok.Invitation.Token), invitation.TokenHash);

        // RN-CC4: sin contraseña utilizable hasta usar la invitación.
        Assert.False(hasher.Verify("Segura123", ok.User.PasswordHash));
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.Proveedor)]
    [InlineData(Roles.AdminTenant)]
    public async Task AgregarUsuario_QuienNoEsDeUnaEmpresa_NoAgrega(string role)
    {
        var actor = User.Create(tenant, $"{role}@correo.co", "hash:x", role, "Persona", null, Now, documentId: role == Roles.Proveedor ? "1020304050" : null);
        store.Users.Add(actor);

        var result = await CreateUser().HandleAsync(new CreateCompanyUserCommand(tenant, actor.Id, "nuevo@andinas.com.co", "Nuevo", null), CancellationToken.None);

        Assert.IsType<CreateCompanyUserResult.NotMember>(result);
    }

    [Fact]
    public async Task AgregarUsuario_ConElTokenEnOtroTenant_NoEncuentraAlUsuario()
    {
        var (_, founder) = await RegisteredAsync();

        var result = await CreateUser().HandleAsync(new CreateCompanyUserCommand(otherTenant, founder.Id, "nuevo@andinas.com.co", "Nuevo", null), CancellationToken.None);

        Assert.IsType<CreateCompanyUserResult.NotMember>(result);
    }

    [Fact]
    public async Task AgregarUsuario_CorreoRepetidoYTenantInactivoYCarrera()
    {
        var (_, founder) = await RegisteredAsync();

        var repetido = await CreateUser().HandleAsync(new CreateCompanyUserCommand(tenant, founder.Id, founder.Email, "Otra", null), CancellationToken.None);
        var inactivo = await CreateUser().HandleAsync(new CreateCompanyUserCommand(inactiveTenant, founder.Id, "x@andinas.com.co", "Otra", null), CancellationToken.None);
        store.FailNextSaveWithDuplicate = true;
        var carrera = await CreateUser().HandleAsync(new CreateCompanyUserCommand(tenant, founder.Id, "y@andinas.com.co", "Otra", null), CancellationToken.None);

        Assert.IsType<CreateCompanyUserResult.EmailTaken>(repetido);
        Assert.IsType<CreateCompanyUserResult.TenantDisabled>(inactivo);
        Assert.IsType<CreateCompanyUserResult.EmailTaken>(carrera);
    }

    [Fact]
    public async Task AgregarUsuario_DatosInvalidos_400PorCampo()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, Guid.NewGuid(), "malo", "A", new string('1', 31)), CancellationToken.None));

        Assert.Equal(["email", "fullName", "phone"], ex.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Listar_SoloLosDeLaEmpresa_ConLaContrasenaDefinidaONo()
    {
        var (_, founder) = await RegisteredAsync();
        var (_, otherFounder) = await RegisteredAsync(nit: "800111222-3");
        var invitado = Assert.IsType<CreateCompanyUserResult.Created>(await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, founder.Id, "andres@andinas.com.co", "Andrés", null), CancellationToken.None));

        var page = await ListUsers().HandleAsync(new ListCompanyUsersQuery(tenant, founder.Id, 1, 20), CancellationToken.None);
        var other = await ListUsers().HandleAsync(new ListCompanyUsersQuery(tenant, otherFounder.Id, 1, 20), CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalItems);
        Assert.True(page.Items.Single(u => u.UserId == founder.Id).PasswordDefined);
        Assert.False(page.Items.Single(u => u.UserId == invitado.User.Id).PasswordDefined);
        Assert.Equal(otherFounder.Id, Assert.Single(other!.Items).UserId);
    }

    [Fact]
    public async Task Listar_QuienNoEsDeUnaEmpresa_Null_YPaginaInvalida_400()
    {
        var cliente = User.Create(tenant, "c@correo.co", "hash:x", Roles.Cliente, "Cliente", null, Now);
        store.Users.Add(cliente);

        Assert.Null(await ListUsers().HandleAsync(new ListCompanyUsersQuery(tenant, cliente.Id, 1, 20), CancellationToken.None));
        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => ListUsers().HandleAsync(new ListCompanyUsersQuery(tenant, cliente.Id, 0, 101), CancellationToken.None));
        Assert.Equal(["page", "size"], ex.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Empresa_LaDelUsuario_YNadaParaOtroRol()
    {
        var (company, founder) = await RegisteredAsync();
        var cliente = User.Create(tenant, "c@correo.co", "hash:x", Roles.Cliente, "Cliente", null, Now);
        store.Users.Add(cliente);

        Assert.Equal(company.Id, (await MyCompany().HandleAsync(tenant, founder.Id, CancellationToken.None))!.Id);
        Assert.Null(await MyCompany().HandleAsync(tenant, cliente.Id, CancellationToken.None));
        Assert.Null(await MyCompany().HandleAsync(otherTenant, founder.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Renovar_AnulaLaAnteriorYEntregaOtra()
    {
        var (_, founder) = await RegisteredAsync();
        var invitado = Assert.IsType<CreateCompanyUserResult.Created>(await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, founder.Id, "andres@andinas.com.co", "Andrés", null), CancellationToken.None));
        clock.Now = Now.AddHours(1);

        var result = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, founder.Id, invitado.User.Id), CancellationToken.None);

        var ok = Assert.IsType<RenewInvitationResult.Issued>(result);
        Assert.NotEqual(invitado.Invitation.Token, ok.Invitation.Token);
        Assert.Single(store.Invitations, i => i.IsUsable(clock.Now));
    }

    [Fact]
    public async Task Renovar_ElFundadorNuncaRecibeInvitacion_NadieTomaSuCuenta()
    {
        var (_, founder) = await RegisteredAsync();
        var invitado = Assert.IsType<CreateCompanyUserResult.Created>(await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, founder.Id, "andres@andinas.com.co", "Andrés", null), CancellationToken.None));

        var result = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, invitado.User.Id, founder.Id), CancellationToken.None);

        Assert.IsType<RenewInvitationResult.PasswordAlreadySet>(result);
    }

    [Fact]
    public async Task Renovar_ConLaInvitacionYaUsada_409()
    {
        var (_, founder) = await RegisteredAsync();
        var invitado = Assert.IsType<CreateCompanyUserResult.Created>(await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, founder.Id, "andres@andinas.com.co", "Andrés", null), CancellationToken.None));
        var setup = new SetupPasswordHandler(store, store, store, store, hasher, clock);
        Assert.IsType<SetupPasswordResult.Done>(
            await setup.HandleAsync(new SetupPasswordCommand(tenant, invitado.Invitation.Token, "MiClave2026"), CancellationToken.None));

        var result = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, founder.Id, invitado.User.Id), CancellationToken.None);

        Assert.IsType<RenewInvitationResult.PasswordAlreadySet>(result);
        Assert.Equal("hash:MiClave2026", store.Users.Single(u => u.Id == invitado.User.Id).PasswordHash);
    }

    [Fact]
    public async Task Renovar_UsuarioDeOtraEmpresaOInexistenteOActorAjeno_404()
    {
        var (_, founder) = await RegisteredAsync();
        var (_, otherFounder) = await RegisteredAsync(nit: "800111222-3");
        var ajeno = Assert.IsType<CreateCompanyUserResult.Created>(await CreateUser().HandleAsync(
            new CreateCompanyUserCommand(tenant, otherFounder.Id, "ajeno@otra.com.co", "Ajeno", null), CancellationToken.None));
        var cliente = User.Create(tenant, "c@correo.co", "hash:x", Roles.Cliente, "Cliente", null, Now);
        store.Users.Add(cliente);

        var otraEmpresa = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, founder.Id, ajeno.User.Id), CancellationToken.None);
        var inexistente = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, founder.Id, Guid.NewGuid()), CancellationToken.None);
        var actorCliente = await Renew().HandleAsync(new RenewCompanyInvitationCommand(tenant, cliente.Id, ajeno.User.Id), CancellationToken.None);

        Assert.IsType<RenewInvitationResult.NotInTeam>(otraEmpresa);
        Assert.IsType<RenewInvitationResult.NotInTeam>(inexistente);
        Assert.IsType<RenewInvitationResult.NotInTeam>(actorCliente);
    }
}