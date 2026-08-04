using System.Diagnostics;

namespace octo_fiesta.Services.YouTube;

public interface IYtDlpProcessRunner
{
    Task<YtDlpProcessRunner.ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class YtDlpProcessRunner : IYtDlpProcessRunner
{
    public sealed record ExecutionResult(int ExitCode, string StandardOutput, string StandardError);

    public async Task<ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ExecutionResult(process.ExitCode, stdout, stderr);
    }
}
