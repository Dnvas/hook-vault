namespace HookVault.Middleware;

// Caps the captured request body. Reads HOOKVAULT_MAX_BODY_BYTES per request so
// the value can be changed between test runs in the same process without a static
// cache causing cross-test interference. In production the env var is fixed at
// startup, so per-request reads are effectively free. When unset, zero, or
// unparseable, no cap is applied. Runs after RawBodyMiddleware so the body
// length is already known from HttpContext.Items.
public sealed class MaxBodySizeMiddleware(RequestDelegate next, ILogger<MaxBodySizeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var maxBytes = ReadCap();
        if (maxBytes > 0 &&
            context.Items[RawBodyMiddleware.RawBodyKey] is byte[] body &&
            body.Length > maxBytes)
        {
            logger.LogWarning(
                "Rejected oversize ingest: {Bytes}B > cap {Cap}B on {Path}",
                body.Length, maxBytes, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Request body exceeds HOOKVAULT_MAX_BODY_BYTES cap of {maxBytes} bytes.",
                code = "body_too_large",
            });
            return;
        }

        await next(context);
    }

    private static int ReadCap()
    {
        var raw = Environment.GetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES");
        return int.TryParse(raw, out var n) && n > 0 ? n : 0;
    }
}
