using System.Security.Cryptography;

namespace SevenZcvt;

/// <summary>End-to-end check: build a nested archive, convert it, verify the contents survive.</summary>
internal static class SelfTest
{
    public static bool Run(string engine)
    {
        string root = Path.Combine(Path.GetTempPath(), "7zcvt-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string stage = Path.Combine(root, "stage");
            string inner = Path.Combine(stage, "inner");
            Directory.CreateDirectory(inner);

            File.WriteAllText(Path.Combine(stage, "top.txt"), new string('a', 50_000));
            File.WriteAllText(Path.Combine(inner, "deep.txt"), new string('b', 50_000));
            File.WriteAllBytes(Path.Combine(inner, "bin.dat"), RandomNumberGenerator.GetBytes(4096));

            string innerZip = Path.Combine(stage, "inner.zip");
            Check(SevenZip.Run(engine, "a", "-tzip", "-y", "-bso0", "-bsp0", innerZip, inner + Path.DirectorySeparatorChar + "*").Code == 0,
                "cannot build inner.zip");
            Directory.Delete(inner, recursive: true);

            var expected = Hashes(stage);

            string outerZip = Path.Combine(root, "outer.zip");
            Check(SevenZip.Run(engine, "a", "-tzip", "-y", "-bso0", "-bsp0", outerZip, stage + Path.DirectorySeparatorChar + "*").Code == 0,
                "cannot build outer.zip");

            var result = Converter.Convert(outerZip, engine, new Options { Depth = 4, Level = 1 });
            Check(result.Ok, "conversion failed: " + result.Message);

            string outer7z = Path.Combine(root, "outer.7z");
            Check(File.Exists(outer7z), "outer.7z was not produced");

            string back = Path.Combine(root, "back");
            Check(SevenZip.Run(engine, "x", "-y", "-p", "-bso0", "-bsp0", "-o" + back, outer7z).Code == 0,
                "cannot extract the result");

            Check(File.Exists(Path.Combine(back, "inner.7z")), "the nested inner.zip was not repacked to inner.7z");
            Check(!File.Exists(Path.Combine(back, "inner.zip")), "the nested inner.zip is still there");

            string nested = Path.Combine(root, "nested");
            Check(SevenZip.Run(engine, "x", "-y", "-p", "-bso0", "-bsp0", "-o" + nested, Path.Combine(back, "inner.7z")).Code == 0,
                "cannot extract the nested result");

            var actual = Hashes(back);
            foreach (var (name, hash) in expected)
            {
                if (name == "inner.zip") continue; // by design it is inner.7z now
                Check(actual.TryGetValue(name, out string? got) && got == hash, $"content of '{name}' changed");
            }

            Check(Hashes(nested).Count == 2, "the nested archive lost files");
            Check(File.ReadAllText(Path.Combine(nested, "deep.txt")).Length == 50_000, "deep.txt is damaged");

            NameCollision(engine, Path.Combine(root, "collide"));
            BrokenSource(engine, Path.Combine(root, "broken"));

            Console.WriteLine("selftest: ok");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selftest: FAILED - " + ex.Message);
            return false;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Two sources that want the same result name must not overwrite each other.</summary>
    private static void NameCollision(string engine, string dir)
    {
        string stage = Path.Combine(dir, "stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "from-zip.txt"), "zip");
        string zip = Path.Combine(dir, "aa.zip");
        Check(SevenZip.Run(engine, "a", "-tzip", "-y", "-bso0", "-bsp0", zip, Path.Combine(stage, "from-zip.txt")).Code == 0, "cannot build aa.zip");

        File.Delete(Path.Combine(stage, "from-zip.txt"));
        File.WriteAllText(Path.Combine(stage, "from-tar.txt"), "tar");
        string tgz = Path.Combine(dir, "aa.tar.gz");
        Check(SevenZip.Run(engine, "a", "-tgzip", "-y", "-bso0", "-bsp0", tgz,
            Path.Combine(stage, "from-tar.txt")).Code == 0 || File.Exists(tgz), "cannot build aa.tar.gz");

        var options = new Options { Depth = 2, Level = 1, Force = true, DeleteSource = true };
        var inputs = new[] { zip, tgz }.Where(File.Exists).ToArray();
        Converter.Reserve(inputs);
        foreach (string input in inputs)
            Check(Converter.Convert(input, engine, options).Ok, "collision case failed to convert " + input);

        foreach (string input in inputs)
            Check(!File.Exists(input), "source survived --delete: " + input);

        var results = Directory.GetFiles(dir, "*.7z");
        Check(results.Length == inputs.Length, $"expected {inputs.Length} results, found {results.Length}: one overwrote another");
    }

    /// <summary>A source that cannot be extracted must be left alone and produce no result.</summary>
    private static void BrokenSource(string engine, string dir)
    {
        Directory.CreateDirectory(dir);
        string broken = Path.Combine(dir, "broken.zip");
        File.WriteAllBytes(broken, RandomNumberGenerator.GetBytes(2048));
        byte[] before = File.ReadAllBytes(broken);

        var result = Converter.Convert(broken, engine, new Options { Level = 1, Force = true, DeleteSource = true });
        Check(!result.Ok, "a broken archive was reported as converted");
        Check(File.Exists(broken) && File.ReadAllBytes(broken).SequenceEqual(before), "a broken source was damaged");
        Check(!File.Exists(Path.Combine(dir, "broken.7z")), "a broken archive produced a result");
        Check(Directory.GetFiles(dir).Length == 1, "the failed run left files behind: " +
            string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
            f => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
}
