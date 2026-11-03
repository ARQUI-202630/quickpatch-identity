using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.Infrastructure.Security;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Vencimiento del enlace y ventana del límite de la recuperación (contrato identity 1.10.0, SCRUM-625).</summary>
public class PasswordRecoveryTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryIdentityStore store = new();
    private readonly MutableClock clock = new(Start);
    private readonly HmacEmailHasher emailHasher = Hasher(TestKeys.EmailHashKey);

    public PasswordRecoveryTests() => store.Tenants.Add(new Tenant(TenantId, "Alfa", "900", Tenant.Active, Start));

    private RequestPasswordRecoveryHandler Request() =>
        new(store, store, store, store, emailHasher, clock, NullLogger<RequestPasswordRecoveryHandler>.Instance);

    private static HmacEmailHasher Hasher(string key) =>
        new(Options.Create(new PasswordRecoveryOptions { EmailHashKey = key }));

    private IssueRecoveryLinkHandler Issue() => new(store, store, store, store, store, clock);

    private ConfirmPasswordRecoveryHandler Confirm() => new(store, store, store, store, store, new FakeHasher(), clock);

    private User Seed()
    {
        var user = User.Create(TenantId, $"r{Guid.NewGuid():N}@correo.co", "hash:Vieja2026", Roles.Cliente, "Ana", null, Start);
        store.Users.Add(user);
        return user;
    }

    private async Task<string> LinkAsync(User user)
    {
        await Request().HandleAsync(new RequestPasswordRecoveryCommand(TenantId, user.Email), CancellationToken.None);
        var request = store.Recoveries.Single(r => r.UserId == user.Id && r.IsPending);
        var issued = Assert.IsType<IssueRecoveryLinkResult.Issued>(
            await Issue().HandleAsync(new IssueRecoveryLinkCommand(TenantId, Guid.NewGuid(), request.Id, Guid.NewGuid()), CancellationToken.None));
        return issued.Link.Token;
    }

    [Fact]
    public async Task Enlace_VenceALaHora()
    {
        var user = Seed();
        var token = await LinkAsync(user);

        clock.Now = Start.AddHours(1);
        var result = await Confirm().HandleAsync(new ConfirmPasswordRecoveryCommand(TenantId, token, "Nueva2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.InvalidLink>(result);
        Assert.Equal("hash:Vieja2026", user.PasswordHash);
    }

    [Fact]
    public async Task Enlace_DeOtroTenant_NoSirve()
    {
        var user = Seed();
        var token = await LinkAsync(user);
        var otro = Guid.NewGuid();
        store.Tenants.Add(new Tenant(otro, "Beta", "901", Tenant.Active, Start));

        var result = await Confirm().HandleAsync(new ConfirmPasswordRecoveryCommand(otro, token, "Nueva2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.InvalidLink>(result);
    }

    [Fact]
    public async Task Limite_SeLiberaCuandoSaleDeLaVentanaElIntentoMasViejo()
    {
        var email = $"x{Guid.NewGuid():N}@correo.co";
        var handler = Request();
        for (var i = 0; i < 3; i++)
        {
            clock.Now = Start.AddMinutes(i * 10);
            Assert.IsType<RequestPasswordRecoveryResult.Accepted>(
                await handler.HandleAsync(new RequestPasswordRecoveryCommand(TenantId, email), CancellationToken.None));
        }

        clock.Now = Start.AddMinutes(30);
        var limited = Assert.IsType<RequestPasswordRecoveryResult.TooManyRequests>(
            await handler.HandleAsync(new RequestPasswordRecoveryCommand(TenantId, email), CancellationToken.None));
        Assert.Equal(Start.AddHours(1), limited.RetryAt);

        clock.Now = Start.AddHours(1).AddSeconds(1);
        Assert.IsType<RequestPasswordRecoveryResult.Accepted>(
            await handler.HandleAsync(new RequestPasswordRecoveryCommand(TenantId, email), CancellationToken.None));
    }

    [Fact]
    public async Task TenantInactivo_NoRegistraNada()
    {
        var inactivo = Guid.NewGuid();
        store.Tenants.Add(new Tenant(inactivo, "Delta", "902", Tenant.Inactive, Start));

        var result = await Request().HandleAsync(new RequestPasswordRecoveryCommand(inactivo, "a@correo.co"), CancellationToken.None);

        Assert.IsType<RequestPasswordRecoveryResult.TenantDisabled>(result);
        Assert.Empty(store.RecoveryAttempts);
    }

    [Fact]
    public async Task Pedido_GuardaElHmacDelCorreo_NoElSha256()
    {
        var email = $"h{Guid.NewGuid():N}@correo.co";

        await Request().HandleAsync(new RequestPasswordRecoveryCommand(TenantId, $" {email.ToUpperInvariant()} "), CancellationToken.None);

        var attempt = Assert.Single(store.RecoveryAttempts);
        Assert.Equal(emailHasher.Hash(email), attempt.EmailHash);
        Assert.NotEqual(PasswordSetupToken.Hash(email), attempt.EmailHash);
        Assert.Matches("^[0-9a-f]{64}$", attempt.EmailHash);
    }

    [Fact]
    public void HmacDelCorreo_DependeDeLaClave()
    {
        var otro = Hasher("otra-clave-distinta-de-la-de-pruebas-identity");

        Assert.Equal(emailHasher.Hash("a@correo.co"), emailHasher.Hash("a@correo.co"));
        Assert.NotEqual(emailHasher.Hash("a@correo.co"), otro.Hash("a@correo.co"));
        Assert.NotEqual(emailHasher.Hash("a@correo.co"), emailHasher.Hash("b@correo.co"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("clave-corta")]
    public void HmacDelCorreo_SinClaveValida_NoSeCrea(string key) =>
        Assert.Throws<InvalidOperationException>(() => Hasher(key));

    [Fact]
    public async Task TecnicoQueNuncaActivoLaInvitacion_AlRecuperar_QuedaConLaContrasenaDefinida()
    {
        var provider = User.Create(TenantId, $"p{Guid.NewGuid():N}@correo.co", "hash:x", Roles.Proveedor, "Proveedor", "3000000000", Start, "1111111111");
        var technician = User.Create(TenantId, $"t{Guid.NewGuid():N}@correo.co", "hash:aleatorio", Roles.Tecnico, "Técnico", "3000000001", Start, "2222222222");
        store.Users.AddRange(provider, technician);
        store.Profiles.Add(TechnicianProfile.CreatePendingInTeam(technician.Id, TenantId, provider.Id, Guid.NewGuid(), Start));
        var vencida = PasswordSetupToken.Issue(technician, Start.AddDays(-10), out _);
        var vigente = PasswordSetupToken.Issue(technician, Start, out var tokenInvitacion);
        store.Invitations.AddRange(vencida, vigente);
        var token = await LinkAsync(technician);

        clock.Now = Start.AddMinutes(5);
        var result = await Confirm().HandleAsync(new ConfirmPasswordRecoveryCommand(TenantId, token, "Nueva2026"), CancellationToken.None);

        Assert.IsType<SetupPasswordResult.Done>(result);
        Assert.Equal("hash:Nueva2026", technician.PasswordHash);
        Assert.Equal(clock.Now, vencida.UsedAt);
        Assert.Equal(clock.Now, vigente.UsedAt);

        // El proveedor ya no le puede generar otra invitación, y la que tenía no sirve para cambiar la contraseña.
        var renew = await new RenewInvitationHandler(store, store, store, store, clock)
            .HandleAsync(new RenewInvitationCommand(TenantId, provider.Id, technician.Id), CancellationToken.None);
        var setup = await new SetupPasswordHandler(store, store, store, store, new FakeHasher(), clock)
            .HandleAsync(new SetupPasswordCommand(TenantId, tokenInvitacion, "Otra2026"), CancellationToken.None);
        Assert.IsType<RenewInvitationResult.PasswordAlreadySet>(renew);
        Assert.IsNotType<SetupPasswordResult.Done>(setup);
        Assert.Equal("hash:Nueva2026", technician.PasswordHash);
    }

    [Fact]
    public void Invitacion_MarcarUsadaPorRecuperacion_NoCambiaLaFechaSiYaSeUso()
    {
        var user = Seed();
        var invitation = PasswordSetupToken.Issue(user, Start, out _);
        invitation.Consume(Start.AddMinutes(1));

        invitation.MarkUsedByRecovery(Start.AddMinutes(2));
        var recovery = PasswordSetupToken.IssueRecovery(user, Start, out _);

        Assert.Equal(Start.AddMinutes(1), invitation.UsedAt);
        Assert.Throws<InvalidOperationException>(() => recovery.MarkUsedByRecovery(Start));
    }
}