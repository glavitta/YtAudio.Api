using System.Security.Cryptography;
using System.Text;

namespace YtAudio.Api.Middleware
{
    public class ApiKeyMiddleware(RequestDelegate next)
    {
        private const string HeaderName = "X-Api-Key";

        public async Task InvokeAsync(HttpContext context, IConfiguration config)
        {
            if (context.Request.Path.StartsWithSegments("/hangfire"))
            {
                await next(context);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/scalar") || context.Request.Path.StartsWithSegments("/openapi"))
            {
                if (!context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(context);
                return;
            }

            if (!context.Request.Headers.TryGetValue(HeaderName, out var provided))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = $"Missing {HeaderName} header." });
                return;
            }

            var expected = config["Security:ApiKey"] ?? string.Empty;

            if (!ConstantTimeEquals(provided.ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
                return;
            }

            await next(context);
        }
        private static bool ConstantTimeEquals(string a, string b)
        {
            const int fixedLength = 128;
            var bytesA = Encoding.UTF8.GetBytes(a.PadRight(fixedLength)[..fixedLength]);
            var bytesB = Encoding.UTF8.GetBytes(b.PadRight(fixedLength)[..fixedLength]);

            return CryptographicOperations.FixedTimeEquals(bytesA, bytesB)
                   && a.Length == b.Length; 
        }
    }
}
