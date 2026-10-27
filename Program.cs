using Avalonia;
using Avalonia.Controls;
using Avalonia.Win32;
using EasyDesktopLyrics.Infrastructure;

namespace EasyDesktopLyrics;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 隐藏的诊断入口：--probe <标题> [歌手] / --probe-smtc
        if (args.Length > 0 && args[0] is "--probe" or "--probe-smtc")
            return DebugProbes.Run(args);

        using var mutex = new Mutex(initiallyOwned: true, @"Local\EasyDesktopLyrics.SingleInstance", out var createdNew);
        if (!createdNew)
            return 0; // 已有实例，静默退出

        try
        {
            Log.Info("---- app start ----");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            // 注入官方字体保护锁，强制收束全局默认字体，防止被桌面歌词特殊字体劫持进程
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "Microsoft YaHei, Segoe UI, Arial"
            });

}
