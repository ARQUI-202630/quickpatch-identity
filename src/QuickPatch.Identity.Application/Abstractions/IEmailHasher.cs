using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Hash del correo normalizado para limitar los pedidos de recuperación sin conservar el correo (Ley 1581). Es un HMAC
/// con una clave propia de Identity: sin la clave no se puede saber con un diccionario quién pidió la recuperación.
/// Devuelve 64 caracteres hexadecimales en minúscula.
/// </summary>
public interface IEmailHasher
{
    string Hash(string normalizedEmail);
}