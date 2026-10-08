using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application;

/// <summary>
/// Capa de aplicación: casos de uso, comandos y consultas (SDD 6.3).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(LockoutPolicy.Default);
        services.AddScoped<RegisterClientHandler>();
        services.AddScoped<RegisterTechnicianHandler>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<GetCurrentUserHandler>();
        services.AddScoped<EnsureTenantAdminHandler>();
        services.AddScoped<ListTenantsHandler>();
        services.AddScoped<UpdateTenantStatusHandler>();
        return services;
    }
}