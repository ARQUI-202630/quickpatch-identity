using System.Security.Cryptography;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Datos con los que Identity valida sus propios tokens (RS256, ADR-018): emisor, audiencia y la llave pública derivada de
/// la llave privada. El API arma la validación con ellos sin conocer el emisor (SDD 10.3.2).
/// </summary>
public interface ITokenValidationKey
{
    string Issuer { get; }

    string Audience { get; }

    /// <summary>Parámetros públicos de la llave RSA (sin la parte privada).</summary>
    RSAParameters PublicKey { get; }
}