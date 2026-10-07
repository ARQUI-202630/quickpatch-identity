namespace QuickPatch.Identity.Domain.Audit;

/// <summary>
/// Fila de <c>audit_logs</c> (DD 5.14). Las operaciones de plataforma no pertenecen a un tenant: llevan
/// <see cref="TenantId"/> nulo y solo las escribe el rol <c>identity_platform</c> (DD 10.2).
/// </summary>
public sealed class AuditEntry
{
    public const string ActivateTenant = "activar_tenant";
    public const string DeactivateTenant = "desactivar_tenant";

    public AuditEntry(Guid id, Guid? tenantId, Guid? actorUserId, string action, Guid? targetId, string? metadata, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ActorUserId = actorUserId;
        Action = action;
        TargetId = targetId;
        Metadata = metadata;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid? TenantId { get; }

    public Guid? ActorUserId { get; }

    public string Action { get; }

    public Guid? TargetId { get; }

    /// <summary>JSON con información adicional (estado anterior y nuevo, correlación).</summary>
    public string? Metadata { get; }

    public DateTimeOffset CreatedAt { get; }
}