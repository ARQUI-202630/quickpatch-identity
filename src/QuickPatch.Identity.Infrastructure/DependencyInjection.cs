using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.Infrastructure.Persistence;
using QuickPatch.Identity.Infrastructure.Security;

namespace QuickPatch.Identity.Infrastructure;

/// <summary>
/// Capa de infraestructura: PostgreSQL (EF Core), BCrypt y firma de tokens (SDD 6.3).
/// Implementa las interfaces que definen Application y Domain.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "Identity";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Sin llave privada Identity no puede emitir ni validar tokens: falla al arrancar, no en cada petición.
        services.AddOptions<JwtIssuerOptions>()
            .Bind(configuration.GetSection(JwtIssuerOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.PrivateKeyPem), "Falta Jwt:PrivateKeyPem: Identity no puede emitir tokens.")
            .ValidateOnStart();
        services.Configure<PasswordOptions>(configuration.GetSection(PasswordOptions.Section));
        services.Configure<ChannelOptions>(configuration.GetSection(ChannelOptions.Section));
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName)));

        services.AddScoped<ITenantUnitOfWork, TenantUnitOfWork>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITechnicianProfileRepository, TechnicianProfileRepository>();
        services.AddScoped<IPlatformUnitOfWork, PlatformUnitOfWork>();
        services.AddScoped<IPlatformTenantRepository, PlatformTenantRepository>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<RsaTokenIssuer>();
        services.AddSingleton<ITokenIssuer>(sp => sp.GetRequiredService<RsaTokenIssuer>());

        services.AddHealthChecks().AddDbContextCheck<IdentityDbContext>("postgresql", tags: [ReadyTag]);
        return services;
    }
}