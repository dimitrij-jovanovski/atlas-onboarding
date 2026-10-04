using Atlas.Domain;
using Microsoft.AspNetCore.Http;

namespace Atlas.Infrastructure.Web;

/// <summary>
/// Turns a broken business rule into 409 (wrong state) or 422 (request can never succeed as sent).
///
/// An endpoint filter rather than an IExceptionHandler: in .NET 8 the exception-handler middleware
/// logs every exception as an unhandled error before handing it over, which would put ordinary
/// customer mistakes ("selfie missing") in the error log next to real failures.
/// </summary>
public sealed class DomainRuleViolationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DomainRuleViolation violation)
        {
            var status = violation.Code is "invalid_state" or "not_draft"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: violation.Message, type: violation.Code, statusCode: status);
        }
    }
}
