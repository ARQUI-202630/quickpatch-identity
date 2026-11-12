using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Infrastructure.Persistence;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Identity.Infrastructure.Messaging;

/// <summary>Conexión a Kafka (VM6 en producción, VM7 en QA). Sección <c>Kafka</c>; sin servidores, el publicador no arranca.</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Grupo del consumidor de <c>ranking.technician-reputation-changed</c> (ADR-027).</summary>
    public string ConsumerGroupId { get; set; } = "identity-service";

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Guarda el evento en <c>outbox_events</c> dentro de la transacción del caso de uso.</summary>
public sealed class EfOutbox(IdentityDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(OutboxEventRecord.Pending(message.Id, message.TenantId, message.AggregateId, message.EventType, message.Payload, clock.GetUtcNow()));
    }
}