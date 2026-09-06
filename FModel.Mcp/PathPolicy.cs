namespace FModel.Mcp;

public sealed class PathPolicy(ServerOptions options)
{
    public string Input(string path, bool directory = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Input paths must be absolute.");
        var full = Path.GetFullPath(path);
        if (!options.InputRoots.Any(root => IsWithin(root, full)))
            throw new ArgumentException("Path is outside configured inputRoots. Update the server configuration to allow it.");
        RejectLinks(full);
        if (directory ? !Directory.Exists(full) : !File.Exists(full))
            throw new FileNotFoundException(directory ? "Input directory does not exist." : "Input file does not exist.");
        return full;
    }

    public string NewOutputDirectory(string category)
    {
        RejectLinks(options.OutputRoot);
        Directory.CreateDirectory(options.OutputRoot);
        var path = Path.Combine(options.OutputRoot, category + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return string.Equals(root, path, comparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison);
    }

    public static string OutputFile(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, path)) throw new ArgumentException("Output path escapes its directory.");
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(p)))
            throw new ArgumentException("Invalid relative asset/output path.");
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }

    public static void RejectLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
        {
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links and junctions are not allowed in input/output paths.");
        }
    }

    public static void CheckInputTree(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Game directory contains a symbolic link or junction.");
                if ((item.Attributes & FileAttributes.Directory) != 0) pending.Push(item.FullName);
            }
        }
    }
}
