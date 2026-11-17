using TimeStore.Protocol;

namespace TimeStore.Server.S3;

/// <summary>
/// Success responses on the S3 port. XML documents are written by <c>TimeStore.Protocol.Xml.S3XmlWriter</c>; HEAD
/// responses never carry a body. Errors go through <see cref="S3Errors"/>.
/// </summary>
internal static class S3Responses
{
    public static Task XmlAsync(HttpContext context, Func<Stream, Task> write, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = S3ContentTypes.Xml;
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : write(context.Response.Body);
    }

    /// <summary>Status only (CreateBucket 200, DeleteBucket 204, HeadBucket 200).</summary>
    public static Task EmptyAsync(HttpContext context, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}
