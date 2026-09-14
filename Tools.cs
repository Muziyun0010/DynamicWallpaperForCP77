using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using WolvenKit.RED4.Types;

namespace BK2maker
{
    public static class HiddenProcessRunner
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_MINIMIZE = 6;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;

        public static void RunProcess(string exePath, string arguments, string nameIndex, string expectedOutputPath, string progressLabel)
        {
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_MINIMIZE;

            PROCESS_INFORMATION pi;

            string commandLine = $"\"{exePath}\" {arguments}";

            if (!CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                null,
                ref si,
                out pi))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateProcess failed with error {err}");
            }

            try
            {
                string label = progressLabel;
                var monitorTask = Task.Run(() =>
                {
                    IntPtr hwnd = IntPtr.Zero;
                    var windowDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (hwnd == IntPtr.Zero && DateTime.UtcNow < windowDeadline)
                    {
                        hwnd = ProcessWindowFinder.GetProcessWindow(pi.dwProcessId);
                        if (ProcessWindowFinder.HasProcessExited(pi.dwProcessId))
                            return;
                        Thread.Sleep(100);
                    }

                    // Some RAD versions do not expose a UI Automation window at all.
                    // Conversion can still complete successfully, so the window is optional.
                    if (hwnd == IntPtr.Zero)
                        return;

                    string title;

                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if (exStyle == 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        ConsoleHelper.Error($"GetWindowLong 失败，错误码: {err}", "");
                    }
                    else
                    {
                        exStyle &= ~WS_EX_APPWINDOW;
                        exStyle |= WS_EX_TOOLWINDOW;

                        int result = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
                        if (result == 0)
                        {
                            int err = Marshal.GetLastWin32Error();
                            ConsoleHelper.Error($"GetWindowLong 失败，错误码: {err}", "");
                        }

                        bool posResult = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

                        if (!posResult)
                        {
                            int err = Marshal.GetLastWin32Error();
                            ConsoleHelper.Error($"GetWindowLong 失败，错误码: {err}", "");
                        }
                    }


                    while (!ProcessWindowFinder.HasProcessExited(pi.dwProcessId))
                    {
                        title = ProcessWindowFinder.GetWindowTitle(hwnd);
                        if (TryReadPercent(title, out var percent))
                        {
                            ConsoleHelper.PrintProgressBar(label, percent, 100);
                        }

                        if (IsCompletionTitle(title))
                        {
                            ConsoleHelper.PrintProgressBar(label, 100, 100);
                            if (!ProcessWindowFinder.ClickCompletionButton(hwnd))
                            {
                                // If the exact button text differs between RAD versions/locales,
                                // closing a completed window is safer than waiting forever.
                                ProcessWindowFinder.CloseWindow(hwnd);
                            }
                            return;
                        }

                        Thread.Sleep(200);
                    }
                });

                WaitForSingleObject(pi.hProcess, uint.MaxValue);
                monitorTask.Wait(TimeSpan.FromSeconds(2));

                if (!File.Exists(expectedOutputPath) || new FileInfo(expectedOutputPath).Length == 0)
                    throw new InvalidOperationException($"RAD Video Tools 未生成输出文件: {expectedOutputPath}");

                ConsoleHelper.PrintProgressBar(label, 100, 100);
            }
            finally
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
        }

        private static bool TryReadPercent(string title, out long percent)
        {
            percent = 0;
            int percentIndex = title.IndexOf('%');
            if (percentIndex <= 0)
                return false;

            int start = percentIndex - 1;
            while (start >= 0 && char.IsDigit(title[start]))
                start--;
            start++;

            return start < percentIndex &&
                   long.TryParse(title[start..percentIndex], out percent) &&
                   percent is >= 0 and <= 100;
        }

        private static bool IsCompletionTitle(string title)
        {
            return title.Contains("Done", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("Finished", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("Complete", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("完成", StringComparison.OrdinalIgnoreCase);
        }
    }


    public static class ProcessWindowFinder
    {
        private const uint WM_CLOSE = 0x0010;
        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static bool HasProcessExited(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public static void CloseWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public static IntPtr GetProcessWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var ownerPid);
                if (ownerPid == (uint)processId)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static string GetWindowTitle(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return string.Empty;

            int length = GetWindowTextLength(window);
            var buffer = new StringBuilder(Math.Max(length + 1, 256));
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        public static bool ClickCompletionButton(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return false;

            var acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Done", "Close", "OK", "完成", "关闭", "确定" };
            IntPtr foundButton = IntPtr.Zero;

            EnumChildWindows(window, (hWnd, _) =>
            {
                var className = new StringBuilder(64);
                GetClassName(hWnd, className, className.Capacity);
                if (!className.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase))
                    return true;

                var text = new StringBuilder(128);
                GetWindowText(hWnd, text, text.Capacity);
                if (acceptedNames.Contains(text.ToString().Trim()))
                {
                    foundButton = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (foundButton == IntPtr.Zero)
                return false;

            SendMessage(foundButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
    }

    public static class ConsoleHelper
    {
        //------------------------进度条------------------------------------------------
        private static int _nextLine = -1;
        private static int _subnextLine = 0;
        private static readonly object _lock = new();
        private const int BarWidth = 30;
        private const char FilledChar = '█';
        private const char EmptyChar = ' ';

        private static readonly ConsoleColor[] ProgressColors =
            { ConsoleColor.DarkCyan, ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Yellow };

        private static Dictionary<string, int> _lineMap = new();
        private static Dictionary<string, int> _sublineMap = new();

        private static List<string> _completed = new();

        private static bool _cursorHidden;

        private static Process? _subConsoleProcess = null;
        public static string PadLabel(string label, int totalWidth)
        {
            int width = 0;
            foreach (char c in label)
                width += (c >= 0x2E80 && c <= 0x9FFF) ||
                   (c >= 0xFF00 && c <= 0xFFEF) ? 2 : 1;

            int padding = Math.Max(0, totalWidth - width);
            return label + new string(' ', padding);
        }

        public static void PrintProgressBar(string label, long current, long total)
        {
            lock (_lock)
            {
                if (!_cursorHidden)
                {
                    try { Console.CursorVisible = false; } catch { }
                    _cursorHidden = true;
                }

                if (_completed.Contains(label))
                {
                    return;
                }
                else if (current >= total)
                {
                    current = total;
                    _completed.Add(label);
                }

                double ratio = total > 0 ? (double)current / total : 0;
                int filled = (int)(ratio * BarWidth);
                int percent = (int)(ratio * 100);
                string bar = new string(FilledChar, filled) + new string(EmptyChar, BarWidth - filled);
                string text = $"{PadLabel(label, 17)} [{bar}] {percent,3}% ({current,3}/{total,-3})";

                if (_lineMap.TryGetValue(label, out var line))
                {
                    PrintBar(line, label, text);
                }
                else if (_lineMap.Count < 30)
                {
                    if (_nextLine == -1)
                    {
                        try { _nextLine = Console.CursorTop; } catch { _nextLine = 0; }
                    }

                    line = Interlocked.Increment(ref _nextLine) - 1;
                    _lineMap[label] = line;

                    PrintBar(line, label, text);
                }
                // The tool accepts at most 30 videos, so all progress rows now stay in
                // the main console. This removes the fragile second-console IPC path.
            }
        }

        public static void ResetProgress(string label)
        {
            lock (_lock)
            {
                _completed.Remove(label);
            }
        }

        public static void PrintBar(int line, string label, string text)
        {
            int windowWidth = 80;
            try { windowWidth = Math.Max(Console.WindowWidth, 10); } catch { }

            try
            {
                int safeLine = Math.Max(0, Math.Min(line, Console.BufferHeight - 1));
                Console.SetCursorPosition(0, safeLine);
            }
            catch { }

            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = ProgressColors[Math.Abs(label.GetHashCode()) % ProgressColors.Length];
            Console.Write(text);

            if (text.Length < windowWidth - 1)
            {
                try { Console.Write(new string(' ', windowWidth - 1 - text.Length)); }
                catch { }
            }
            Console.ForegroundColor = oldColor;
        }
        public static void PrintBar(string message)
        {
            var a = message.Split(',');
            PrintBar(int.Parse(a[0]), a[1], a[2]);
        }

        public static void CompleteProgressBars()
        {
            lock (_lock)
            {
                if (_cursorHidden)
                {
                    try { Console.CursorVisible = true; } catch { }
                    _cursorHidden = false;
                }

                if (_lineMap.Count > 0)
                {
                    int maxLine = 0;
                    try { maxLine = _lineMap.Values.Max(); }
                    catch { maxLine = 0; }

                    int targetLine = maxLine + 1;

                    int bufferHeight = 25;
                    try { bufferHeight = Console.BufferHeight; } catch { }

                    int bufferWidth = 80;
                    try { bufferWidth = Console.BufferWidth; } catch { }

                    if (targetLine >= bufferHeight)
                    {
                        int scrollLines = targetLine - bufferHeight + 1;
                        for (int i = 0; i < scrollLines; i++)
                        {
                            try { Console.MoveBufferArea(0, 1, bufferWidth, bufferHeight - 1, 0, 0); }
                            catch { break; }
                        }
                        try { Console.SetCursorPosition(0, bufferHeight - 1); }
                        catch { }
                    }
                    else
                    {
                        try { Console.SetCursorPosition(0, targetLine); }
                        catch { }
                    }

                    try { Console.WriteLine(); }
                    catch { }
                }

                _lineMap.Clear();
                _completed.Clear();
                _nextLine = -1;
                SubConsoleMessenger.Dispose();
                StopSubConsole();
            }
        }

        #region 子窗口相关空方法示范
        private static void StartSubConsole()
        {
            if (_subConsoleProcess != null && !_subConsoleProcess.HasExited)
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule!.FileName,
                Arguments = "--subconsole",
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            _subConsoleProcess = Process.Start(startInfo);

            Task.Run(() =>
            {
                const int maxRetries = 50;
                int retryCount = 0;
                bool _subConsoleClientInitialized = false;

                while (retryCount < maxRetries)
                {
                    if (_subConsoleClientInitialized)
                        break;
                    try
                    {
                        SubConsoleMessenger.InitAsClient();
                        _subConsoleClientInitialized = true;
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelper.Error(ex.Message, "");
                    }

                    retryCount++;
                    Thread.Sleep(100);
                }
                if (!_subConsoleClientInitialized)
                {
                }
            });
        }

        private static void RefreshSubConsole(string text)
        {
            if (_subConsoleProcess != null && !_subConsoleProcess.HasExited)
            {
                try
                {
                    SubConsoleMessenger.Trigger(text);
                }
                catch
                {
                }
            }
        }

        private static void StopSubConsole()
        {
            if (_subConsoleProcess != null && !_subConsoleProcess.HasExited)
            {
                _subConsoleProcess.Kill();
                _subConsoleProcess.Dispose();
                _subConsoleProcess = null;
            }
        }

        #endregion
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void DisableFocusAndInput()
        {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd == IntPtr.Zero)
                return;

            // 1. 禁用输入
            EnableWindow(hWnd, false);

            // 2. 设置无激活样式
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            exStyle &= ~WS_EX_APPWINDOW; // 不出现在任务栏
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);

            // 3. 最小化再恢复避免闪烁
            ShowWindow(hWnd, 2); // SW_MINIMIZE
            ShowWindow(hWnd, 9); // SW_RESTORE
        }
        //--------------------------------------------------
        public static void Info(string en, string zh)
        {
            WriteTag("INFO", ConsoleColor.Cyan);
            Console.WriteLine(en);
            WriteTag("INFO", ConsoleColor.Cyan);
            Console.WriteLine(zh);
            Console.WriteLine();
        }

        public static void Error(string en, string zh)
        {
            WriteTag("ERROR", ConsoleColor.Red);
            Console.WriteLine(en);
            WriteTag("ERROR", ConsoleColor.Red);
            Console.WriteLine(zh);
            Console.WriteLine();
        }

        public static void Warn(string en, string zh)
        {
            WriteTag("WARN", ConsoleColor.Yellow);
            Console.WriteLine(en);
            WriteTag("WARN", ConsoleColor.Yellow);
            Console.WriteLine(zh);
            Console.WriteLine();
        }

        private static void WriteTag(string tag, ConsoleColor color)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"[{tag}] ");
            Console.ForegroundColor = old;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
       int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_SHOWWINDOW = 0x0040;
        public static void Quit()
        {
            Info("按任意键退出...", "Press any key to exit...");
            var handle = GetConsoleWindow();

            // 强制置顶 + 显示 + 保持位置大小
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                SetForegroundWindow(handle); // 激活
            }

            if (!Console.IsInputRedirected)
            {
                try { Console.ReadKey(true); } catch { }
            }
        }
    }

    public static class ResourceExtractor
    {
        private const int BufferSize = 1 << 20; // 1MB

        public static void ExtractIfSizeDiffers(string resourceName, string outputPath)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                          ?? throw new InvalidOperationException($"Resource not found: {resourceName}");

            // 如果已存在且大小一致，直接跳过
            if (File.Exists(outputPath))
            {
                try
                {
                    if (new FileInfo(outputPath).Length == stream.Length)
                        return;
                }
                catch
                {
                    // ignore, fallback to overwrite
                }
            }

            // 确保目标目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                          BufferSize, FileOptions.SequentialScan);
            stream.CopyTo(fs, BufferSize);
        }
        public static void Generate(string outputDir, int count, IReadOnlyList<double>? aspects = null)
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            object jsonObject = aspects == null
                ? new { count }
                : new
                {
                    count,
                    aspects = aspects.Select(x => Math.Round(x, 6)).ToArray()
                };

            string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string outputPath = Path.Combine(outputDir, "count.json");
            File.WriteAllText(outputPath, json);
        }
    }

    public static class SubConsoleMessenger
    {
        private const string MemoryName = "SubConsoleSharedMemory";
        private const string EventName = "SubConsoleTriggerEvent";
        private const int MaxMessageLength = 1024;

        private static EventWaitHandle _event;
        private static MemoryMappedFile _mmf;
        private static MemoryMappedViewAccessor _accessor;
        private static CancellationTokenSource _cts;

        public static event Action<string> OnMessageReceived;

        public static void InitAsServer()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }
            _mmf = MemoryMappedFile.CreateOrOpen(MemoryName, MaxMessageLength);
            _accessor = _mmf.CreateViewAccessor();
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _cts = new CancellationTokenSource();

            while (!_cts.IsCancellationRequested)
            {
                _event.WaitOne();
                int length = _accessor.ReadInt32(0);
                if (length > 0 && length <= MaxMessageLength - 4)
                {
                    byte[] buffer = new byte[length];
                    _accessor.ReadArray(4, buffer, 0, length);
                    string message = Encoding.UTF8.GetString(buffer);
                    OnMessageReceived?.Invoke(message);
                }
            }
        }

        public static void InitAsClient()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }
            _mmf = MemoryMappedFile.OpenExisting(MemoryName);
            _accessor = _mmf.CreateViewAccessor();
            _event = EventWaitHandle.OpenExisting(EventName);
        }

        public static void Trigger(string message)
        {
            if (_accessor == null)
                return;
            if (string.IsNullOrEmpty(message)) return;

            byte[] data = Encoding.UTF8.GetBytes(message);
            if (data.Length > MaxMessageLength - 4)
                throw new ArgumentException("Message too long");

            _accessor.Write(0, data.Length);
            _accessor.WriteArray(4, data, 0, data.Length);
            _event.Set();
        }

        public static void Dispose()
        {
            _cts?.Cancel();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _event?.Dispose();
        }
    }
}
