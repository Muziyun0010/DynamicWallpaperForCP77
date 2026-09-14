using System.IO.Compression;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ReleasePackager <published-main-dir> <wrapper-root> <output-zip>");
    return 2;
}

var mainDir = Path.GetFullPath(args[0]);
var wrapperRoot = Path.GetFullPath(args[1]);
var outputZip = Path.GetFullPath(args[2]);

if (!Directory.Exists(mainDir))
    throw new DirectoryNotFoundException(mainDir);

var clickHere = Path.Combine(wrapperRoot, "ClickHere.bat");
var readme = Path.Combine(wrapperRoot, "Readme.markdown");
if (!File.Exists(clickHere))
    throw new FileNotFoundException("Missing ClickHere.bat", clickHere);
if (!File.Exists(readme))
    throw new FileNotFoundException("Missing Readme.markdown", readme);

Directory.CreateDirectory(Path.GetDirectoryName(outputZip)!);
if (File.Exists(outputZip))
    File.Delete(outputZip);

static bool ShouldExclude(string path)
{
    var ext = Path.GetExtension(path);
    return ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
           ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
           ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
}

using (var zip = ZipFile.Open(outputZip, ZipArchiveMode.Create))
{
    foreach (var file in Directory.EnumerateFiles(mainDir, "*", SearchOption.AllDirectories))
    {
        if (ShouldExclude(file))
            continue;

        var relative = Path.GetRelativePath(mainDir, file).Replace('\\', '/');
        zip.CreateEntryFromFile(file, $"Main/{relative}", CompressionLevel.Optimal);
    }

    zip.CreateEntryFromFile(clickHere, "ClickHere.bat", CompressionLevel.Optimal);
    zip.CreateEntryFromFile(readme, "Readme.markdown", CompressionLevel.Optimal);
    zip.CreateEntry("PlaceYourMp4Here/");
}

using (var zip = ZipFile.OpenRead(outputZip))
{
    var names = zip.Entries.Select(e => e.FullName).ToArray();
    var forbidden = names.Where(n =>
        n.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();

    if (forbidden.Length > 0)
        throw new InvalidDataException("Forbidden files in release ZIP: " + string.Join(", ", forbidden));

    foreach (var required in new[]
             {
                 "ClickHere.bat",
                 "Readme.markdown",
                 "PlaceYourMp4Here/",
                 "Main/BK2maker.exe",
                 "Main/BK2maker.dll",
                 "Main/coreclr.dll",
                 "Main/hostfxr.dll",
                 "Main/hostpolicy.dll"
             })
    {
        if (!names.Contains(required, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Required entry missing: {required}");
    }

    Console.WriteLine($"Created: {outputZip}");
    Console.WriteLine($"Entries: {zip.Entries.Count}");
    Console.WriteLine($"Size: {new FileInfo(outputZip).Length:N0} bytes");
    Console.WriteLine("Release ZIP verification passed.");
}

return 0;
