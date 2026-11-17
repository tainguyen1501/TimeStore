using System.Reflection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace TimeStore.Server.Admin;

/// <summary>
/// The admin UI (React build) on the admin port: embedded in the binary (<c>ui/</c>, from <c>frontend/dist</c>) or
/// read from <see cref="AdminSettings.UiPath"/>. <c>GET</c>/<c>HEAD</c> outside <c>/api</c>, <c>/health</c> and
/// <c>/openapi</c> only: a path naming a file is served or 404, any other path gets <c>index.html</c> so client routes
/// survive a reload. Hashed assets are cached for a year; the page itself is revalidated on every load.
/// </summary>
internal static class AdminUi
{
    private const string EmbeddedRoot = "ui";
    private const string IndexFile = "/index.html";
    private const string AssetsPrefix = "/assets";
    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const string NoCache = "no-cache";
    private const string NoSniff = "nosniff";
    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string NoReferrer = "no-referrer";
    private const string NotBuilt =
        "The admin UI is not part of this build. Build frontend/ (pnpm build) before the Server, or set TimeStore:Admin:UiPath.";

    /// <summary>
    /// Inline styles are allowed for the component library (positioning of popovers) and <c>data:</c> fonts for the
    /// small font files the build inlines; scripts come from this origin only (no inline script), and no other site may
    /// frame the page.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Only the UI's text files are compressed; API responses never are (secrets next to user input: BREACH).</summary>
    private static readonly string[] CompressedTypes =
        ["text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml", "application/json"];

    private static readonly PathString[] ServerPrefixes =
        [AdminRoutes.Prefix, HealthRoutes.Prefix, AdminRoutes.OpenApiPrefix];

    public static void AddAdminUi(this IServiceCollection services) =>
        services.AddResponseCompression(compression =>
        {
            compression.Providers.Add<BrotliCompressionProvider>();
            compression.Providers.Add<GzipCompressionProvider>();
            compression.MimeTypes = CompressedTypes;
        });

    public static void UseAdminUi(this WebApplication app, AdminSettings settings)
    {
        var files = OpenFiles(settings);
        app.MapWhen(IsUiRequest, ui =>
        {
            if (files is null)
            {
                ui.Run(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return context.Response.WriteAsync(NotBuilt);
                });
                return;
            }

            var options = new StaticFileOptions
            {
                FileProvider = files,
                ContentTypeProvider = new FileExtensionContentTypeProvider(),
                OnPrepareResponse = static file => AddHeaders(file.Context),
            };
            ui.UseResponseCompression();
            ui.UseStaticFiles(options);
            ui.Use((context, next) =>
            {
                // A file that does not exist is a 404; anything else is a client route of the single-page app.
                if (Path.HasExtension(context.Request.Path.Value))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                context.Request.Path = IndexFile;
                return next(context);
            });
            ui.UseStaticFiles(options);
        });
    }

    /// <summary>The configured directory, else the embedded build, else null (built without the UI).</summary>
    private static IFileProvider? OpenFiles(AdminSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.UiPath))
            return new PhysicalFileProvider(Path.GetFullPath(settings.UiPath));

        // The binary's own time stamp: changes with every release, so ETags change with the files.
        var built = Environment.ProcessPath is { } binary && File.Exists(binary)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(binary), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        try
        {
            var embedded = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), EmbeddedRoot, built);
            return embedded.GetFileInfo(IndexFile).Exists ? embedded : null;
        }
        catch (InvalidOperationException)
        {
            return null; // built without frontend/dist: the manifest has no ui/ directory
        }
    }

    private static bool IsUiRequest(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;
        foreach (var prefix in ServerPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix))
                return false;
        }

        return true;
    }

    private static void AddHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers[HeaderNames.CacheControl] = context.Request.Path.StartsWithSegments(AssetsPrefix) ? ImmutableCache : NoCache;
        headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
        headers[HeaderNames.XContentTypeOptions] = NoSniff;
        headers[ReferrerPolicyHeader] = NoReferrer;
    }
}
