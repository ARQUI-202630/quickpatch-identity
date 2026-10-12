using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Consulta de técnicos del panel del administrador (RF-19, <c>GET /v1/admin/technicians</c>).</summary>
public sealed record ListTechniciansQuery(Guid TenantId, string? VerificationStatus, int Page, int Size);

/// <summary>Lista paginada de los técnicos y proveedores del tenant del administrador; RLS impide ver otros tenants.</summary>
public sealed class ListTechniciansHandler(ITenantUnitOfWork unitOfWork, ITechnicianProfileRepository profiles)
{
    public const int MaxSize = 100;

    public Task<TechnicianPage> HandleAsync(ListTechniciansQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        if (query.VerificationStatus is not null && !VerificationStatus.IsValid(query.VerificationStatus))
        {
            errors["verificationStatus"] = ["Debe ser pendiente, aprobado, rechazado o suspendido."];
        }

        if (query.Page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (query.Size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => profiles.ListAsync(query.VerificationStatus, query.Page, query.Size, ct),
            cancellationToken);
    }
}

/// <summary>Acción del administrador sobre la verificación de un técnico (DD 8.3.13).</summary>
public enum VerificationAction
{
    Approve,
    Reject,
    Suspend,
}

public sealed record ChangeVerificationCommand(
    Guid TenantId,
    Guid ActorUserId,
    Guid TechnicianId,
    VerificationAction Action,
    string? Reason,
    Guid CorrelationId);

public abstract record ChangeVerificationResult
{
    public sealed record Changed(TechnicianProfile Profile) : ChangeVerificationResult;

    public sealed record NotFound : ChangeVerificationResult;

    public sealed record InvalidTransition : ChangeVerificationResult;
}

/// <summary>
/// Aprueba, rechaza o suspende a un técnico o proveedor del tenant (RF-19, RF-20). En una sola transacción bloquea
/// el perfil, valida la transición del DD 8.3.13, registra la acción en <c>audit_logs</c> con el responsable y la
/// correlación, y encola <c>technician.verification-changed</c> en el Outbox (ADR-007).
/// </summary>
public sealed class ChangeVerificationHandler(
    ITenantUnitOfWork unitOfWork,
    ITechnicianProfileRepository profiles,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<ChangeVerificationResult> HandleAsync(ChangeVerificationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = command.Action == VerificationAction.Approve ? null : TechnicianProfile.ValidateReason(command.Reason);

        return await unitOfWork.ExecuteAsync<ChangeVerificationResult>(
            command.TenantId,
            async ct =>
            {
                var profile = await profiles.FindForUpdateAsync(command.TechnicianId, ct);
                if (profile is null)
                {
                    return new ChangeVerificationResult.NotFound();
                }

                var previous = profile.VerificationStatus;
                var (changed, action) = command.Action switch
                {
                    VerificationAction.Approve => (profile.TryApprove(), AuditEntry.ApproveTechnician),
                    VerificationAction.Reject => (profile.TryReject(reason!), AuditEntry.RejectTechnician),
                    _ => (profile.TrySuspend(reason!), AuditEntry.SuspendTechnician),
                };
                if (!changed)
                {
                    return new ChangeVerificationResult.InvalidTransition();
                }

                var now = clock.GetUtcNow();
                audit.Add(new AuditEntry(
                    Guid.CreateVersion7(now),
                    profile.TenantId,
                    command.ActorUserId,
                    action,
                    profile.UserId,
                    JsonSerializer.Serialize(new
                    {
                        anterior = previous,
                        nuevo = profile.VerificationStatus,
                        motivo = profile.VerificationReason,
                        correlationId = command.CorrelationId,
                    }),
                    now));
                outbox.Enqueue(TechnicianVerificationChangedEvent.From(profile, previous, command.CorrelationId, now));
                return new ChangeVerificationResult.Changed(profile);
            },
            cancellationToken);
    }
}