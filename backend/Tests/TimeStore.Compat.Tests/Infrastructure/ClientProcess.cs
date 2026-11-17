using System.Diagnostics;
using System.Text;

namespace TimeStore.Compat.Tests.Infrastructure;

/// <summary>What a client command printed and returned.</summary>
public sealed record ClientResult(int ExitCode, string Output)
{
    /// <summary>Fails the test with the command's output (it never contains the secret key: that travels by environment).</summary>
    public ClientResult ShouldSucceed(string what)
    {
        Assert.True(ExitCode == 0, $"{what} exited with {ExitCode}:\n{Output}");
        return this;
    }
}

/// <summary>
/// Runs a client binary without a shell (arguments as a list, never concatenated). Secrets go through environment
/// variables, so they never appear in a command line or a test message.
/// </summary>
public static class ClientProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5); // the first run of an image includes the pull

    public static async Task<ClientResult> RunAsync(
        string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment, string? workingDirectory,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? Timeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish in {limit}:\n{Text(output)}");
        }

        process.WaitForExit(); // flush the asynchronous output readers
        return new ClientResult(process.ExitCode, Text(output));
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
            return;
        lock (output)
            output.AppendLine(line);
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }
}
