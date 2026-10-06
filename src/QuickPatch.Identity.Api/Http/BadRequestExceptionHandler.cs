using System.Text.Json;

using Microsoft.AspNetCore.Diagnostics;

namespace QuickPatch.Identity.Api.Http;

/// <summary>
/// Un cuerpo mal formado o con campos que el contrato no declara (<c>additionalProperties: false</c>)
/// es un error del cliente: responde 400 de validación en lugar de 500.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not (BadHttpRequestException or JsonException))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Type = Problems.Validation,
                Title = "La solicitud tiene datos inválidos",
                Detail = "El cuerpo no es un JSON válido o trae campos que el contrato no define.",
            },
        });
    }
}