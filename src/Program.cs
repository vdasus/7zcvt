using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SevenZcvt;

internal sealed class Options
{
    public List<string> Inputs { get; } = new();
    public string? OutputDir;
    public bool ScanDirs;          // -r : walk input directories looking for archives
    public int Depth = 8;          // -d : how deep to repack nested archives
    public int Level = 9;          // -mx
    public bool Force;             // -f : overwrite existing .7z
    public bool DeleteSource;      // --delete
    public bool OnlySmaller;       // --only-smaller
    public bool Quiet;             // -q
    public string? EnginePath;     // --7z
    public long MaxBytes = 10L * 1024 * 1024 * 1024; // --max-size, per archive
}

internal static class Program
{
    /// <summary>Single source of truth: the &lt;Version&gt; property in 7zcvt.csproj.</summary>
    public static readonly string Version = typeof(Program).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";

    public const string Author = "vdasus";

    // Container formats worth repacking. Disk images, installers and firmware
    // images are deliberately excluded: unpacking them changes their meaning.
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".zip", ".zipx", ".jar", ".rar", ".arj", ".lzh", ".lha", ".cab",
        ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".z", ".taz",
        ".lzma", ".zst", ".tzst",
    };

    private static bool _quiet;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] == "--version")
        {
            Console.WriteLine($"7zcvt {Version} by {Author}");
            return 0;
        }

        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"7zcvt: {ex.Message}");
            return 2;
        }

        _quiet = options.Quiet;

        string engine;
        try
        {
            engine = SevenZip.Resolve(options.EnginePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"7zcvt: {ex.Message}");
            return 3;
        }

        Log($"engine: {engine}");

        if (args[0] == "--selftest")
            return SelfTest.Run(engine) ? 0 : 1;

        var targets = ExpandInputs(options);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("7zcvt: no input archives found");
            return 2;
        }

        Converter.Reserve(targets); // never overwrite another input or a result already produced
        int failed = 0;
        long before = 0, after = 0;
        foreach (var target in targets)
        {
            var result = Converter.Convert(target, engine, options);
            if (!result.Ok)
            {
                failed++;
                Console.Error.WriteLine($"FAIL  {target}: {result.Message}");
                continue;
            }

            before += result.SourceBytes;
            after += result.ResultBytes;
            Console.WriteLine(result.Message);
        }

        if (targets.Count > 1 && before > 0)
            Console.WriteLine($"total: {Format(before)} -> {Format(after)} ({Percent(before, after)})");

        return failed == 0 ? 0 : 1;
    }

    private static Options ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name) => ++i < args.Length ? args[i] : throw new ArgumentException($"{name} requires a value");

            switch (a)
            {
                case "--selftest": break;
                case "-r": o.ScanDirs = true; break;
                case "-f": o.Force = true; break;
                case "-q": o.Quiet = true; break;
                case "--delete": o.DeleteSource = true; break;
                case "--only-smaller": o.OnlySmaller = true; break;
                case "-o": o.OutputDir = Next("-o"); break;
                case "--7z": o.EnginePath = Next("--7z"); break;
                case "-d": o.Depth = ParseInt(Next("-d"), 0, 64); break;
                case "--max-size": o.MaxBytes = ParseSize(Next("--max-size")); break;
                default:
                    if (a.StartsWith("-mx", StringComparison.Ordinal))
                        o.Level = ParseInt(a[3..].TrimStart('='), 0, 9);
                    else if (a.StartsWith('-') && a.Length > 1)
                        throw new ArgumentException($"unknown option '{a}'");
                    else
                        o.Inputs.Add(a);
                    break;
            }
        }

        return o;
    }

    private static int ParseInt(string s, int min, int max) =>
        int.TryParse(s, out int v) && v >= min && v <= max
            ? v
            : throw new ArgumentException($"expected a number {min}..{max}, got '{s}'");

    private static long ParseSize(string s)
    {
        long mult = s.Length > 0
            ? char.ToLowerInvariant(s[^1]) switch { 'k' => 1024L, 'm' => 1024L * 1024, 'g' => 1024L * 1024 * 1024, _ => 1 }
            : 1;
        string digits = mult == 1 ? s : s[..^1];
        return long.TryParse(digits, out long v) && v > 0
            ? v * mult
            : throw new ArgumentException($"bad size '{s}'");
    }

    private static List<string> ExpandInputs(Options o)
    {
        var files = new List<string>();
        foreach (string input in o.Inputs)
        {
            if (Directory.Exists(input))
            {
                var search = o.ScanDirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                files.AddRange(Directory.EnumerateFiles(input, "*", search).Where(IsArchive));
                continue;
            }

            // Explicit path or a glob such as *.zip; the shell may not have expanded it.
            string dir = Path.GetDirectoryName(input) is { Length: > 0 } d ? d : ".";
            string mask = Path.GetFileName(input);
            if (mask.Contains('*') || mask.Contains('?'))
                files.AddRange(Directory.EnumerateFiles(dir, mask).Where(IsArchive));
            else if (File.Exists(input))
                files.Add(input);
            else
                Console.Error.WriteLine($"7zcvt: not found: {input}");
        }

        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
    }

    internal static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    internal static void Log(string message)
    {
        if (!_quiet) Console.WriteLine(message);
    }

    internal static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GiB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F2} MiB",
        >= 1024 => $"{bytes / 1024.0:F1} KiB",
        _ => $"{bytes} B",
    };

    internal static string Percent(long before, long after) =>
        before <= 0 ? "n/a" : $"{(after - before) * 100.0 / before:+0.0;-0.0;0.0}%";

    private static void PrintUsage() => Console.WriteLine($"""
        7zcvt {Version} by {Author} - repack archives into 7z, nested archives included.

        Usage: 7zcvt [options] <file|dir|mask>...

          -o DIR          write results to DIR (default: next to the source)
          -r              scan input directories recursively for archives
          -d N            repack archives nested up to N levels deep (default 8, 0 = off)
          -mxN            compression level 0..9 (default 9)
          -f              overwrite an existing .7z
          --delete        delete the source after the result is verified
          --only-smaller  discard the result when it is not smaller than the source
          --max-size SIZE abort an archive whose contents exceed SIZE (default 10g)
          --7z PATH       path to the 7z executable to use
          -q              quiet
          --selftest      run a built-in end-to-end check
          --version       print version

        Examples:
          7zcvt aa.zip                  aa.zip  -> aa.7z
          7zcvt -r -f --delete D:\arc   repack a whole tree in place
        """);
}
