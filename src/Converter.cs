namespace SevenZcvt;

internal readonly record struct ConvertResult(bool Ok, string Message, long SourceBytes, long ResultBytes)
{
    public static ConvertResult Fail(string message) => new(false, message, 0, 0);
}

internal static class Converter
{
    /// <summary>
    /// Paths this run must not overwrite: the inputs it was given plus every result it already
    /// produced. Two sources can map to the same name (aa.zip and aa.tar.gz both want aa.7z),
    /// and with -f the second one would otherwise destroy the first result.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    public static void Reserve(IEnumerable<string> paths)
    {
        foreach (string p in paths) Reserved.Add(Path.GetFullPath(p));
    }

    public static ConvertResult Convert(string source, string engine, Options o)
    {
        source = Path.GetFullPath(source);
        long sourceBytes = new FileInfo(source).Length;
        string outputDir = o.OutputDir is { Length: > 0 } d ? Path.GetFullPath(d) : Path.GetDirectoryName(source)!;
        Directory.CreateDirectory(outputDir);

        string output = PickOutput(source, outputDir, out bool inPlace);
        if (File.Exists(output) && !o.Force)
            return ConvertResult.Fail(inPlace ? "already a .7z (use -f to recompress)" : $"{output} exists (use -f)");

        string work = Path.Combine(Path.GetTempPath(), "7zcvt-" + Guid.NewGuid().ToString("N")[..12]);
        string content = Path.Combine(work, "c");
        // Staged next to the final file: same volume, so the last step is a rename, not a copy.
        string staged = Path.Combine(outputDir, "." + Path.GetFileName(output) + ".7zcvt" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(content);

            var (code, error) = Extract(source, content, engine, work);
            if (code != 0)
                return ConvertResult.Fail($"extract failed ({code}) {error}");

            long extracted = DirectorySize(content);
            if (extracted > o.MaxBytes)
                return ConvertResult.Fail($"contents are {Program.Format(extracted)}, over the --max-size limit");
            if (!Directory.EnumerateFileSystemEntries(content).Any())
                return ConvertResult.Fail("archive is empty");

            if (o.Depth > 0)
                RepackNested(content, engine, o, o.Depth, work);

            (code, error) = Pack(content, staged, engine, o.Level, work);
            if (code != 0)
                return ConvertResult.Fail($"pack failed ({code}) {error}");

            (code, error) = SevenZip.Run(engine, "t", "-p", "-bso0", "-bsp0", staged);
            if (code != 0)
                return ConvertResult.Fail($"result failed verification ({code}) {error}");

            long resultBytes = new FileInfo(staged).Length;
            if (o.OnlySmaller && resultBytes >= sourceBytes)
                return new ConvertResult(true,
                    $"skip  {source}: {Program.Format(resultBytes)} is not smaller than {Program.Format(sourceBytes)}",
                    sourceBytes, sourceBytes);

            // Every file that went in has to be in the result, with the same size.
            string? mismatch = Parity(content, staged, engine);
            if (mismatch is not null && (inPlace || File.Exists(output)))
                return ConvertResult.Fail($"{mismatch}; nothing was overwritten and the source is untouched");

            Reserved.Add(output);
            File.Move(staged, output, overwrite: true);

            if (mismatch is not null)
                return new ConvertResult(true,
                    $"warn  {source} -> {output}  kept both: {mismatch}",
                    sourceBytes, resultBytes);

            if (o.DeleteSource && !inPlace)
            {
                // Only ever after the result exists, verifies and matches the source contents.
                if (!File.Exists(output) || new FileInfo(output).Length != resultBytes)
                    return ConvertResult.Fail("result vanished before the source could be deleted; source kept");
                File.Delete(source);
            }

            return new ConvertResult(true,
                $"ok    {source} -> {output}  {Program.Format(sourceBytes)} -> {Program.Format(resultBytes)} ({Program.Percent(sourceBytes, resultBytes)})",
                sourceBytes, resultBytes);
        }
        catch (Exception ex)
        {
            return ConvertResult.Fail(ex.Message);
        }
        finally
        {
            if (File.Exists(staged)) TryDeleteFile(staged);
            TryDelete(work);
        }
    }

    /// <summary>Picks a result name that collides with neither another input nor an earlier result.</summary>
    private static string PickOutput(string source, string outputDir, out bool inPlace)
    {
        string candidate = Path.Combine(outputDir, StripExtension(Path.GetFileName(source)) + ".7z");
        inPlace = string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase);
        if (inPlace || !Reserved.Contains(candidate)) return candidate;

        // aa.tar.gz -> aa.tar.gz.7z, then aa.tar.gz (2).7z ...
        candidate = Path.Combine(outputDir, Path.GetFileName(source) + ".7z");
        for (int n = 2; Reserved.Contains(candidate); n++)
            candidate = Path.Combine(outputDir, $"{Path.GetFileName(source)} ({n}).7z");
        return candidate;
    }

    /// <summary>Replaces every archive inside <paramref name="dir"/> with a 7z of its contents.</summary>
    private static void RepackNested(string dir, string engine, Options o, int depth, string workRoot)
    {
        if (depth <= 0) return;

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(Program.IsArchive).ToList())
        {
            if (Path.GetExtension(file).Equals(".7z", StringComparison.OrdinalIgnoreCase))
                continue; // already the target format

            // Scratch space lives outside the extracted tree: a folder inside the archive that
            // happens to share the name must never be treated as ours and deleted.
            string work = Path.Combine(workRoot, "n" + Guid.NewGuid().ToString("N")[..8]);
            string content = Path.Combine(work, "c");
            try
            {
                Directory.CreateDirectory(content);
                var (code, _) = Extract(file, content, engine, work);
                if (code != 0 || !Directory.EnumerateFileSystemEntries(content).Any())
                    continue; // encrypted, damaged or empty: keep the nested archive as it is

                RepackNested(content, engine, o, depth - 1, work);

                string target = Path.Combine(Path.GetDirectoryName(file)!, StripExtension(Path.GetFileName(file)) + ".7z");
                if (File.Exists(target) || Directory.Exists(target)) target = file + ".7z";
                if (File.Exists(target) || Directory.Exists(target)) continue; // do not clobber archive contents

                string staged = Path.Combine(work, "out.7z");
                (code, _) = Pack(content, staged, engine, o.Level, work);
                if (code != 0) continue;
                (code, _) = SevenZip.Run(engine, "t", "-p", "-bso0", "-bsp0", staged);
                if (code != 0) continue;
                if (Parity(content, staged, engine) is { } mismatch)
                {
                    Program.Log($"      nested kept as is: {Path.GetRelativePath(dir, file)} ({mismatch})");
                    continue;
                }

                File.Move(staged, target);
                File.Delete(file);
                Program.Log($"      nested: {Path.GetRelativePath(dir, file)} -> {Path.GetFileName(target)}");
            }
            catch (IOException)
            {
                // A nested archive that cannot be handled stays untouched.
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDelete(work);
            }
        }
    }

    /// <summary>
    /// Compares the packed archive against the tree it was built from: same relative paths, same
    /// sizes. Returns null when they match, otherwise what differs. This is the check that stands
    /// between a silent extraction gap and <c>--delete</c>.
    /// </summary>
    private static string? Parity(string contentDir, string archive, string engine)
    {
        var (code, error, listing) = SevenZip.RunWithOutput(engine, "l", "-ba", "-slt", "-p", archive);
        if (code != 0) return $"cannot list the result ({code}) {error}";

        var inArchive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? path = null;
        long size = -1;
        bool isDir = false;

        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path is not null && !isDir) inArchive[Normalize(path)] = size;
                path = null; size = -1; isDir = false;
                continue;
            }

            if (line.StartsWith("Path = ", StringComparison.Ordinal)) path = line[7..];
            else if (line.StartsWith("Size = ", StringComparison.Ordinal)) long.TryParse(line[7..], out size);
            else if (line.StartsWith("Folder = ", StringComparison.Ordinal)) isDir |= line[9..] == "+";
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal)) isDir |= line[13..].StartsWith('D');
        }

        if (path is not null && !isDir) inArchive[Normalize(path)] = size;

        foreach (string file in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
        {
            string relative = Normalize(Path.GetRelativePath(contentDir, file));
            if (!inArchive.TryGetValue(relative, out long stored))
                return $"'{relative}' is missing from the result";

            long actual = new FileInfo(file).Length;
            if (stored != actual)
                return $"'{relative}' is {actual} bytes on disk but {stored} in the result";
        }

        return null;

        static string Normalize(string p) => p.Replace('\\', '/').TrimStart('.', '/');
    }

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="dest"/>. A single-stream container
    /// (aa.tar.gz, aa.tgz) is peeled down to its files instead of leaving an intermediate .tar.
    /// </summary>
    private static (int Code, string Error) Extract(string archive, string dest, string engine, string workRoot)
    {
        var result = SevenZip.Run(engine, "x", "-y", "-p", "-bso0", "-bsp0", "-o" + dest, archive);
        if (result.Code != 0) return result;

        if (!SingleStream.Contains(Path.GetExtension(archive))) return result;

        var entries = Directory.GetFileSystemEntries(dest);
        if (entries.Length != 1 || !File.Exists(entries[0]) || !Program.IsArchive(entries[0])) return result;

        string inner = Path.Combine(workRoot, "inner" + Path.GetExtension(entries[0]));
        File.Move(entries[0], inner);
        try
        {
            var unwrapped = SevenZip.Run(engine, "x", "-y", "-p", "-bso0", "-bsp0", "-o" + dest, inner);
            if (unwrapped.Code != 0) File.Move(inner, entries[0]); // put the layer back untouched
            return result;
        }
        finally
        {
            if (File.Exists(inner)) File.Delete(inner);
        }
    }

    private static readonly HashSet<string> SingleStream = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".z", ".taz", ".lzma", ".zst", ".tzst",
    };

    private static (int Code, string Error) Pack(string contentDir, string archive, string engine, int level, string workRoot) =>
        SevenZip.Run(engine, "a", "-t7z", "-y", $"-mx={level}", "-ms=on", "-mmt=on", "-bso0", "-bsp0",
            "-w" + workRoot, archive, contentDir + Path.DirectorySeparatorChar + "*");

    /// <summary>aa.zip -> aa, aa.tar.gz -> aa (the .tar layer is unpacked too).</summary>
    private static string StripExtension(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static long DirectorySize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
