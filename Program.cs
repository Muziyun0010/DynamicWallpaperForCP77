using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using WolvenKit;
using WolvenKit.Common.Services;
using WolvenKit.Helpers;
using WolvenKit.RED4.Archive.IO;
using System.Runtime.InteropServices;
using WolvenKit.RED4.Types;
using BK2maker;
using System.IO.Compression;
using System.Reflection.Emit;
using System.Collections.Concurrent;
using SharpDX.DXGI;

public class Bink2Maker
{
    static readonly string Root = AppContext.BaseDirectory;
    static readonly string RootX = Path.Combine(Root, "Resources");
    static readonly string VideoDir = Path.Combine(Directory.GetParent(Root).Parent.FullName, "PlaceYourMp4Here");
    static readonly string TempDir = Path.Combine(Root, "Temp");

    const string ffmpegResource = "BK2maker.ffmpeg.exe";
    const string ffprobeResource = "BK2maker.ffprobe.exe";
    static readonly string ffmpegPath = Path.Combine(RootX, "ffmpeg.exe");
    static readonly string ffprobePath = Path.Combine(RootX, "ffprobe.exe");

    const string radvideo64Resource = "BK2maker.radvideo64.exe";
    static readonly string radvideo64Path = Path.Combine(RootX, "radvideo64.exe");
    const string luaResource = "BK2maker.init.lua";
    static readonly string binPath = Path.Combine(Root, "output", "bin\\x64\\plugins\\cyber_engine_tweaks\\mods\\DynamicWallpaper");
    static readonly string luaPath = Path.Combine(binPath,"init.lua");

    static ConcurrentDictionary<string,int> videoInfos = new();

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                GenerateBk2Batch();
            }
            else
            {
                RunAsSubConsole();
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.Error(
                $"程序发生错误：{ex.GetBaseException().Message}",
                $"The program encountered an error: {ex.GetBaseException().Message}");
            Console.WriteLine(ex);
            ConsoleHelper.Quit();
            return 1;
        }
    }

    static void RunAsSubConsole()
    {
        ConsoleHelper.DisableFocusAndInput();

        if (Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "zh")
            Console.Title = "进度显示";
        else
            Console.Title = "Progress Display";

        Console.CursorVisible = false;

        SubConsoleMessenger.OnMessageReceived += ConsoleHelper.PrintBar;
        SubConsoleMessenger.InitAsServer();
        SubConsoleMessenger.Dispose();
    }

    public static void GenerateBk2Batch()
    {
        CleanupWorkingDirectories();
        Directory.CreateDirectory(TempDir);

        var mp4Files = Directory.EnumerateFiles(VideoDir, "*.mp4")
            .Take(30)
            .ToArray();
        ResourceExtractor.Generate(binPath, mp4Files.Length);

        if (mp4Files.Length == 0)
        {
            ConsoleHelper.Error("请将视频放入 PlaceYourMp4Here 文件夹", "Please put the video in the PlaceYourMp4Here folder");
            Console.ReadLine();
            return;
        }

        ConsoleHelper.Warn("最多转换三十个视频，HDR 视频将自动转换为 SDR", "Convert up to 30 videos; HDR videos will be automatically converted to SDR");
        ConsoleHelper.Info("开始预处理所有视频...", "Starting preprocessing all videos...");
        ConsoleHelper.Info("初始化资源...", "Extracting tools...");

        ResourceExtractor.ExtractIfSizeDiffers(ffmpegResource, ffmpegPath);
        ResourceExtractor.ExtractIfSizeDiffers(ffprobeResource, ffprobePath);
        ResourceExtractor.ExtractIfSizeDiffers(radvideo64Resource, radvideo64Path);
        ResourceExtractor.ExtractIfSizeDiffers(luaResource, luaPath);

        var outputDir = Path.Combine(Root, "archive", "dynamicWallpaper", "Video");
        Directory.CreateDirectory(outputDir);

        var tasks = new List<Task>();
        var failures = new ConcurrentBag<(string Name, Exception Error)>();

        // FFmpeg is CPU-heavy; running one encoder per logical core made large batches
        // slower and less reliable on user machines. Keep a small bounded pool instead.
        int maxFfmpegThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        using var ffmpegSemaphore = new SemaphoreSlim(maxFfmpegThreads);

        // RAD Video Tools opens a progress window for each conversion. Serialising this
        // stage avoids many UI windows racing each other and makes progress detection stable.
        using var radSemaphore = new SemaphoreSlim(1, 1);

        for (int i = 0; i < mp4Files.Length; i++)
        {
            int index = i;
            string fileName = Path.GetFileNameWithoutExtension(mp4Files[index]);
            string label = $"{index + 1:D2}:{TrimLabel(fileName)}";

            var nameIndex = index.ToString("D2");
            var sourceMp4 = mp4Files[index];

            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var enhancedMp4 = Path.Combine(TempDir, $"temp_{nameIndex}.mp4");

                    ffmpegSemaphore.Wait();
                    try
                    {
                        RunFfmpegWithFrameProgress(sourceMp4, enhancedMp4, label);
                    }
                    finally
                    {
                        ffmpegSemaphore.Release();
                    }

                    var outputBik = Path.Combine(outputDir, $"wallpaper_{nameIndex}.bk2");

                    radSemaphore.Wait();
                    try
                    {
                        ConsoleHelper.ResetProgress(label);
                        HiddenProcessRunner.RunProcess(
                            radvideo64Path,
                            $"binkc \"{enhancedMp4}\" \"{outputBik}\"",
                            nameIndex,
                            outputBik,
                            label);
                    }
                    finally
                    {
                        radSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((fileName, ex));
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        ConsoleHelper.CompleteProgressBars();

        if (!failures.IsEmpty)
        {
            foreach (var failure in failures.OrderBy(x => x.Name))
            {
                ConsoleHelper.Error(
                    $"{failure.Name} 转换失败：{failure.Error.GetBaseException().Message}",
                    $"Failed to convert {failure.Name}: {failure.Error.GetBaseException().Message}");
            }

            throw new InvalidOperationException($"{failures.Count} 个视频转换失败，已停止打包。 / {failures.Count} video(s) failed; packaging was stopped.");
        }

        ConsoleHelper.Info("所有视频转换完成", "All videos converted successfully");

        if (Directory.Exists(TempDir))
            Directory.Delete(TempDir, true);

        WolveKit.GetPack();
    }

    private static void CleanupWorkingDirectories()
    {
        foreach (var path in new[]
                 {
                     TempDir,
                     Path.Combine(Root, "archive"),
                     Path.Combine(Root, "output")
                 })
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private static string TrimLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";
        if (name.Length <= 10) return name;
        return name.Substring(0, 10) + "...";
    }
    private static void RunFfmpegWithFrameProgress(string input, string output, string label)
    {
        var info = GetVideoInfo(input);
        videoInfos.TryAdd(input,(int)info.TotalFrames);
        bool needResize = info.Width > 2560 || info.Height > 1440;
        bool isHdr = info.IsHdr;
        long totalFrames = info.TotalFrames;

        List<string> filters = new();
        if (isHdr)
        {
            filters.Add("eq=saturation = 8");
            filters.Add("zscale=transfer=bt709:primaries=bt709:matrix=bt709:dither=ordered");
            filters.Add("tonemap=tonemap=hable");
            filters.Add("eq=gamma = 1.1:contrast = 1.1");
        }
        else
        {
            var ffFilter = "eq=saturation=3.5,eq=contrast=1.1,curves=master='0/0 0.2/0.25 0.5/0.5 0.75/0.6 0.8/0.7 1/1'";
            filters.Add(ffFilter);
        }
        if (needResize)
        {
            ConsoleHelper.Warn($"{label} 分辨率超过 2K，正在缩放为 2560x1440", $"{label} resolution exceeds 2K, resizing to 2560x1440");
            filters.Add("scale=2560:1440");
        }


        string finalFilter = string.Join(",", filters);

        var psi = new ProcessStartInfo(ffmpegPath,
            $"-y -i \"{input}\" -vf \"{finalFilter}\" -c:v libx264 -pix_fmt yuv420p -preset slow -crf 18 -progress pipe:1 -nostats \"{output}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        ConsoleHelper.PrintProgressBar(label, 0, totalFrames);

        DateTime lastUpdate = DateTime.MinValue;

        while (!proc.StandardOutput.EndOfStream)
        {
            var line = proc.StandardOutput.ReadLine();
            if (line != null && line.StartsWith("frame="))
            {
                if (long.TryParse(line.Split('=')[1], out var current))
                {
                    var frame = Math.Clamp(current, 0, totalFrames);
                    var now = DateTime.Now;
                    if ((now - lastUpdate).TotalMilliseconds > 100)
                    {
                        ConsoleHelper.PrintProgressBar(label, frame, totalFrames);
                        lastUpdate = now;
                    }
                }
            }
            else if (line != null && line.StartsWith("progress=end"))
            {
                ConsoleHelper.PrintProgressBar(label, totalFrames, totalFrames);
                break;
            }
        }

        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"FFmpeg 退出码非零: {proc.ExitCode}");
    }

    private static VideoInfo GetVideoInfo(string input)
    {
        var args = $"-v error -select_streams v:0 -count_frames " +
                   "-show_entries stream=width,height,r_frame_rate,avg_frame_rate,nb_frames," +
                   "pix_fmt,color_space,color_transfer,color_primaries,duration " +
                   "-of json";

        var psi = new ProcessStartInfo(ffprobePath, args + $" \"{input}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        using var doc = JsonDocument.Parse(output);
        var stream = doc.RootElement.GetProperty("streams")[0];

        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();

        // 优先 nb_frames，其次 duration × avg_frame_rate
        int totalFrames = 1;
        if (stream.TryGetProperty("nb_frames", out var nbFramesProp) && nbFramesProp.ValueKind == JsonValueKind.String)
        {
            int.TryParse(nbFramesProp.GetString(), out totalFrames);
        }

        if (totalFrames <= 1 &&
            stream.TryGetProperty("duration", out var durationProp) &&
            stream.TryGetProperty("avg_frame_rate", out var fpsProp))
        {
            double.TryParse(durationProp.GetString(), out double duration);
            var fpsParts = fpsProp.GetString()?.Split('/');
            if (fpsParts?.Length == 2 &&
                double.TryParse(fpsParts[0], out double num) &&
                double.TryParse(fpsParts[1], out double den) &&
                den != 0)
            {
                double fps = num / den;
                totalFrames = (int)(duration * fps);
            }
        }

        // HDR 检测
        string color = string.Join(" ",
            stream.TryGetProperty("color_space", out var cs) ? cs.ToString() : "",
            stream.TryGetProperty("color_transfer", out var ct) ? ct.ToString() : "",
            stream.TryGetProperty("color_primaries", out var cp) ? cp.ToString() : "");

        bool isHdr = color.Contains("2020") || color.Contains("2084") || color.Contains("pq") || color.Contains("hlg");

        return new VideoInfo(width, height, Math.Max(totalFrames, 1), isHdr);
    }

    private record VideoInfo(int Width, int Height, long TotalFrames, bool IsHdr);
}

public static class WolveKit
{
    private static readonly string Root = AppContext.BaseDirectory;
    static readonly string RootX = Directory.GetParent(Root).Parent.FullName;
    private static readonly string FinalZipPath = Path.Combine(RootX, "DynamicWallpaper.zip");

    public static void GetPack()
    {
        string packFolder = Path.Combine(Root, "archive");
        string outputFolder = Path.Combine(Root, "output", "archive", "pc", "mod");

        if (!Pack(new DirectoryInfo(packFolder), new DirectoryInfo(outputFolder), "DynamicWallPaper"))
            return;

        DeleteFolder(packFolder);

        ConsoleHelper.Info("Creating zip archive...", "开始打包zip文件...");
        if (File.Exists(FinalZipPath)) File.Delete(FinalZipPath);

        ZipFile.CreateFromDirectory(Path.Combine(Root, "output"), FinalZipPath);
        DeleteFolder(Path.Combine(Root, "output"));

        ConsoleHelper.Info($"文件已输出至: {FinalZipPath}", $"Zip archive created at: {FinalZipPath}");
        ConsoleHelper.Quit();
    }

    public static bool Pack(DirectoryInfo inputDir, DirectoryInfo outputDir, string filename)
    {
        if (!inputDir.Exists)
        {
            ConsoleHelper.Error("打包失败: 输入目录不存在", "Packing failed: input directory not found.");
            return false;
        }

        outputDir.Create();
        ConsoleHelper.Info($"开始打包archive文件 {inputDir.FullName}（可能耗时较长）...", $"Packing archive from {inputDir.FullName} (may take a while)...");

        string outFile = Path.Combine(outputDir.FullName, $"{filename ?? inputDir.Name}.archive");
        string tmpFile = Path.ChangeExtension(outFile, "tmp");

        var logger = new SerilogWrapper();
        bool success;

        using (var fs = File.Create(tmpFile))
        {
            var writer = new ArchiveWriter(new HashService(), logger);
            success = writer.WriteArchive(inputDir, fs);
        }

        if (!success)
        {
            logger.Error("Could not pack archive");
            FileHelper.SafeDelete(tmpFile);
            return false;
        }

        if (!FileHelper.SafeMove(tmpFile, outFile, logger))
        {
            FileHelper.SafeDelete(tmpFile);
            return false;
        }
        ConsoleHelper.Info($"打包完成: {outFile}", $"Packing completed: {outFile}");
        return true;
    }

    public static void DeleteFolder(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            ConsoleHelper.Warn($"清理临时文件: {path}", $"Cleaning temporary files: {path}");
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            ConsoleHelper.Error($"无法删除文件夹 {path}：{ex.Message}", $"Failed to delete folder {path}:{ex.Message}");
        }
    }
}


















