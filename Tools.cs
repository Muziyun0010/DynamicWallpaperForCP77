using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
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

        public static void RunProcess(string exePath, string arguments, string nameIndex)
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
                string label = $"wallpaper_{nameIndex}.bk2";
                Task.Run(() =>
                {
                    List<AutomationElement> windows = new();
                    while (windows.Count < 1)
                    {
                        windows = ProcessWindowFinder.GetProcessWindows(pi.dwProcessId);
                        Thread.Sleep(100);
                    }
                    string title;
                    IntPtr hwnd = windows[0].FrameworkAutomationElement.NativeWindowHandle;

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


                    while (true)
                    {
                        title = ProcessWindowFinder.GetVisibleWindowTitle(windows[0]);
                        if (title.Contains("Done")) break;

                        int percentIndex = title.IndexOf('%');
                        if (percentIndex > 0)
                        {
                            var percent = long.Parse(title.Split('%')[0]);
                            ConsoleHelper.PrintProgressBar(label, percent, 100);
                        }
                        Thread.Sleep(200);
                    }

                    ConsoleHelper.PrintProgressBar(label, 100, 100);
                    ProcessWindowFinder.ClickButtonByName(windows[0], "Done");
                });

                WaitForSingleObject(pi.hProcess, uint.MaxValue);
            }
            finally
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
        }
    }


    public class ProcessWindowFinder
    {
        public static List<AutomationElement> GetProcessWindows(int processId)
        {
            var windows = new List<AutomationElement>();
            using var automation = new UIA3Automation();

            foreach (var proc in Process.GetProcesses())
            {
                if (proc.Id != processId)
                    continue;

                var mainWindowHandle = proc.MainWindowHandle;
                if (mainWindowHandle != IntPtr.Zero)
                {
                    try
                    {
                        var elem = automation.FromHandle(mainWindowHandle);
                        if (elem != null)
                        {
                            windows.Add(elem);
                        }
                    }
                    catch { }
                }
            }

            return windows;
        }

        /// <summary>
        /// 获取窗口标题
        /// </summary>
        public static string GetVisibleWindowTitle(AutomationElement window)
        {
            try
            {
                return window.Name;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
        public static bool ClickButtonByName(AutomationElement window, string buttonName)
        {
            if (window == null || string.IsNullOrEmpty(buttonName)) return false;

            try
            {
                var button = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName(buttonName)));

                if (button != null)
                {
                    if (button.Patterns.Invoke.IsSupported)
                    {
                        button.Patterns.Invoke.Pattern.Invoke();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"点击按钮失败: {ex.Message}");
            }

            return false;
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
                    Console.CursorVisible = false;
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
                else if (_lineMap.Count < 20)
                {
                    if (_nextLine == -1)
                    {
                        try { _nextLine = Console.CursorTop; } catch { _nextLine = 0; }
                    }

                    line = Interlocked.Increment(ref _nextLine) - 1;
                    _lineMap[label] = line;

                    PrintBar(line, label, text);
                }
                else if (!_sublineMap.TryGetValue(label, out line))
                {
                    if (_subConsoleProcess == null)
                    {
                        StartSubConsole();
                    }
                    line = Interlocked.Increment(ref _subnextLine) - 1;
                    _sublineMap.Add(label, line);
                    RefreshSubConsole($"{line},{label},{text}");
                }
                else
                {
                    RefreshSubConsole($"{line},{label},{text}");
                }
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
                    Console.CursorVisible = true;
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
            Console.ReadKey(true);
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
        public static void Generate(string outputDir, int count)
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var jsonObject = new { count };

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

    class AudioBankGenerator
    {
        public static void ExtractAndConvertToBnk()
        {
            var args = $"generate-soundbank -project \"{projectPath}\" -platform Windows -language SFX";
            RunWwiseConsole(args);

            var generatedBnk = Path.Combine(wwiseSoundBankOutputDir, "Windows", "SFX", "MyBank.bnk");
            File.Move(generatedBnk, bnkOutputPath, overwrite: true);
            Directory.Delete(wwiseSoundBankOutDir, true );
        }
    }
}
