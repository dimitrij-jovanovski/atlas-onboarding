using Atlas.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Atlas.Infrastructure.Web;

/// <summary>Maps broken business rules to 409 (wrong state) or 422 (request can never succeed as sent).</summary>
public sealed class DomainRuleViolationHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainRuleViolation violation) return false;

        context.Response.StatusCode = violation.Code is "invalid_state" or "not_draft"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Title = violation.Message, Type = violation.Code, Status = context.Response.StatusCode },
        });
    }
}
