namespace YuSwitch.Endpoints;

/// <summary>Return a raw body with an explicit HTTP status code. Results.Text has
/// no status-code overload, so wrap it in an IResult that sets Response.StatusCode.</summary>
internal sealed class StatusTextResult : IResult
{
    private readonly string _body;
    private readonly string? _contentType;
    private readonly int _statusCode;

    public StatusTextResult(string body, string? contentType, int statusCode)
    {
        _body = body;
        _contentType = contentType;
        _statusCode = statusCode;
    }

    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = _statusCode;
        httpContext.Response.ContentType = _contentType ?? "text/plain";
        return httpContext.Response.WriteAsync(_body);
    }
}
