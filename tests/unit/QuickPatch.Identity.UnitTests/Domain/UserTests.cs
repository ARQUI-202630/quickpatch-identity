using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("sin-arroba.com")]
    [InlineData("a@b")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateRegistration_CorreoInvalido_Falla(string? email)
    {
        var ex = Assert.Throws<DomainValidationException>(() => User.ValidateRegistration(email, "Segura123", "Ana Pérez", null));

        Assert.True(ex.Errors.ContainsKey("email"));
    }

    [Theory]
    [InlineData("corta")]
    [InlineData(null)]
    public void ValidateRegistration_ContrasenaCorta_Falla(string? password)
    {
        var ex = Assert.Throws<DomainValidationException>(() => User.ValidateRegistration("ana@correo.co", password, "Ana Pérez", null));

        Assert.True(ex.Errors.ContainsKey("password"));
    }

    [Fact]
    public void ValidateRegistration_NombreYTelefonoInvalidos_Fallan()
    {
        var ex = Assert.Throws<DomainValidationException>(() => User.ValidateRegistration("ana@correo.co", "Segura123", "A", new string('1', 31)));

        Assert.True(ex.Errors.ContainsKey("fullName"));
        Assert.True(ex.Errors.ContainsKey("phone"));
    }

    [Fact]
    public void ValidateRegistration_DatosValidos_NoFalla()
    {
        User.ValidateRegistration(" Ana@Correo.CO ", "Segura123", "Ana Pérez", "3001234567");
    }

    [Fact]
    public void Create_NormalizaCorreoYTelefono()
    {
        var user = User.Create(Guid.NewGuid(), " Ana@Correo.CO ", "hash", Roles.Cliente, " Ana Pérez ", "  ", Now);

        Assert.Equal("ana@correo.co", user.Email);
        Assert.Equal("Ana Pérez", user.FullName);
        Assert.Null(user.Phone);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void Create_RolDesconocido_Falla()
    {
        Assert.Throws<ArgumentException>(() => User.Create(Guid.NewGuid(), "a@b.co", "h", "superusuario", "Ana", null, Now));
    }

    [Fact]
    public void QuintoIntentoFallido_BloqueaQuinceMinutos()
    {
        var user = User.Create(Guid.NewGuid(), "a@b.co", "h", Roles.Cliente, "Ana", null, Now);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(user.RegisterFailedAttempt(Now, LockoutPolicy.Default));
        }

        Assert.True(user.RegisterFailedAttempt(Now, LockoutPolicy.Default));
        Assert.Equal(Now.AddMinutes(15), user.LockedUntil);
        Assert.True(user.IsLocked(Now.AddMinutes(14)));
        Assert.False(user.IsLocked(Now.AddMinutes(15)));
        Assert.Equal(0, user.FailedLoginAttempts);
    }

    [Fact]
    public void LoginExitoso_ReiniciaContadorYBloqueo()
    {
        var user = User.Create(Guid.NewGuid(), "a@b.co", "h", Roles.Cliente, "Ana", null, Now);
        user.RegisterFailedAttempt(Now, LockoutPolicy.Default);

        user.RegisterSuccessfulLogin();

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void RegisterFailedAttempt_SinPolitica_Falla()
    {
        var user = User.Create(Guid.NewGuid(), "a@b.co", "h", Roles.Cliente, "Ana", null, Now);

        Assert.Throws<ArgumentNullException>(() => user.RegisterFailedAttempt(Now, null!));
    }

    [Fact]
    public void Tenant_SoloActivoEsActivo()
    {
        Assert.True(new Tenant(Guid.NewGuid(), "Alfa", "900", Tenant.Active, Now).IsActive);
        Assert.False(new Tenant(Guid.NewGuid(), "Alfa", "900", Tenant.Inactive, Now).IsActive);
    }

    [Fact]
    public void Roles_SonLosSeisDelDD()
    {
        Assert.Equal(["cliente", "tecnico", "proveedor", "empresa_contacto", "admin_tenant", "admin_plataforma"], Roles.All);
    }

    [Fact]
    public void DomainValidationException_ConstructoresEstandar()
    {
        Assert.Empty(new DomainValidationException().Errors);
        Assert.Equal("x", new DomainValidationException("x").Message);
        Assert.Empty(new DomainValidationException("x", new InvalidOperationException()).Errors);
    }
}