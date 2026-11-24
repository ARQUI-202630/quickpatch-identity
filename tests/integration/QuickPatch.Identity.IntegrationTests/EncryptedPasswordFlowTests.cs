using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// Contraseña cifrada en el cliente (contrato 1.11.0): la app y el panel piden la llave pública, cifran con RSA-OAEP-256
/// y mandan <c>encryptedPassword</c>, así que la contraseña no viaja legible. Identity la descifra y sigue igual que antes.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class EncryptedPasswordFlowTests(IdentityEnvironment env)
{
    private static readonly Uri Key = new("/v1/auth/password-key", UriKind.Relative);
    private static readonly Uri Register = new("/v1/auth/register/client", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private static async Task<(string KeyId, RSA Rsa)> PublicKeyAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(Key);
        Assert.Equal("RSA-OAEP-256", body.GetProperty("algorithm").GetString());
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(body.GetProperty("publicKey").GetString()!), out _);
        return (body.GetProperty("keyId").GetString()!, rsa);
    }

    private static string Encrypt(RSA rsa, string password) =>
        Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.OaepSHA256))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task RegistroYLoginConLaContrasenaCifrada()
    {
        using var client = env.App.CreateClient();
        var (keyId, rsa) = await PublicKeyAsync(client);
        using (rsa)
        {
            var email = $"u{Guid.NewGuid():N}@correo.co";

            using var register = await client.PostAsJsonAsync(Register, new
            {
                email,
                encryptedPassword = new { keyId, ciphertext = Encrypt(rsa, "Segura123") },
                fullName = "Ana Pérez",
            });
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);

            // Cada cifrado da un texto distinto (OAEP es aleatorio), y los dos descifran a la misma contraseña.
            using var login = await client.PostAsJsonAsync(Login, new { email, encryptedPassword = new { keyId, ciphertext = Encrypt(rsa, "Segura123") } });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // La contraseña cifrada y la en claro son la misma cuenta: la del registro cifrado entra también en claro.
            using var plain = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);

            using var wrong = await client.PostAsJsonAsync(Login, new { email, encryptedPassword = new { keyId, ciphertext = Encrypt(rsa, "Otra12345") } });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
    }

    [Fact]
    public async Task LlaveDistinta_400LlaveDesconocida()
    {
        using var client = env.App.CreateClient();
        var (_, rsa) = await PublicKeyAsync(client);
        using (rsa)
        {
            using var response = await client.PostAsJsonAsync(Login, new
            {
                email = "ana@correo.co",
                encryptedPassword = new { keyId = "otra-llave", ciphertext = Encrypt(rsa, "Segura123") },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.EndsWith("/llave-desconocida", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TextoQueNoDescifra_400Validacion()
    {
        using var client = env.App.CreateClient();
        var (keyId, rsa) = await PublicKeyAsync(client);
        rsa.Dispose();

        using var otra = RSA.Create(3072);
        using var response = await client.PostAsJsonAsync(Login, new
        {
            email = "ana@correo.co",
            encryptedPassword = new { keyId, ciphertext = Encrypt(otra, "Segura123") },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("encryptedPassword", out _));
    }

    [Fact]
    public async Task EnClaroYCifradaALaVez_400()
    {
        using var client = env.App.CreateClient();
        var (keyId, rsa) = await PublicKeyAsync(client);
        using (rsa)
        {
            using var response = await client.PostAsJsonAsync(Login, new
            {
                email = "ana@correo.co",
                password = "Segura123",
                encryptedPassword = new { keyId, ciphertext = Encrypt(rsa, "Segura123") },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task LaLlaveNoCambiaEntrePeticiones()
    {
        using var client = env.App.CreateClient();
        var (primera, rsa1) = await PublicKeyAsync(client);
        var (segunda, rsa2) = await PublicKeyAsync(client);
        rsa1.Dispose();
        rsa2.Dispose();

        Assert.Equal(primera, segunda);
    }
}