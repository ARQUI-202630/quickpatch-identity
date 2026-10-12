using System.Text;
using System.Text.RegularExpressions;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Infrastructure.Persistence;

namespace QuickPatch.Identity.Infrastructure.Messaging;

/// <summary>Conexión a Kafka (VM6 en producción, VM7 en QA). Sección <c>Kafka</c>; sin servidores, el publicador no arranca.</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Publicador del Outbox (ADR-007). Sección <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// Rol <c>BYPASSRLS</c> que adopta el publicador con <c>SET LOCAL ROLE</c> para leer los eventos de todos los
    /// tenants (<c>identity_outbox</c>, <c>db/roles.sql</c>). Vacío solo en desarrollo local.
    /// </summary>
    public string PublisherRole { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Fila de <c>outbox_events</c> (DD 5.15).</summary>
public sealed class OutboxEventRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AggregateId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }
}

/// <summary>Guarda el evento en <c>outbox_events</c> dentro de la transacción del caso de uso.</summary>
public sealed class EfOutbox(IdentityDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(new OutboxEventRecord
        {
            Id = message.Id,
            TenantId = message.TenantId,
            AggregateId = message.AggregateId,
            EventType = message.EventType,
            Payload = message.Payload,
            CreatedAt = clock.GetUtcNow(),
            Attempts = 0,
        });
    }
}

/// <summary>
/// Publicador del Transactional Outbox (ADR-007), igual al de Catalog: lee los eventos pendientes con el rol del
/// publicador, los publica en el topic con el nombre del evento y la clave del agregado (el técnico), y marca
/// <c>published_at</c>. Si Kafka falla, suma un intento y reintenta en la siguiente vuelta: ningún evento se pierde.
/// </summary>
public sealed partial class OutboxPublisher(
    IServiceScopeFactory scopes,
    IProducer<string, string> producer,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await PublishPendingAsync(stoppingToken) > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex);
            }

            await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
        }
    }

    /// <summary>Publica un lote de eventos pendientes; devuelve cuántos quedaron publicados.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var role = options.Value.PublisherRole;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!RoleName().IsMatch(role))
            {
                throw new InvalidOperationException("Outbox:PublisherRole no es un nombre de rol válido.");
            }

#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002
        }

        var batch = await db.OutboxEvents
            .FromSql($"SELECT * FROM outbox_events WHERE published_at IS NULL ORDER BY created_at LIMIT {options.Value.BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var record in batch)
        {
            try
            {
                await producer.ProduceAsync(record.EventType, ToMessage(record), cancellationToken);
                record.PublishedAt = clock.GetUtcNow();
                published++;
            }
            catch (ProduceException<string, string> ex)
            {
                record.Attempts++;
                LogProduceFailed(logger, record.Id, record.Attempts, ex);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    private static Message<string, string> ToMessage(OutboxEventRecord record) => new()
    {
        Key = record.AggregateId.ToString(),
        Value = record.Payload,
        Headers = new Headers
        {
            { "eventId", Encoding.UTF8.GetBytes(record.Id.ToString()) },
            { "eventType", Encoding.UTF8.GetBytes(record.EventType) },
        },
    };

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la publicación del Outbox.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo publicar el evento {EventId} (intento {Attempt}).")]
    private static partial void LogProduceFailed(ILogger logger, Guid eventId, int attempt, Exception exception);
}