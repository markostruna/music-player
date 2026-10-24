using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using MusicPlayer.Api.Endpoints;

namespace MusicPlayer.Api.Tests;

public sealed class CsrfEndpointFilterTests
{
    [Fact]
    public async Task RejectsUnsafeRequestWithoutAnAntiforgeryToken()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddAntiforgery()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Post;
        var invocation = EndpointFilterInvocationContext.Create(context);
        var calledNext = false;
        var filter = new CsrfEndpointFilter(services.GetRequiredService<IAntiforgery>());

        var result = await filter.InvokeAsync(invocation, _ =>
        {
            calledNext = true;
            return ValueTask.FromResult<object?>(TypedResults.NoContent());
        });

        Assert.False(calledNext);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }
}
