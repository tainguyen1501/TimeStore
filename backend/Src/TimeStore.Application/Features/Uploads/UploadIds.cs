namespace TimeStore.Application.Features.Uploads;

/// <summary>Upload ids on the wire: the UUIDv7 as 32 lower-case hex digits (sorts like the id itself).</summary>
public static class UploadIds
{
    private const string Format = "N";

    public static string ToText(Guid uploadId) => uploadId.ToString(Format);

    /// <summary>False for anything that is not exactly a <see cref="ToText"/> result (so it can never match an upload).</summary>
    public static bool TryParse(string? text, out Guid uploadId) =>
        Guid.TryParseExact(text, Format, out uploadId) && string.Equals(uploadId.ToString(Format), text, StringComparison.Ordinal);
}
