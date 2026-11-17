using System.Reflection;
using System.Runtime.CompilerServices;
using TimeStore.Infra.Services.Buckets;

namespace TimeStore.UnitTests.Conventions;

/// <summary>
/// Verifies that Runtime Async (Directory.Build.props: Features runtime-async=on) is active. With it, the compiler
/// leaves async methods to the runtime and emits no state machine, so the method carries no
/// <see cref="AsyncStateMachineAttribute"/>. If this fails, the property is not taking effect: check the .NET 11
/// docs before changing it.
/// </summary>
public sealed class RuntimeAsyncTests
{
    [Fact]
    public void AsyncMethods_AreCompiledForRuntimeAsync()
    {
        var method = typeof(BucketService).GetMethod(nameof(BucketService.CreateAsync))!;

        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }
}
