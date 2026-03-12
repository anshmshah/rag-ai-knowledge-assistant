using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace LocalRagAPI.Middleware
{
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var sw = Stopwatch.StartNew();
            var request = context.Request;

            _logger.LogInformation("Handling {Method} {Path}", request.Method, request.Path);

            await _next(context);

            sw.Stop();
            _logger.LogInformation("Handled {Method} {Path} in {Elapsed} ms with status {StatusCode}",
                request.Method, request.Path, sw.ElapsedMilliseconds, context.Response?.StatusCode);
        }
    }
}
