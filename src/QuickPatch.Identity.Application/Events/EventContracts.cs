using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Technicians;

namespace QuickPatch.Identity.Application.Events;

/// <summary>Sobre común de los eventos (DD 8.2.1; <c>quickpatch-kafka/events</c>).</summary>
public sealed record EventEnvelope<TData>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid TenantId,
    string Producer,
    TData Data);

/// <summary><c>data</c> de <c>technician.registered</c> v1 (<c>quickpatch-kafka</c> v0.3.0).</summary>
public sealed record TechnicianRegisteredData(
    Guid TechnicianId,
    Guid SpecialtyId,
    Guid? ProviderId,
    string VerificationStatus,
    DateTimeOffset UpdatedAt);

/// <summary>
/// <c>technician.registered</c>: un técnico o proveedor se registró y quedó pendiente (RF-02). Lleva el estado completo
/// de la proyección que mantiene Matching (P-01) con <c>updatedAt</c> como versión (ADR-017).
/// </summary>
public static class TechnicianRegisteredEvent
{
    public const string Type = "technician.registered";
    public const string Producer = "identity-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OutboxMessage From(TechnicianProfile profile, Guid correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<TechnicianRegisteredData>(
            eventId,
            Type,
            1,
            now,
            correlationId,
            profile.TenantId,
            Producer,
            new TechnicianRegisteredData(
                profile.UserId, profile.SpecialtyId, profile.ProviderId, profile.VerificationStatus, profile.CreatedAt));
        return new OutboxMessage(eventId, profile.TenantId, profile.UserId, Type, JsonSerializer.Serialize(envelope, Json));
    }
}