using System.Collections.Concurrent;
using System.Text;

namespace EasyDesktopLyrics.Infrastructure;

internal enum LogLevel { None = 0, Normal = 1, Debug = 2 }

/// <summary>极简文件日志：内存缓冲 + 异步落盘。</summary>
internal static class Log
{
    private static readonly ConcurrentQueue<string> LogQueue = new();
    private static readonly object Gate = new();
    private static readonly System.Threading.Timer FlushTimer;
    private static bool _isDisposed;

    public static LogLevel CurrentLevel { get; set; } = LogLevel.Normal;

    static Log()
    {
        try
        {
            var fi = new FileInfo(AppPaths.LogFile);
            if (fi.Exists && fi.Length > 1024 * 1024)
                fi.Delete();
        }
        catch { }

        FlushTimer = new System.Threading.Timer(TimerCallback, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
    }

    public static void Info(string message)
    {
        if (CurrentLevel >= LogLevel.Normal) Enqueue("INF", message);
    }

    public static void Debug(string message)
    {
        if (CurrentLevel >= LogLevel.Debug) Enqueue("DBG", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        if (CurrentLevel > LogLevel.None) Enqueue("ERR", ex is null ? message : $"{message} \n{ex}");
    }

    private static void Enqueue(string level, string message)
    {
        if (_isDisposed) return;
        LogQueue.Enqueue($"{DateTime.Now:MM-dd HH:mm:ss.fff} [{level}] {message}");
    }

    private static void TimerCallback(object? state)
    {
        try
        {
            FlushToDisk();
        }
        finally
        {
            lock (Gate)
            {
                // 执行完毕后，重新动态规划下一次 1 秒后触发
                if (!_isDisposed)
                {
                    FlushTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>
    /// 将内存队列中的日志彻底刷新到磁盘
    /// </summary>
    public static void FlushToDisk()
    {
        if (LogQueue.IsEmpty) return;

        var sb = new StringBuilder();
        while (LogQueue.TryDequeue(out var line))
        {
            sb.AppendLine(line);
        }

        if (sb.Length == 0) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogFile, sb.ToString());
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    /// <summary>
    /// 程序退出时释放资源并最后一次强制落盘
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        try
        {
            FlushTimer.Dispose();
            FlushToDisk(); // 把队列里剩余的日志写进去
        }
        catch
        {
            // ignore
        }
    }
}
