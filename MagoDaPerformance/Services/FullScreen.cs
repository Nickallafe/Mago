using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MagoDaPerformance;

/// <summary>
/// A app que esta' em primeiro plano e, quando ocupa o monitor todo, o modo de ecra inteiro.
/// Nao ha API que diga "isto e' ecra inteiro exclusivo": isso quem sabe e' a janela do
/// overlay, ao verificar se ficou atras do jogo (ver <see cref="FpsOverlay.IsBehind"/>).
/// </summary>
public readonly record struct GameScreen(bool Fullscreen, string Process, string Title, IntPtr Handle, IntPtr Monitor)
{
    public static readonly GameScreen None = new(false, "", "", IntPtr.Zero, IntPtr.Zero);

    /// <summary>Nome curto para mostrar ao utilizador (sem ".exe").</summary>
    public string ShortName => Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? Process[..^4]
        : Process;
}

public static class FullScreen
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);

    private const uint MonitorDefaultToNearest = 2;

    // Tolerancia de 2 px: alguns jogos pedem o ecra com um pixel a mais e nao deixam de ser ecra inteiro.
    private const int Tolerance = 2;

    /// <summary>Janela em primeiro plano. Devolve <see cref="GameScreen.None"/> quando a frente e' o Mago ou o ambiente de trabalho.</summary>
    public static GameScreen Detect()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle)) return GameScreen.None;

        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return GameScreen.None;

        var process = ProcessName(pid);
        if (process.Length == 0 || IsShell(process)) return GameScreen.None;

        if (!GetWindowRect(handle, out var rect)) return GameScreen.None;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return GameScreen.None;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return GameScreen.None;

        var screen = info.Monitor;
        var covers =
            Math.Abs(rect.Left - screen.Left) <= Tolerance &&
            Math.Abs(rect.Top - screen.Top) <= Tolerance &&
            Math.Abs(rect.Right - screen.Right) <= Tolerance &&
            Math.Abs(rect.Bottom - screen.Bottom) <= Tolerance;

        return new GameScreen(covers, process, Title(handle), handle, monitor);
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static string Title(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return "";

        var text = new StringBuilder(length + 1);
        GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>O ambiente de trabalho tambem ocupa o ecra todo e nao e' jogo nenhum.</summary>
    private static bool IsShell(string process) =>
        process.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
        process.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
        process.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
        process.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase);
}
