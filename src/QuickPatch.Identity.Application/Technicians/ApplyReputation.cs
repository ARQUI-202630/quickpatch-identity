using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;

namespace QuickPatch.Identity.Application.Technicians;

public enum ReputationHandlingOutcome
{
    /// <summary>Se actualizó la proyección del técnico.</summary>
    Applied,

    /// <summary>Ya se había aplicado una reputación igual o más reciente (ADR-017).</summary>
    Stale,

    /// <summary>El técnico no existe en el tenant del evento (RLS): no hay nada que proyectar.</summary>
    UnknownTechnician,

    /// <summary>El promedio o la cantidad no son válidos: el mensaje no cumple el contrato.</summary>
    Invalid,
}

/// <summary>
/// Proyecta en <c>technician_profiles</c> la reputación que publica Ranking en <c>ranking.technician-reputation-changed</c>
/// (ADR-027, decisión 4; DEP-04 del DD). Corre bajo el tenant del evento, bloquea el perfil y descarta lo que sea más viejo
/// que lo ya aplicado por <c>changedAt</c>, así que es idempotente sin tabla de eventos procesados.
/// </summary>
public sealed partial class ApplyReputationHandler(
    ITenantUnitOfWork unitOfWork,
    ITechnicianProfileRepository profiles,
    ILogger<ApplyReputationHandler> logger)
{
    public Task<ReputationHandlingOutcome> HandleAsync(
        EventEnvelope<TechnicianReputationChangedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var data = envelope.Data;
        if (data.AverageRating is < 0 or > 5 || data.RatingCount < 0)
        {
            LogInvalid(logger, envelope.EventId);
            return Task.FromResult(ReputationHandlingOutcome.Invalid);
        }

        return unitOfWork.ExecuteAsync(
            envelope.TenantId,
            async ct =>
            {
                var profile = await profiles.FindForUpdateAsync(data.TechnicianId, ct);
                if (profile is null)
                {
                    LogUnknownTechnician(logger, envelope.EventId);
                    return ReputationHandlingOutcome.UnknownTechnician;
                }

                return profile.TryApplyReputation(data.AverageRating, data.RatingCount, data.LowReputation, data.ChangedAt)
                    ? ReputationHandlingOutcome.Applied
                    : ReputationHandlingOutcome.Stale;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ranking.technician-reputation-changed {EventId} trae datos inválidos; se descarta.")]
    private static partial void LogInvalid(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ranking.technician-reputation-changed {EventId}: el técnico no existe en el tenant; se descarta.")]
    private static partial void LogUnknownTechnician(ILogger logger, Guid eventId);
}