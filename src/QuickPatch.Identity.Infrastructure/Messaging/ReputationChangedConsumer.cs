using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Identity.Infrastructure.Messaging;

/// <summary>
/// Consumidor de <c>ranking.technician-reputation-changed</c> v1 (ADR-027): proyecta el promedio y la marca de reputación
/// baja en <c>technician_profiles</c>. Confirma el offset solo después de aplicar el evento, descarta el mensaje que no
/// cumple el contrato y reintenta los errores de lectura no fatales, igual que los consumidores de los demás servicios.
/// </summary>
public sealed partial class ReputationChangedConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<ReputationChangedConsumer> logger)
    : KafkaConsumerService(consumer, [ConsumedEvents.TechnicianReputationChanged], logger)
{
    protected override Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken) =>
        TryProcessAsync(result.Message.Value, cancellationToken);

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<TechnicianReputationChangedData>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope<TechnicianReputationChangedData>>(value, ConsumedEvents.Json);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope?.Data is not { } data || envelope.EventType != ConsumedEvents.TechnicianReputationChanged
            || envelope.EventVersion != 1 || envelope.EventId == Guid.Empty || envelope.TenantId == Guid.Empty
            || data.TechnicianId == Guid.Empty)
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ApplyReputationHandler>();
            await handler.HandleAsync(envelope, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(logger, envelope.EventId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de ranking.technician-reputation-changed que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}