using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Infrastructure.Security;

/// <summary>
/// Llave RSA con la que la app y el panel cifran la contraseña antes de enviarla (contrato 1.11.0). Publica la parte
/// pública en SPKI y descifra con RSA-OAEP-SHA256. El identificador es la huella SHA-256 de la llave pública: si la llave
/// cambia, el cliente lo ve y pide la nueva.
/// </summary>
public sealed partial class PasswordEncryptionKey : IDisposable
{
    public const string Algorithm = "RSA-OAEP-256";

    /// <summary>Largo máximo del texto cifrado en base64url: una llave de 4096 bits da 683 caracteres.</summary>
    public const int MaxCiphertextLength = 700;

    private readonly RSA rsa;

    public PasswordEncryptionKey(IOptions<PasswordEncryptionOptions> options, ILogger<PasswordEncryptionKey> logger)
    {
        rsa = RSA.Create();
        var pem = options.Value.PrivateKeyPem;
        if (string.IsNullOrWhiteSpace(pem))
        {
            rsa.KeySize = 3072;
            LogEphemeralKey(logger);
        }
        else
        {
            rsa.ImportFromPem(pem);
        }

        var spki = rsa.ExportSubjectPublicKeyInfo();
        PublicKey = Convert.ToBase64String(spki);
        KeyId = Base64Url(SHA256.HashData(spki))[..16];
    }

    /// <summary>Llave pública en SPKI (DER) codificada en base64, lista para <c>crypto.subtle.importKey('spki', …)</c>.</summary>
    public string PublicKey { get; }

    public string KeyId { get; }

    /// <summary>Descifra la contraseña; devuelve <c>null</c> si el texto no es base64url válido o no descifra con esta llave.</summary>
    public string? TryDecrypt(string ciphertext)
    {
        if (ciphertext.Length > MaxCiphertextLength)
        {
            return null;
        }

        try
        {
            var plain = rsa.Decrypt(FromBase64Url(ciphertext), RSAEncryptionPadding.OaepSHA256);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    public void Dispose() => rsa.Dispose();

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PasswordEncryption:PrivateKeyPem está vacío: se generó una llave para este proceso; con más de una réplica hay que darla como secreto.")]
    private static partial void LogEphemeralKey(ILogger logger);
}