using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

public class IdentityHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly FakeHasher hasher = new();
    private readonly MutableClock clock = new(Now);
    private readonly Guid tenant = Guid.NewGuid();

    public IdentityHandlerTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
    }

    private RegisterClientHandler Register() => new(store, store, hasher, clock);

    private LoginHandler Login() =>
        new(store, store, store, hasher, new FakeTokens(), LockoutPolicy.Default, clock, NullLogger<LoginHandler>.Instance);

    private async Task<User> RegisteredAsync(string email = "ana@correo.co")
    {
        var result = await Register().HandleAsync(new RegisterClientCommand(tenant, email, "Segura123", "Ana Pérez", null), CancellationToken.None);
        return Assert.IsType<RegisterClientResult.Registered>(result).User;
    }

    [Fact]
    public async Task Registrar_CreaClienteConHash()
    {
        var user = await RegisteredAsync("Ana@Correo.co");

        Assert.Equal(Roles.Cliente, user.Role);
        Assert.Equal("ana@correo.co", user.Email);
        Assert.Equal(tenant, user.TenantId);
        Assert.NotEqual("Segura123", user.PasswordHash);
    }

    [Fact]
    public async Task Registrar_CorreoRepetidoEnElTenant_EsConflicto()
    {
        await RegisteredAsync();

        var result = await Register().HandleAsync(new RegisterClientCommand(tenant, "ANA@correo.co", "Segura123", "Otra", null), CancellationToken.None);

        Assert.IsType<RegisterClientResult.EmailTaken>(result);
        Assert.Single(store.Users);
    }

    [Fact]
    public async Task Registrar_MismoCorreoEnOtroTenant_EsPermitido()
    {
        await RegisteredAsync();
        var otro = Guid.NewGuid();

        var result = await Register().HandleAsync(new RegisterClientCommand(otro, "ana@correo.co", "Segura123", "Ana", null), CancellationToken.None);

        Assert.IsType<RegisterClientResult.Registered>(result);
    }

    [Fact]
    public async Task Registrar_CarreraConLaBase_EsConflicto()
    {
        store.FailNextSaveWithDuplicate = true;

        var result = await Register().HandleAsync(new RegisterClientCommand(tenant, "ana@correo.co", "Segura123", "Ana", null), CancellationToken.None);

        Assert.IsType<RegisterClientResult.EmailTaken>(result);
    }

    [Fact]
    public async Task Registrar_DatosInvalidos_LanzaValidacion()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Register().HandleAsync(new RegisterClientCommand(tenant, "malo", "corta", "A", null), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Register().HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Login_CredencialesCorrectas_EmiteTokenYReiniciaIntentos()
    {
        var user = await RegisteredAsync();
        await Login().HandleAsync(new LoginCommand(tenant, "ana@correo.co", "mala"), CancellationToken.None);

        var result = await Login().HandleAsync(new LoginCommand(tenant, " ANA@correo.co ", "Segura123"), CancellationToken.None);

        var ok = Assert.IsType<LoginResult.Succeeded>(result);
        Assert.Equal($"token-{user.Id}", ok.Token.AccessToken);
        Assert.Equal(0, user.FailedLoginAttempts);
    }

    [Fact]
    public async Task Login_CorreoInexistente_EsInvalidoYGastaElMismoTiempo()
    {
        var result = await Login().HandleAsync(new LoginCommand(tenant, "nadie@correo.co", "Segura123"), CancellationToken.None);

        Assert.IsType<LoginResult.InvalidCredentials>(result);
        Assert.Equal(1, hasher.Simulations);
    }

    [Fact]
    public async Task Login_UsuarioDeOtroTenant_NoSeEncuentra()
    {
        await RegisteredAsync();

        var result = await Login().HandleAsync(new LoginCommand(Guid.NewGuid(), "ana@correo.co", "Segura123"), CancellationToken.None);

        Assert.IsType<LoginResult.InvalidCredentials>(result);
    }

    [Fact]
    public async Task Login_QuintoFallo_Bloquea_YDuranteElBloqueoRechazaAunConClaveCorrecta()
    {
        var user = await RegisteredAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.IsType<LoginResult.InvalidCredentials>(await Login().HandleAsync(new LoginCommand(tenant, user.Email, "mala"), CancellationToken.None));
        }

        var quinto = await Login().HandleAsync(new LoginCommand(tenant, user.Email, "mala"), CancellationToken.None);
        clock.Now = Now.AddMinutes(10);
        var durante = await Login().HandleAsync(new LoginCommand(tenant, user.Email, "Segura123"), CancellationToken.None);
        clock.Now = Now.AddMinutes(16);
        var despues = await Login().HandleAsync(new LoginCommand(tenant, user.Email, "Segura123"), CancellationToken.None);

        Assert.Equal(Now.AddMinutes(15), Assert.IsType<LoginResult.Locked>(quinto).Until);
        Assert.IsType<LoginResult.Locked>(durante);
        Assert.IsType<LoginResult.Succeeded>(despues);
    }

    [Fact]
    public async Task Login_TenantInactivo_RechazaSoloConCredencialesValidas()
    {
        var inactivo = Guid.NewGuid();
        store.Tenants.Add(new Tenant(inactivo, "Beta", "901", Tenant.Inactive, Now));
        await Register().HandleAsync(new RegisterClientCommand(inactivo, "beto@correo.co", "Segura123", "Beto", null), CancellationToken.None);

        var conClave = await Login().HandleAsync(new LoginCommand(inactivo, "beto@correo.co", "Segura123"), CancellationToken.None);
        var sinClave = await Login().HandleAsync(new LoginCommand(inactivo, "beto@correo.co", "mala"), CancellationToken.None);

        Assert.IsType<LoginResult.TenantDisabled>(conClave);
        Assert.IsType<LoginResult.InvalidCredentials>(sinClave);
    }

    [Fact]
    public async Task Login_SinComando_Falla_YCorreoVacio_EsInvalido()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Login().HandleAsync(null!, CancellationToken.None));
        Assert.IsType<LoginResult.InvalidCredentials>(await Login().HandleAsync(new LoginCommand(tenant, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task PerfilPropio_SoloEnSuTenant()
    {
        var user = await RegisteredAsync();
        var handler = new GetCurrentUserHandler(store, store);

        Assert.NotNull(await handler.HandleAsync(tenant, user.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(Guid.NewGuid(), user.Id, CancellationToken.None));
    }

    [Fact]
    public void DuplicateEmailException_ConstructoresEstandar()
    {
        Assert.Equal("x", new DuplicateEmailException("x").Message);
        Assert.NotNull(new DuplicateEmailException("x", new InvalidOperationException()).InnerException);
    }
}