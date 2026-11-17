using System.Text;
using System.Xml;

namespace TimeStore.Protocol;

/// <summary>
/// Writes an S3 error document. Hand-written with <see cref="XmlWriter"/>: no <c>XmlSerializer</c> (reflection,
/// not AOT friendly).
/// </summary>
public static class S3ErrorXml
{
    private static readonly XmlWriterSettings Settings = new()
    {
        Async = true,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    public static async Task WriteAsync(Stream destination, string code, string message, string resource, string requestId)
    {
        await using var writer = XmlWriter.Create(destination, Settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, S3Xml.Error, null);
        await writer.WriteElementStringAsync(null, S3Xml.Code, null, code);
        await writer.WriteElementStringAsync(null, S3Xml.Message, null, message);
        await writer.WriteElementStringAsync(null, S3Xml.Resource, null, resource);
        await writer.WriteElementStringAsync(null, S3Xml.RequestId, null, requestId);
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }
}
