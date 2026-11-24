using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Transversal.Http;

namespace QuickPatch.Identity.Api.Http;

/// <summary>Respuestas HTTP comunes de los endpoints de Identity (Problem Details del contrato).</summary>
internal static class IdentityResults
{
    internal static LockedResult Locked(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
        return new LockedResult(seconds);
    }

    internal static IResult TenantDisabled() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        type: Problems.TenantDisabled,
        title: "La empresa está inactiva",
        detail: "tenant is disabled");

    internal static IResult EmailTaken() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        type: Problems.EmailTaken,
        title: "El correo ya está registrado");

    internal static IResult ChannelNotConfigured() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: Problems.ChannelNotConfigured,
        title: "El canal no tiene un tenant configurado");

    internal static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);

    /// <summary>429 con <c>Retry-After</c> (contrato 1.10.0), en formato Problem Details.</summary>
    internal sealed class TooManyRequestsResult(int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                type: Problems.TooManyRequests,
                title: "Demasiadas solicitudes",
                detail: "Ya pediste recuperar la contraseña varias veces. Intenta de nuevo más tarde.").ExecuteAsync(httpContext);
        }
    }

    /// <summary>423 con <c>Retry-After</c> (contrato), en formato Problem Details.</summary>
    internal sealed class LockedResult(int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status423Locked,
                type: Problems.AccountLocked,
                title: "La cuenta está bloqueada temporalmente",
                detail: "Demasiados intentos fallidos. Intenta de nuevo más tarde.").ExecuteAsync(httpContext);
        }
    }
}