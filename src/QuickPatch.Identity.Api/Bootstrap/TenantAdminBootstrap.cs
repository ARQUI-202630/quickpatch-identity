using Microsoft.Extensions.Options;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Bootstrap;

/// <summary>Datos del administrador inicial (sección <c>Bootstrap</c>), entregados como secreto de Kubernetes.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    public string AdminEmail { get; set; } = string.Empty;

    public string AdminPassword { get; set; } = string.Empty;

    public string AdminFullName { get; set; } = "Administrador QUICKPATCH";

    /// <summary>Administrador de la plataforma (<c>admin_plataforma</c>): gestiona los tenants (RF-21).</summary>
    public string PlatformAdminEmail { get; set; } = string.Empty;

    public string PlatformAdminPassword { get; set; } = string.Empty;

    public string PlatformAdminFullName { get; set; } = "Administrador de la plataforma";
}

/// <summary>
/// Crea al arrancar el administrador del tenant y el de la plataforma (en el tenant del canal, que en el MVP es el
/// dueño de la plataforma), si se configuraron y todavía no existen. Un fallo se registra y no detiene el servicio:
/// el administrador se puede crear en el siguiente arranque.
/// </summary>
public sealed partial class TenantAdminBootstrap(
    IServiceScopeFactory scopes,
    IOptions<BootstrapOptions> options,
    IOptions<ChannelOptions> channel,
    ILogger<TenantAdminBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var datos = options.Value;
        await EnsureAsync(datos.AdminEmail, datos.AdminPassword, datos.AdminFullName, Roles.AdminTenant, cancellationToken);
        await EnsureAsync(
            datos.PlatformAdminEmail, datos.PlatformAdminPassword, datos.PlatformAdminFullName, Roles.AdminPlataforma, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureAsync(string email, string password, string fullName, string role, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<EnsureTenantAdminHandler>();
            var resultado = await handler.HandleAsync(
                new EnsureTenantAdminCommand(channel.Value.TenantId, email, password, fullName, role), cancellationToken);
            LogResult(logger, role, resultado);
        }
        catch (DomainValidationException ex)
        {
            LogInvalid(logger, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Administrador inicial ({Role}): {Result}.")]
    private static partial void LogResult(ILogger logger, string role, EnsureTenantAdminResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Los datos del administrador inicial (Bootstrap) no son válidos.")]
    private static partial void LogInvalid(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo crear el administrador inicial; se reintentará en el próximo arranque.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}