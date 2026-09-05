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

    // Container formats worth repacking. Disk images, installers, firmware images
    // and .jar are deliberately excluded: unpacking them changes their meaning.
    // A .jar named on the command line is still converted; it is only skipped when
    // found inside another archive.
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".zip", ".zipx", ".rar", ".arj", ".lzh", ".lha", ".cab",
        ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".z", ".taz",
        ".lzma", ".zst", ".tzst",
    };

    private static bool _quiet;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "-?" or "/?" or "/h" or "/help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "-v" or "--version" or "/v" or "/version")
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

        if (args.Any(a => Flag(a) == "--selftest"))
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
            // Every option has a short and a long form; on Windows /flag works as well.
            // A value may follow the option or be attached with '=' (--depth 4, --depth=4).
            string a = Flag(args[i]);
            string? inline = null;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('=') is int eq && eq > 0)
            {
                inline = a[(eq + 1)..];
                a = a[..eq];
            }

            string Next(string name) => inline ?? (++i < args.Length ? args[i]
                : throw new ArgumentException($"{name} requires a value"));

            switch (a)
            {
                case "--selftest": break;
                case "-r" or "--recurse" or "--recursive": o.ScanDirs = true; break;
                case "-f" or "--force": o.Force = true; break;
                case "-q" or "--quiet": o.Quiet = true; break;
                case "-D" or "--delete": o.DeleteSource = true; break;
                case "-s" or "--only-smaller": o.OnlySmaller = true; break;
                case "-o" or "--output": o.OutputDir = Next(a); break;
                case "-e" or "--engine" or "--7z": o.EnginePath = Next(a); break;
                case "-d" or "--depth": o.Depth = ParseInt(Next(a), 0, 64); break;
                case "-l" or "--level": o.Level = ParseInt(Next(a), 0, 9); break;
                case "-M" or "--max-size": o.MaxBytes = ParseSize(Next(a)); break;
                default:
                    if (a.StartsWith("-mx", StringComparison.Ordinal)) // 7-Zip style: -mx9, -mx=9
                        o.Level = ParseInt(a[3..].TrimStart('='), 0, 9);
                    else if (a.StartsWith('-') && a.Length > 1)
                        throw new ArgumentException($"unknown option '{args[i]}'");
                    else
                        o.Inputs.Add(args[i]);
                    break;
            }
        }

        return o;
    }

    /// <summary>Turns a Windows-style /flag into -flag or --flag; leaves paths and everything else alone.</summary>
    private static string Flag(string arg)
    {
        if (!OperatingSystem.IsWindows() || arg.Length < 2 || arg[0] != '/') return arg;
        string name = arg[1..];
        if (name.Contains('/') || name.Contains('\\') || name == "?") return arg;
        return name.Length == 1 || name.StartsWith("mx", StringComparison.Ordinal) ? "-" + name : "--" + name;
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

          -o, --output DIR     write results to DIR (default: next to the source)
          -r, --recurse        scan input directories recursively for archives
          -d, --depth N        repack archives nested up to N levels deep (default 8, 0 = off)
          -l, --level N        compression level 0..9 (default 9); -mx9 also works
          -f, --force          overwrite an existing .7z
          -D, --delete         delete the source after the result is verified
          -s, --only-smaller   discard the result when it is not smaller than the source
          -M, --max-size SIZE  abort an archive whose contents exceed SIZE (default 10g)
          -e, --engine PATH    path to the 7z executable to use (--7z is an alias)
          -q, --quiet          quiet
          -h, --help           this text
          -v, --version        print version
              --selftest       run a built-in end-to-end check

        A long option takes its value either way: --depth 4 or --depth=4.
        On Windows the /flag form works too: /r, /depth 4, /delete.

        Examples:
          7zcvt aa.zip                   aa.zip  -> aa.7z
          7zcvt -r -f -D D:\arc          repack a whole tree in place
          7zcvt --recurse --force --delete D:\arc   the same, long form
        """);
}
