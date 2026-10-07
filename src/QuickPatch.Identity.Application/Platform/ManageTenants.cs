using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;

namespace QuickPatch.Identity.Application.Platform;

/// <summary>Todos los tenants de la plataforma (RF-21, DD 10.4), ordenados por nombre.</summary>
public sealed class ListTenantsHandler(IPlatformUnitOfWork unitOfWork, IPlatformTenantRepository tenants)
{
    public Task<IReadOnlyList<Tenant>> HandleAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(tenants.ListAsync, cancellationToken);
}

/// <summary>
/// Activa o desactiva un tenant (RF-21). <see cref="ActorTenantId"/> es el tenant del administrador de la
/// plataforma: no se puede desactivar, porque dejaría la plataforma sin administración.
/// </summary>
public sealed record UpdateTenantStatusCommand(Guid TenantId, string? Status, Guid ActorUserId, Guid ActorTenantId, string? CorrelationId);

public abstract record UpdateTenantStatusResult
{
    public sealed record Updated(Tenant Tenant) : UpdateTenantStatusResult;

    public sealed record NotFound : UpdateTenantStatusResult;

    public sealed record PlatformTenant : UpdateTenantStatusResult;
}

public sealed class UpdateTenantStatusHandler(
    IPlatformUnitOfWork unitOfWork,
    IPlatformTenantRepository tenants,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<UpdateTenantStatusResult> HandleAsync(UpdateTenantStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Tenant.IsValidStatus(command.Status))
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["status"] = [$"El estado debe ser {Tenant.Active} o {Tenant.Inactive}."],
            });
        }

        if (command.TenantId == command.ActorTenantId && command.Status == Tenant.Inactive)
        {
            return new UpdateTenantStatusResult.PlatformTenant();
        }

        return await unitOfWork.ExecuteAsync<UpdateTenantStatusResult>(
            async ct =>
            {
                var tenant = await tenants.FindForUpdateAsync(command.TenantId, ct);
                if (tenant is null)
                {
                    return new UpdateTenantStatusResult.NotFound();
                }

                var anterior = tenant.Status;
                if (tenant.ChangeStatus(command.Status!))
                {
                    audit.Add(new AuditEntry(
                        Guid.CreateVersion7(),
                        tenantId: null,
                        command.ActorUserId,
                        command.Status == Tenant.Active ? AuditEntry.ActivateTenant : AuditEntry.DeactivateTenant,
                        tenant.Id,
                        JsonSerializer.Serialize(new { anterior, nuevo = tenant.Status, correlationId = command.CorrelationId }),
                        clock.GetUtcNow()));
                }

                return new UpdateTenantStatusResult.Updated(tenant);
            },
            cancellationToken);
    }
}