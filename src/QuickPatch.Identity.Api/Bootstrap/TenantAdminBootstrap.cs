using Microsoft.Extensions.Options;

using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Bootstrap;

/// <summary>Datos del administrador inicial (sección <c>Bootstrap</c>), entregados como secreto de Kubernetes.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    public string AdminEmail { get; set; } = string.Empty;

    public string AdminPassword { get; set; } = string.Empty;

    public string AdminFullName { get; set; } = "Administrador QUICKPATCH";
}

/// <summary>
/// Crea el administrador del tenant del canal al arrancar, si se configuró y todavía no existe. Un fallo se registra
/// y no detiene el servicio: el administrador se puede crear en el siguiente arranque.
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
        if (string.IsNullOrWhiteSpace(datos.AdminEmail))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<EnsureTenantAdminHandler>();
            var resultado = await handler.HandleAsync(
                new EnsureTenantAdminCommand(channel.Value.TenantId, datos.AdminEmail, datos.AdminPassword, datos.AdminFullName),
                cancellationToken);
            LogResult(logger, resultado);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Administrador inicial del tenant: {Result}.")]
    private static partial void LogResult(ILogger logger, EnsureTenantAdminResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Los datos del administrador inicial (Bootstrap) no son válidos.")]
    private static partial void LogInvalid(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo crear el administrador inicial; se reintentará en el próximo arranque.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}