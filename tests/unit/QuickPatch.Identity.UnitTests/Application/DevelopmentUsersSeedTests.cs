using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Identity.Api.Bootstrap;
using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.UnitTests.Api;
using QuickPatch.Identity.UnitTests.Support;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuickPatch.Identity.UnitTests.Application;

public class DevelopmentUsersSeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly Guid tenant = Guid.NewGuid();

    public DevelopmentUsersSeedTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
    }

    private DevelopmentUsersSeed Seed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<ITenantRepository>(store);
        services.AddSingleton<IUserRepository>(store);
        services.AddSingleton<ITechnicianProfileRepository>(store);
        services.AddSingleton<IOutbox>(store);
        services.AddSingleton<IPasswordHasher>(new FakeHasher());
        services.AddSingleton<TimeProvider>(new MutableClock(Now));
        services.AddScoped<EnsureTenantAdminHandler>();
        services.AddScoped<RegisterClientHandler>();
        services.AddScoped<RegisterTechnicianHandler>();
        var provider = services.BuildServiceProvider();
        return new DevelopmentUsersSeed(
            provider.GetRequiredService<IServiceScopeFactory>(),
            MsOptions.Create(new ChannelOptions { TenantId = tenant }),
            NullLogger<DevelopmentUsersSeed>.Instance);
    }

    [Fact]
    public async Task CreaUnUsuarioPorRolEnElTenantDelCanal()
    {
        var seed = Seed();
        await seed.StartAsync(CancellationToken.None);
        await seed.StopAsync(CancellationToken.None);

        Assert.Equal(
            [Roles.Cliente, Roles.Tecnico, Roles.Proveedor, Roles.AdminTenant, Roles.AdminPlataforma],
            store.Users.Select(u => u.Role));
        Assert.All(store.Users, u =>
        {
            Assert.Equal(tenant, u.TenantId);
            Assert.Equal($"hash:{DevelopmentUsers.Password}", u.PasswordHash);
            Assert.EndsWith("@quickpatch.dev", u.Email, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TecnicoYProveedorQuedanConPerfilPendiente()
    {
        await Seed().StartAsync(CancellationToken.None);

        Assert.Equal(2, store.Profiles.Count);
        Assert.All(store.Profiles, p =>
        {
            Assert.Equal("pendiente", p.VerificationStatus);
            Assert.Equal(DevelopmentUsers.SpecialtyId, p.SpecialtyId);
        });
    }

    [Fact]
    public async Task EsIdempotente()
    {
        await Seed().StartAsync(CancellationToken.None);
        await Seed().StartAsync(CancellationToken.None);

        Assert.Equal(DevelopmentUsers.All.Count, store.Users.Count);
    }

    [Fact]
    public async Task UnFalloNoDetieneElArranque()
    {
        store.Tenants.Clear();
        store.FailNextSaveWithDuplicateDocument = true;

        await Seed().StartAsync(CancellationToken.None);

        Assert.Empty(store.Users);
    }

    [Fact]
    public void LosDatosDeDesarrolloPasanLaValidacionDelRegistro()
    {
        foreach (var usuario in DevelopmentUsers.All)
        {
            User.ValidateRegistration(usuario.Email, DevelopmentUsers.Password, usuario.FullName, usuario.Phone);
        }
    }

    [Fact]
    public void FueraDeDevelopmentNoSeRegistraElSembrado()
    {
        using var app = new IdentityApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("DevelopmentUsers:Enabled", "true");
        });

        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is DevelopmentUsersSeed);
    }

    [Fact]
    public void EnDevelopmentConLaBanderaSeRegistra()
    {
        using var app = new IdentityApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.UseSetting("DevelopmentUsers:Enabled", "true");
        });

        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is DevelopmentUsersSeed);
    }
}