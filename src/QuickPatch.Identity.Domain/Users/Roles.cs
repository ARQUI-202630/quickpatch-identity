namespace QuickPatch.Identity.Domain.Users;

/// <summary>Roles del DD (sección 5.2). Se publican en el claim <c>role</c> del token.</summary>
public static class Roles
{
    public const string Cliente = "cliente";
    public const string Tecnico = "tecnico";
    public const string Proveedor = "proveedor";
    public const string EmpresaContacto = "empresa_contacto";
    public const string AdminTenant = "admin_tenant";
    public const string AdminPlataforma = "admin_plataforma";

    public static IReadOnlyList<string> All { get; } =
        [Cliente, Tecnico, Proveedor, EmpresaContacto, AdminTenant, AdminPlataforma];
}