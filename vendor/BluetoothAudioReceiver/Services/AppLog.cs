using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BluetoothAudioReceiver.Services;

/// <summary>
/// Lightweight, thread-safe rolling logger for runtime diagnostics.
/// The original app only logged unhandled crashes, which made intermittent
/// "connected but silent" problems impossible to diagnose after the fact.
/// Log writes are best-effort and never throw.
/// </summary>
public static class AppLog
{
    private const int MaxRecentEntries = 500;
    private const long MaxLogFileBytes = 2L * 1024 * 1024;

    private static readonly object _lock = new();
    private static readonly Queue<string> _recent = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BluetoothAudioReceiver",
        "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception == null
            ? message
            : $"{message} :: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// Returns the most recent log lines (oldest first) for diagnostic reports.
    /// </summary>
    public static IReadOnlyList<string> GetRecent(int maxLines = 200)
    {
        lock (_lock)
        {
            var result = new List<string>(_recent);
            if (result.Count > maxLines)
            {
                result.RemoveRange(0, result.Count - maxLines);
            }
            return result;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        lock (_lock)
        {
            _recent.Enqueue(line);
            while (_recent.Count > MaxRecentEntries)
            {
                _recent.Dequeue();
            }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break audio handling.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length <= MaxLogFileBytes)
        {
            return;
        }

        var backup = Path.Combine(LogDirectory, "app.1.log");
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }
        File.Move(LogPath, backup);
    }
}
