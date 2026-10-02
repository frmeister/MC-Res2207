using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MC_Ref2207_NetSocketLib
{
    /// <summary>
    /// Простая утилита для перенаправления Debug выходов в файл.
    /// Вызовите DebugFileLogger.Start(path) в начале работы приложения,
    /// и Debug.WriteLine(...) будет писаться в указанный файл.
    /// Вызовите DebugFileLogger.Stop() для остановки и закрытия файла.
    /// </summary>
    public static class DebugFileLogger
    {
        private static readonly object _sync = new object();
        private static TextWriterTraceListener? _listener;
        private static StreamWriter? _writer;

        /// <summary>
        /// Запустить логирование Debug в файл.
        /// Если path == null, будет использован файл "debug.log" в каталоге приложения.
        /// </summary>
        /// <param name="path">Путь к файлу логов.</param>
        /// <param name="append">Добавлять в существующий файл или перезаписать.</param>
        public static void Start(string? path = null, bool append = true)
        {
            lock (_sync)
            {
                if (_listener != null)
                {
                    // Уже запущено
                    return;
                }

                try
                {
                    path ??= Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), "debug.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory ?? Directory.GetCurrentDirectory());

                    _writer = new StreamWriter(path, append, Encoding.UTF8) { AutoFlush = true };
                    _listener = new TextWriterTraceListener(_writer) { Name = "DebugFileLogger" };

                    // Use Trace listeners to avoid environments where Debug.Listeners isn't available
                    Trace.Listeners.Add(_listener);
                    Debug.WriteLine($"[DebugFileLogger] Started logging to {path} at {DateTime.UtcNow:O}");
                }
                catch (Exception ex)
                {
                    // Не бросаем исключение - логгирование не критично
                    Trace.Listeners.Remove(_listener);
                    _listener?.Dispose();
                    _listener = null;
                    _writer?.Dispose();
                    _writer = null;
                    Trace.TraceError($"[DebugFileLogger] Failed to start logging: {ex}");
                }
            }
        }

        /// <summary>
        /// Остановить логирование и закрыть файл.
        /// </summary>
        public static void Stop()
        {
            lock (_sync)
            {
                if (_listener == null) return;

                try
                {
                    Debug.WriteLine($"[DebugFileLogger] Stopping logging at {DateTime.UtcNow:O}");
                }
                catch { }

                try
                {
                    Trace.Listeners.Remove(_listener);
                    _listener.Flush();
                    _listener.Dispose();
                }
                catch { }

                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch { }

                _listener = null;
                _writer = null;
            }
        }

        /// <summary>
        /// true если логгер запущен.
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                lock (_sync) { return _listener != null; }
            }
        }
    }
}
