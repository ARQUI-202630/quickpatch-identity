using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Invitaciones para definir la contraseña (DD 8.3.14, contrato 1.7.0).</summary>
public class PasswordSetupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly MutableClock clock = new(Now);
    private readonly Guid tenant = Guid.NewGuid();
    private readonly User provider;
    private readonly User technician;

    public PasswordSetupTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
        provider = User.Create(tenant, "p@correo.co", "hash:x", Roles.Proveedor, "Proveedor", "3000000000", Now, "1111111111");
        technician = User.Create(tenant, "t@correo.co", "hash:aleatorio", Roles.Tecnico, "Técnico", "3000000001", Now, "2222222222");
        store.Users.AddRange(provider, technician);
        store.Profiles.Add(TechnicianProfile.CreatePendingInTeam(technician.Id, tenant, provider.Id, Guid.NewGuid(), Now));
    }

    private SetupPasswordHandler Setup() => new(store, store, store, store, new FakeHasher(), clock);

    private RenewInvitationHandler Renew() => new(store, store, store, store, clock);

    private string Invite()
    {
        store.Invitations.Add(PasswordSetupToken.Issue(technician, clock.GetUtcNow(), out var token));
        return token;
    }

    [Fact]
    public async Task Definir_CambiaElHash_UsaLaInvitacion_YQuitaElBloqueo()
    {
        var token = Invite();
        technician.RegisterFailedAttempt(Now, new LockoutPolicy(1, TimeSpan.FromHours(1)));

        var result = await Setup().HandleAsync(new SetupPasswordCommand(tenant, $" {token} ", "MiClave2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.Done>(result);
        Assert.Equal("hash:MiClave2026", technician.PasswordHash);
        Assert.False(technician.IsLocked(Now));
        Assert.NotNull(Assert.Single(store.Invitations).UsedAt);
    }

    [Fact]
    public async Task Vencida_EnlaceInvalido()
    {
        var token = Invite();
        clock.Now = Now.Add(PasswordSetupToken.Lifetime);

        var result = await Setup().HandleAsync(new SetupPasswordCommand(tenant, token, "MiClave2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.InvalidLink>(result);
        Assert.Equal("hash:aleatorio", technician.PasswordHash);
    }

    [Fact]
    public async Task TokenDeOtroTenant_EnlaceInvalido()
    {
        var token = Invite();
        var otro = Guid.NewGuid();
        store.Tenants.Add(new Tenant(otro, "Beta", "901", Tenant.Active, Now));

        var result = await Setup().HandleAsync(new SetupPasswordCommand(otro, token, "MiClave2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.InvalidLink>(result);
    }

    [Fact]
    public async Task TenantInactivo_NoDefine()
    {
        var token = Invite();
        Assert.True(store.Tenants[0].ChangeStatus(Tenant.Inactive));

        var result = await Setup().HandleAsync(new SetupPasswordCommand(tenant, token, "MiClave2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.TenantDisabled>(result);
    }

    [Fact]
    public async Task Definir_AnulaLasDemasInvitaciones()
    {
        var primera = Invite();
        var segunda = Invite();

        await Setup().HandleAsync(new SetupPasswordCommand(tenant, segunda, "MiClave2026"), CancellationToken.None);
        var conLaPrimera = await Setup().HandleAsync(new SetupPasswordCommand(tenant, primera, "OtraClave2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.InvalidLink>(conLaPrimera);
        Assert.Equal("hash:MiClave2026", technician.PasswordHash);
    }

    [Fact]
    public async Task Renovar_AnulaLasPendientes_YDevuelveOtra()
    {
        var vieja = Invite();

        var result = Assert.IsType<RenewInvitationResult.Issued>(
            await Renew().HandleAsync(new RenewInvitationCommand(tenant, provider.Id, technician.Id), CancellationToken.None));

        Assert.NotEqual(vieja, result.Invitation.Token);
        Assert.Equal(Now.Add(PasswordSetupToken.Lifetime), result.Invitation.ExpiresAt);
        Assert.Single(store.Invitations, i => i.IsUsable(Now));
    }

    [Fact]
    public async Task Renovar_TecnicoDeOtroProveedorOInexistente_NoEstaEnElEquipo()
    {
        var ajeno = await Renew().HandleAsync(new RenewInvitationCommand(tenant, Guid.NewGuid(), technician.Id), CancellationToken.None);
        var inexistente = await Renew().HandleAsync(new RenewInvitationCommand(tenant, provider.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.IsType<RenewInvitationResult.NotInTeam>(ajeno);
        Assert.IsType<RenewInvitationResult.NotInTeam>(inexistente);
    }

    [Fact]
    public void Token_Base64UrlDe256Bits_YSoloSeGuardaElHash()
    {
        var invitation = PasswordSetupToken.Issue(technician, Now, out var token);

        Assert.Equal(43, token.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.Equal(64, invitation.TokenHash.Length);
        Assert.Equal(PasswordSetupToken.Hash(token), invitation.TokenHash);
        Assert.Throws<InvalidOperationException>(() =>
        {
            invitation.Consume(Now);
            invitation.Consume(Now);
        });
    }

    [Fact]
    public async Task ComandosNulos_LanzanExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Setup().HandleAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Renew().HandleAsync(null!, CancellationToken.None));
        Assert.Throws<DomainValidationException>(() => User.ValidatePassword(null));
    }
}