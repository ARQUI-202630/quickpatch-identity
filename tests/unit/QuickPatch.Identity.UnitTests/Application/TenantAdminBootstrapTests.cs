using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Identity.Api.Bootstrap;
using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.UnitTests.Support;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuickPatch.Identity.UnitTests.Application;

public class TenantAdminBootstrapTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly FakeHasher hasher = new();
    private readonly Guid tenant = Guid.NewGuid();

    public TenantAdminBootstrapTests()
    {
        store.Tenants.Add(new Tenant(tenant, "Alfa", "900", Tenant.Active, Now));
    }

    private EnsureTenantAdminHandler Handler() => new(store, store, store, hasher, new MutableClock(Now));

    private TenantAdminBootstrap Bootstrap(BootstrapOptions datos)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<ITenantRepository>(store);
        services.AddSingleton<IUserRepository>(store);
        services.AddSingleton<IPasswordHasher>(hasher);
        services.AddSingleton<TimeProvider>(new MutableClock(Now));
        services.AddScoped<EnsureTenantAdminHandler>();
        var provider = services.BuildServiceProvider();
        return new TenantAdminBootstrap(
            provider.GetRequiredService<IServiceScopeFactory>(),
            MsOptions.Create(datos),
            MsOptions.Create(new ChannelOptions { TenantId = tenant }),
            NullLogger<TenantAdminBootstrap>.Instance);
    }

    [Fact]
    public async Task CreaElAdministradorDelTenantConHash()
    {
        var resultado = await Handler().HandleAsync(
            new EnsureTenantAdminCommand(tenant, " Admin@QuickPatch.co ", "Segura123", "Admin QA"), CancellationToken.None);

        Assert.Equal(EnsureTenantAdminResult.Created, resultado);
        var admin = Assert.Single(store.Users);
        Assert.Equal(Roles.AdminTenant, admin.Role);
        Assert.Equal("admin@quickpatch.co", admin.Email);
        Assert.Equal("hash:Segura123", admin.PasswordHash);
        Assert.Equal(tenant, admin.TenantId);
    }

    [Fact]
    public async Task EsIdempotente()
    {
        var comando = new EnsureTenantAdminCommand(tenant, "admin@quickpatch.co", "Segura123", "Admin QA");
        await Handler().HandleAsync(comando, CancellationToken.None);

        Assert.Equal(EnsureTenantAdminResult.AlreadyExists, await Handler().HandleAsync(comando, CancellationToken.None));
        Assert.Single(store.Users);
    }

    [Fact]
    public async Task CorreoDuplicadoEnLaBase_SeTrataComoExistente()
    {
        store.FailNextSaveWithDuplicate = true;

        var resultado = await Handler().HandleAsync(
            new EnsureTenantAdminCommand(tenant, "admin@quickpatch.co", "Segura123", "Admin QA"), CancellationToken.None);

        Assert.Equal(EnsureTenantAdminResult.AlreadyExists, resultado);
    }

    [Fact]
    public async Task TenantInexistente_NoCrea()
    {
        var resultado = await Handler().HandleAsync(
            new EnsureTenantAdminCommand(Guid.NewGuid(), "admin@quickpatch.co", "Segura123", "Admin QA"), CancellationToken.None);

        Assert.Equal(EnsureTenantAdminResult.TenantDisabled, resultado);
        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task DatosInvalidos_LanzanValidacion()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => Handler().HandleAsync(
            new EnsureTenantAdminCommand(tenant, "no-es-correo", "corta", "A"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_SinCorreoNoHaceNada()
    {
        await Bootstrap(new BootstrapOptions()).StartAsync(CancellationToken.None);

        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task Bootstrap_ConDatosCreaElAdministrador()
    {
        var bootstrap = Bootstrap(new BootstrapOptions { AdminEmail = "admin@quickpatch.co", AdminPassword = "Segura123" });

        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StopAsync(CancellationToken.None);

        Assert.Equal("Administrador QUICKPATCH", Assert.Single(store.Users).FullName);
    }

    [Fact]
    public async Task Bootstrap_DatosInvalidosOFallo_NoDetienenElArranque()
    {
        await Bootstrap(new BootstrapOptions { AdminEmail = "admin@quickpatch.co", AdminPassword = "corta" })
            .StartAsync(CancellationToken.None);
        store.Tenants.Clear();
        store.FailNextSaveWithDuplicateDocument = true;
        await Bootstrap(new BootstrapOptions { AdminEmail = "admin@quickpatch.co", AdminPassword = "Segura123" })
            .StartAsync(CancellationToken.None);

        Assert.Empty(store.Users);
    }
}