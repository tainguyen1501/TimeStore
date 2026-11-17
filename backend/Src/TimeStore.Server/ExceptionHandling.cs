using Microsoft.AspNetCore.Diagnostics;
using TimeStore.Application.Common;
using TimeStore.Protocol;
using TimeStore.Server.Admin;
using TimeStore.Server.S3;

namespace TimeStore.Server;

/// <summary>
/// Unexpected exceptions (bugs, I/O failures) become a well-formed error: S3 XML <c>InternalError</c> on the S3 port,
/// JSON <c>INTERNAL_ERROR</c> on the admin port. Business rules never throw; services return errors.
/// </summary>
internal static class ExceptionHandling
{
    public static WebApplication UseTimeStoreExceptionHandler(this WebApplication app, int s3Port)
    {
        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            if (context.Connection.LocalPort == s3Port)
            {
                await S3Errors.WriteAsync(
                    context, S3ErrorCodes.InternalError, S3Messages.InternalError,
                    StatusCodes.Status500InternalServerError);
                return;
            }

            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var error = exception is BadHttpRequestException ? CommonErrors.InvalidRequest : CommonErrors.Internal;
            await ApiResults.Error(error, context).ExecuteAsync(context);
        }));
        return app;
    }
}
