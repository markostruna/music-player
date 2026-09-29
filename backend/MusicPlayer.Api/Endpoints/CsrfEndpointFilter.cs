using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MusicPlayer.Api.Endpoints;

public sealed class CsrfEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return TypedResults.Problem(title: "Invalid anti-forgery token", statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
