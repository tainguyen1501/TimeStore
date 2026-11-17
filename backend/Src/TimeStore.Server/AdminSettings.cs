using TimeStore.Application.Features.Auth;

namespace TimeStore.Server;

/// <summary>Section <c>TimeStore:Admin</c>: admin API sign-in.</summary>
public sealed class AdminSettings
{
    public const string Section = ServerOptions.Section + ":Admin";
    private const int MaxSessionHours = 30 * 24;

    /// <summary>How long a session lasts from sign-in.</summary>
    public int SessionHours { get; set; } = (int)AdminSessionOptions.DefaultLifetime.TotalHours;

    /// <summary>
    /// Mark the session cookie <c>Secure</c> even on plain HTTP requests: set it when a TLS proxy terminates HTTPS in
    /// front of the admin port. (Requests that arrive over HTTPS always get a secure cookie.)
    /// </summary>
    public bool SecureCookies { get; set; }

    /// <summary>
    /// Publish the OpenAPI document of the admin API at <c>/openapi/v1.json</c>. Off by default (a production server does
    /// not advertise its admin API); on in Development. The committed copy is <c>docs/api/openapi.v1.json</c>.
    /// </summary>
    public bool OpenApi { get; set; }

    /// <summary>
    /// Serve the admin UI from this directory instead of the copy embedded in the binary (a custom UI build; tests).
    /// </summary>
    public string? UiPath { get; set; }

    public TimeSpan SessionLifetime => TimeSpan.FromHours(SessionHours);

    public string? Validate()
    {
        if (SessionHours is < 1 or > MaxSessionHours)
            return $"{Section}:{nameof(SessionHours)} must be 1-{MaxSessionHours}.";
        if (!string.IsNullOrWhiteSpace(UiPath) && !File.Exists(Path.Combine(UiPath, "index.html")))
            return $"{Section}:{nameof(UiPath)} must be a directory holding index.html.";
        return null;
    }
}
