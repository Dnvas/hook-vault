namespace HookVault.Middleware;

// Signature validation requires the exact raw bytes of the request body —
// any re-serialisation would break the HMAC. This middleware reads the body
// into a byte array and stashes it in HttpContext.Items before controllers run.
// We also replace Request.Body with a rewindable MemoryStream so the model
// binder can read the body a second time.
public sealed class RawBodyMiddleware(RequestDelegate next)
{
    public const string RawBodyKey = "RawBody";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var rawBytes = ms.ToArray();

        context.Items[RawBodyKey] = rawBytes;

        context.Request.Body.Position = 0;

        await next(context);
    }
}
