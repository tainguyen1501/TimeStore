using Microsoft.OpenApi;

namespace TimeStore.Server.Admin;

/// <summary>
/// The OpenAPI document of the admin API (<see cref="AdminSettings.OpenApi"/>). Stable across runs, so the committed
/// copy (<c>docs/api/openapi.v1.json</c>) can be compared byte for byte: no server URL (it would carry the port).
/// OpenAPI 3.1, the version the frontend generator (openapi-typescript) reads.
/// </summary>
internal static class AdminOpenApi
{
    public const string Title = "TimeStore admin API";

    public static void AddAdminOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(openApi =>
        {
            openApi.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            openApi.AddDocumentTransformer((document, _, _) =>
            {
                document.Servers = [];
                document.Info.Title = Title;
                return Task.CompletedTask;
            });
        });
}
