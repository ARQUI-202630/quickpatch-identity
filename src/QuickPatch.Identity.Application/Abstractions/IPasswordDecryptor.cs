namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Llave con la que la app y el panel cifran la contraseña (RSA-OAEP-256, contrato 1.11.0). El API la usa para publicar la
/// llave pública y descifrar el campo <c>encryptedPassword</c> sin conocer la implementación (SDD 10.3.2).
/// </summary>
public interface IPasswordDecryptor
{
    string KeyId { get; }

    string Algorithm { get; }

    /// <summary>Llave pública (SubjectPublicKeyInfo en Base64).</summary>
    string PublicKey { get; }

    /// <summary>Si es verdadero, la contraseña en claro se rechaza y solo se acepta <c>encryptedPassword</c>.</summary>
    bool RequireEncrypted { get; }

    /// <summary>Devuelve la contraseña, o <c>null</c> si el texto cifrado no es válido.</summary>
    string? TryDecrypt(string ciphertext);
}