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
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            if (context.Connection.LocalPort == s3Port)
            {
                // Kestrel's own refusals of a body (too large for the request's limit, sent too slowly) are the
                // client's: answer them as S3 does, not as an internal error.
                var (code, message, status) = exception is BadHttpRequestException failure
                    ? failure.StatusCode switch
                    {
                        StatusCodes.Status413PayloadTooLarge => (S3ErrorCodes.MaxMessageLengthExceeded, S3Messages.MaxMessageLengthExceeded, StatusCodes.Status400BadRequest),
                        StatusCodes.Status408RequestTimeout => (S3ErrorCodes.RequestTimeout, S3Messages.RequestTimeout, StatusCodes.Status400BadRequest),
                        _ => (S3ErrorCodes.InternalError, S3Messages.InternalError, StatusCodes.Status500InternalServerError),
                    }
                    : (S3ErrorCodes.InternalError, S3Messages.InternalError, StatusCodes.Status500InternalServerError);
                await S3Errors.WriteAsync(context, code, message, status);
                return;
            }

            var error = exception is BadHttpRequestException ? CommonErrors.InvalidRequest : CommonErrors.Internal;
            await ApiResults.Error(error, context).ExecuteAsync(context);
        }));
        return app;
    }
}
