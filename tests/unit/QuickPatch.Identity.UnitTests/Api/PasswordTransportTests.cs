using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.Infrastructure.Security;

namespace QuickPatch.Identity.UnitTests.Api;

public class PasswordTransportTests
{
    private static (PasswordTransport Transport, PasswordEncryptionKey Key) Create(bool requireEncrypted)
    {
        var options = Options.Create(new PasswordEncryptionOptions { RequireEncrypted = requireEncrypted });
        var key = new PasswordEncryptionKey(options, NullLogger<PasswordEncryptionKey>.Instance);
        return (new PasswordTransport(key, options), key);
    }

    private static string Encrypt(PasswordEncryptionKey key, string password)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.OaepSHA256))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public void ExigeCifrada_RechazaLaContrasenaEnClaro()
    {
        var (transport, key) = Create(requireEncrypted: true);
        using (key)
        {
            Assert.False(transport.TryResolve("Segura123", null, out var password, out var error));
            Assert.Null(password);
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void ExigeCifrada_AceptaLaCifrada()
    {
        var (transport, key) = Create(requireEncrypted: true);
        using (key)
        {
            Assert.True(transport.TryResolve(null, new EncryptedPassword(key.KeyId, Encrypt(key, "Segura123ñ")), out var password, out _));
            Assert.Equal("Segura123ñ", password);
        }
    }

    [Fact]
    public void SinNingunaContrasena_DejaQueElCasoDeUsoResponda()
    {
        var (transport, key) = Create(requireEncrypted: false);
        using (key)
        {
            Assert.True(transport.TryResolve(null, null, out var password, out var error));
            Assert.Null(password);
            Assert.Null(error);
        }
    }

    [Fact]
    public void TextoDemasiadoLargoONoBase64_NoDescifra()
    {
        var (_, key) = Create(requireEncrypted: false);
        using (key)
        {
            Assert.Null(key.TryDecrypt(new string('A', PasswordEncryptionKey.MaxCiphertextLength + 1)));
            Assert.Null(key.TryDecrypt("no es base64!"));
        }
    }
}