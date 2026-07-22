using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnvilDepth.Services;

public sealed record PbrSidecarResult(string RefinedDepthPath, string RefinedNormalPath, string? Note);

/// <summary>
/// Experimental bridge to an external Python process — a "second pass" architecture test, NOT
/// a real PBRFusion4 integration. PBRFusion4 (and similar diffusion-based PBR tools) run on
/// PyTorch/diffusers, which Microsoft.ML.OnnxRuntime cannot load directly. This class proves the
/// plumbing a real integration would need: spawning Python, handing it an image, and getting
/// processed results back — using a lightweight stub (PbrSidecar/pbr_sidecar_stub.py) that only
/// needs Pillow, not a multi-GB model download, so the bridge itself can be verified first.
///
/// To move from "stub test" to a real second pass: replace pbr_sidecar_stub.py's placeholder
/// logic with an actual call into PBRFusion4's model code (requires a Python environment with
/// torch + diffusers + the model's checkpoint). This C# side wouldn't need to change at all,
/// since it only cares that the script prints one line of JSON with the fields below.
/// </summary>
public sealed class PbrSidecarClient
{
    private readonly string _pythonExe;
    private readonly string _scriptPath;

    public PbrSidecarClient(string? pythonExe = null, string? scriptPath = null)
    {
        _pythonExe = pythonExe ?? "python";
        _scriptPath = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "PbrSidecar", "pbr_sidecar_stub.py");
    }

    public async Task<PbrSidecarResult> RunAsync(string imagePath)
    {
        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException(
                $"Sidecar script not found: {_scriptPath}\n" +
                "Make sure the PbrSidecar folder (with pbr_sidecar_stub.py) is copied next to the .exe, " +
                "same as the Models folder.");

        string outDir = Path.Combine(Path.GetTempPath(), "AnvilDepthPbrSidecar");
        Directory.CreateDirectory(outDir);

        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add(imagePath);
        psi.ArgumentList.Add(outDir);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Couldn't launch '{_pythonExe}'. Make sure Python is installed and on PATH " +
                $"(test by running \"{_pythonExe} --version\" in a terminal), or pass the full " +
                $"path to python.exe. Original error: {ex.Message}", ex);
        }

        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            // The stub prints a {"error": "..."} JSON line to stderr on failure — surface that
            // directly if present, since it's more useful than a bare exit code.
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"Sidecar exited with code {proc.ExitCode} and no error output."
                    : $"Sidecar failed: {stderr.Trim()}");
        }

        // Tolerate any incidental print()s before the result — take the last line that looks
        // like a JSON object rather than assuming stdout is exactly one line.
        string? jsonLine = null;
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("{")) jsonLine = trimmed;
        }
        if (jsonLine == null)
            throw new InvalidOperationException($"Sidecar produced no JSON result.\nOutput: {stdout}\nErrors: {stderr}");

        var result = JsonSerializer.Deserialize<PbrSidecarResult>(
            jsonLine, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result == null)
            throw new InvalidOperationException("Sidecar returned unparseable JSON: " + jsonLine);
        return result;
    }
}
