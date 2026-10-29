using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Vencimiento del enlace y ventana del límite de la recuperación (contrato identity 1.10.0, SCRUM-625).</summary>
public class PasswordRecoveryTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryIdentityStore store = new();
    private readonly MutableClock clock = new(Start);

    public PasswordRecoveryTests() => store.Tenants.Add(new Tenant(TenantId, "Alfa", "900", Tenant.Active, Start));

    private RequestPasswordRecoveryHandler Request() =>
        new(store, store, store, store, clock, NullLogger<RequestPasswordRecoveryHandler>.Instance);

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
}