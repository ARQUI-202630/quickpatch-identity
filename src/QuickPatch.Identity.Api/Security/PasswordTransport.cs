using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.Infrastructure.Security;

namespace QuickPatch.Identity.Api.Security;

/// <summary>Esquema <c>EncryptedPassword</c> del contrato 1.11.0: la contraseña cifrada con la llave de <c>keyId</c>.</summary>
public sealed record EncryptedPassword(string? KeyId, string? Ciphertext);

/// <summary>Esquema <c>PasswordKey</c> del contrato 1.11.0.</summary>
public sealed record PasswordKeyResponse(string KeyId, string Algorithm, string PublicKey);

/// <summary>
/// Saca la contraseña del cuerpo (contrato 1.11.0): llega en claro en <c>password</c> o cifrada en
/// <c>encryptedPassword</c>, nunca las dos. La descifrada sigue el mismo camino que la de antes, así que las reglas de
/// la contraseña y el hash con BCrypt no cambian.
/// </summary>
public sealed class PasswordTransport(PasswordEncryptionKey key, IOptions<PasswordEncryptionOptions> options)
{
    public PasswordKeyResponse PublicKey => new(key.KeyId, PasswordEncryptionKey.Algorithm, key.PublicKey);

    /// <summary>
    /// Devuelve <c>false</c> con el error para responder. Con <c>true</c>, <paramref name="password"/> es la contraseña o
    /// <c>null</c> si el cuerpo no trae ninguna, y cada caso de uso responde como antes a la que falta.
    /// </summary>
    public bool TryResolve(string? plain, EncryptedPassword? encrypted, out string? password, out IResult? error)
    {
        password = null;
        error = null;
        if (encrypted is null)
        {
            if (plain is not null && options.Value.RequireEncrypted)
            {
                error = Invalid("password", "La contraseña debe llegar cifrada en encryptedPassword.");
                return false;
            }

            password = plain;
            return true;
        }

        if (plain is not null)
        {
            error = Invalid("password", "Envía password o encryptedPassword, no los dos.");
            return false;
        }

        if (string.IsNullOrEmpty(encrypted.KeyId) || string.IsNullOrEmpty(encrypted.Ciphertext))
        {
            error = Invalid("encryptedPassword", "Faltan keyId o ciphertext.");
            return false;
        }

        if (!string.Equals(encrypted.KeyId, key.KeyId, StringComparison.Ordinal))
        {
            error = Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                type: Problems.UnknownPasswordKey,
                title: "La llave de cifrado cambió",
                detail: "Pide la llave de nuevo con GET /v1/auth/password-key y vuelve a cifrar la contraseña.");
            return false;
        }

        password = key.TryDecrypt(encrypted.Ciphertext);
        if (password is null)
        {
            error = Invalid("encryptedPassword", "No se pudo descifrar la contraseña.");
            return false;
        }

        return true;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] },
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}