using System.Diagnostics;
using System.Reflection;

namespace SevenZcvt;

/// <summary>Locates a 7-Zip executable and runs it.</summary>
internal static class SevenZip
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    public static string Resolve(string? explicitPath)
    {
        if (explicitPath is { Length: > 0 })
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"7z not found at '{explicitPath}'");
            return Path.GetFullPath(explicitPath);
        }

        foreach (string candidate in Candidates())
            if (File.Exists(candidate))
                return candidate;

        return ExtractPayload() ?? throw new FileNotFoundException(
            "no 7-Zip found: install 7-Zip or pass --7z <path> (this build has no bundled engine)");
    }

    private static IEnumerable<string> Candidates()
    {
        string[] names = Windows ? ["7z.exe"] : ["7zz", "7z"];

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0)
                foreach (string name in names)
                    yield return Path.Combine(dir, name);

        if (!Windows) yield break;

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0) continue;
            yield return Path.Combine(root, "7-Zip", "7z.exe");
            yield return Path.Combine(root, "Programs", "7-Zip", "7z.exe");
        }
    }

    /// <summary>Unpacks the bundled engine into LocalAppData and returns its path.</summary>
    private static string? ExtractPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (assembly.GetManifestResourceInfo("payload/7z.exe") is null)
            return null;

        // Named after the bundled engine, not after 7zcvt: bumping the tool must not
        // leave another copy of the same 7-Zip behind.
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7zcvt");
        string dir = Path.Combine(root, "engine-" + EngineVersion);
        Directory.CreateDirectory(dir);
        RemoveStaleEngines(root, dir);

        string exe = Path.Combine(dir, "7z.exe");
        foreach (string name in new[] { "7z.exe", "7z.dll", "License.txt" })
        {
            string target = Path.Combine(dir, name);
            using var source = assembly.GetManifestResourceStream("payload/" + name);
            if (source is null) continue;
            if (File.Exists(target) && new FileInfo(target).Length == source.Length) continue;

            // A temp name plus a move keeps a half-written engine from being used.
            string temp = target + ".tmp" + Environment.ProcessId;
            using (var output = File.Create(temp))
                source.CopyTo(output);
            File.Move(temp, target, overwrite: true);
        }

        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Version of the 7-Zip build embedded in this executable (see assets/).</summary>
    private const string EngineVersion = "25.01";

    /// <summary>Drops engine folders left by earlier versions; one in use simply stays.</summary>
    private static void RemoveStaleEngines(string root, string current)
    {
        // Materialised first: deleting while enumerating makes the walk skip entries.
        foreach (string dir in Directory.GetDirectories(root, "engine-*"))
        {
            if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }              // still running from that copy
            catch (UnauthorizedAccessException) { }
        }
    }

    public static (int Code, string Error) Run(string engine, params string[] args)
    {
        var (code, error, _) = RunWithOutput(engine, args);
        return (code, error);
    }

    public static (int Code, string Error, string Output) RunWithOutput(string engine, params string[] args)
    {
        var psi = new ProcessStartInfo(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // never let 7z block on a password prompt
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"cannot start {engine}");
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string error = (stderr + stdout).Trim();
        if (error.Length > 400) error = error[^400..];
        return (process.ExitCode, error, stdout);
    }
}
