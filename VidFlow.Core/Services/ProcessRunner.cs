using System.Diagnostics;
using System.Text;

namespace VidFlow.Services;

/// <summary>Runs the helper programs (ffmpeg, ffprobe, whisper-cli) with their output read line by line; they are
/// tied to VidFlow's job object, so they end with it.</summary>
public static class ProcessRunner
{
    /// <summary>Runs a program, passing each line it prints on (both streams, or with <paramref name="stdoutOnly"/>
    /// just its output: what it prints for errors is only kept for the result); stopped when canceled.</summary>
    public static async Task<(int Code, string Error)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine,
        CancellationToken ct, bool stdoutOnly = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var errors = new StringBuilder();
        void Line(string? text, bool error)
        {
            if (text is null) return;
            if (!error || !stdoutOnly) onLine?.Invoke(text);
            if (error) lock (errors) if (errors.Length < 16000) errors.AppendLine(text);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data, false);
        p.ErrorDataReceived += (_, e) => Line(e.Data, true);
        p.Start();
        ProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        lock (errors) return (p.ExitCode, errors.ToString().Trim());
    }
}
