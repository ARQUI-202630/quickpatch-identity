using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>Registro de auditoría (<c>audit_logs</c>, DD 5.14).</summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);
}