namespace octo_fiesta.Middleware;

/// <summary>
/// Middleware that enables request body buffering to allow multiple reads.
/// This is necessary for the proxy to forward POST request bodies after
/// they may have been read by ASP.NET's model binding.
/// </summary>
public class RequestBodyBufferingMiddleware
{
    private readonly RequestDelegate _next;

    public RequestBodyBufferingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Enable buffering so the body can be read multiple times
        context.Request.EnableBuffering();
        await _next(context);
    }
}

/// <summary>
/// Extension method to register the middleware.
/// </summary>
public static class RequestBodyBufferingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestBodyBuffering(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestBodyBufferingMiddleware>();
    }
}
