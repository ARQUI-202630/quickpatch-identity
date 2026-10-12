using Microsoft.Extensions.Options;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Bootstrap;

/// <summary>
/// Usuarios de prueba, uno por rol, para el desarrollo local (SCRUM-450). <c>Program</c> solo registra el sembrado
/// con <c>ASPNETCORE_ENVIRONMENT=Development</c>: en QA y producción no existe. La contraseña es pública a propósito
/// (está en el README); nunca se usa fuera de una base local.
/// </summary>
public static class DevelopmentUsers
{
    public const string Password = "Quickpatch.Dev2026";

    /// <summary>
    /// Especialidad del técnico y del proveedor de prueba. La especialidad es una categoría de Catalog, que vive en
    /// otra base: aquí es solo un identificador fijo de desarrollo.
    /// </summary>
    public static readonly Guid SpecialtyId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    public static IReadOnlyList<DevelopmentUser> All { get; } =
    [
        new("cliente@quickpatch.dev", "Camila Cliente", Roles.Cliente, null, null),
        new("tecnico@quickpatch.dev", "Tomás Técnico", Roles.Tecnico, "3000000001", "DEV-0001"),
        new("proveedor@quickpatch.dev", "Paula Proveedora", Roles.Proveedor, "3000000002", "DEV-0002"),
        new("admin@quickpatch.dev", "Andrés Administrador", Roles.AdminTenant, null, null),
        new("plataforma@quickpatch.dev", "Pilar Plataforma", Roles.AdminPlataforma, null, null),
    ];
}

public sealed record DevelopmentUser(string Email, string FullName, string Role, string? Phone, string? DocumentId);

/// <summary>
/// Crea los <see cref="DevelopmentUsers"/> en el tenant del canal con los mismos casos de uso del registro, así
/// quedan con hash BCrypt y RLS como cualquier usuario. Es idempotente; un fallo se registra y no detiene el servicio.
/// </summary>
public sealed partial class DevelopmentUsersSeed(
    IServiceScopeFactory scopes,
    IOptions<ChannelOptions> channel,
    ILogger<DevelopmentUsersSeed> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var usuario in DevelopmentUsers.All)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var resultado = await CreateAsync(scope.ServiceProvider, usuario, cancellationToken);
                LogResult(logger, usuario.Email, resultado);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, usuario.Email, ex);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<string> CreateAsync(IServiceProvider services, DevelopmentUser usuario, CancellationToken cancellationToken)
    {
        var tenant = channel.Value.TenantId;
        switch (usuario.Role)
        {
            case Roles.AdminTenant or Roles.AdminPlataforma:
                var admin = await services.GetRequiredService<EnsureTenantAdminHandler>().HandleAsync(
                    new EnsureTenantAdminCommand(tenant, usuario.Email, DevelopmentUsers.Password, usuario.FullName, usuario.Role),
                    cancellationToken);
                return admin.ToString();

            case Roles.Tecnico or Roles.Proveedor:
                var tecnico = await services.GetRequiredService<RegisterTechnicianHandler>().HandleAsync(
                    new RegisterTechnicianCommand(
                        tenant,
                        usuario.Email,
                        DevelopmentUsers.Password,
                        usuario.FullName,
                        usuario.Phone,
                        usuario.DocumentId,
                        DevelopmentUsers.SpecialtyId,
                        usuario.Role),
                    cancellationToken);
                return tecnico.GetType().Name;

            default:
                var cliente = await services.GetRequiredService<RegisterClientHandler>().HandleAsync(
                    new RegisterClientCommand(tenant, usuario.Email, DevelopmentUsers.Password, usuario.FullName, usuario.Phone),
                    cancellationToken);
                return cliente.GetType().Name;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Usuario de desarrollo {Email}: {Result}.")]
    private static partial void LogResult(ILogger logger, string email, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo crear el usuario de desarrollo {Email}.")]
    private static partial void LogFailed(ILogger logger, string email, Exception exception);
}