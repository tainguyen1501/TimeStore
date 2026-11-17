using TimeStore.Protocol.Http;
using System.Reflection;
using TimeStore.Application.Common;
using TimeStore.Application.Features.Buckets;
using TimeStore.Application.Features.Objects;
using TimeStore.Application.Features.Uploads;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;
using TimeStore.Protocol.Streaming;
using TimeStore.Protocol.Xml;
using TimeStore.Server.S3;

namespace TimeStore.Server.Tests.Conventions;

/// <summary>
/// Every error code that can reach the S3 port has an explicit S3 code and status: a new catalog entry without a
/// mapping would silently fall back to a generic error. Add new catalogs used on the S3 path to <see cref="Catalogs"/>.
/// </summary>
public sealed class S3ErrorMapTests
{
    private static readonly Type[] Catalogs =
    [
        typeof(S3RequestErrors), typeof(S3AuthErrors), typeof(S3BodyErrors), typeof(S3XmlErrors), typeof(BucketErrors),
        typeof(ObjectErrors),
        typeof(UploadErrors),
        typeof(S3HeaderErrors),
        typeof(CommonErrors),
    ];

    public static TheoryData<string> Codes()
    {
        var codes = new TheoryData<string>();
        foreach (var catalog in Catalogs)
        {
            var nested = catalog.GetNestedType("Codes", BindingFlags.Public)
                ?? throw new InvalidOperationException($"{catalog.Name} has no Codes class.");
            foreach (var field in nested.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
                codes.Add((string)field.GetRawConstantValue()!);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryCatalogCode_HasAnS3Mapping(string code) =>
        Assert.True(S3ErrorMap.IsMapped(code), $"Add {code} to S3ErrorMap.");
}
