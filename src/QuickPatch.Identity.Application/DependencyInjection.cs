using Microsoft.Extensions.DependencyInjection;

namespace QuickPatch.Identity.Application;

/// <summary>
/// Capa de aplicación: casos de uso, comandos y consultas (SDD 6.3).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}