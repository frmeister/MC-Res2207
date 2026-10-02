using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MC_Ref2207_NetSocketLib
{
    /// <summary>
    /// Пишет весь отладочный вывод проекта в файл.
    /// Вызовите DebugFileLogger.Start() в начале работы приложения и DebugFileLogger.Stop() при выходе.
    /// Для отладочных сообщений используйте Trace.WriteLine: вызовы Debug.WriteLine компилятор
    /// вырезает из Release-сборки (а публикуется именно она), поэтому в лог они попадут только в Debug.
    /// </summary>
    public static class DebugFileLogger
    {
        private static readonly object _sync = new object();
        private static TimestampedFileListener? _listener;

        /// <summary>
        /// Полный путь к текущему файлу лога или null, если запись не идёт.
        /// </summary>
        public static string? LogFilePath { get; private set; }

        /// <summary>
        /// Начинает запись в файл.
        /// Если path == null, создаётся новый файл logs/debug_ГГГГММДД_ЧЧММСС_PID.log рядом с программой:
        /// у каждого запуска свой файл, поэтому две копии программы из одной папки не мешают друг другу.
        /// </summary>
        /// <param name="path">Путь к файлу лога.</param>
        /// <param name="append">Дописывать в существующий файл или перезаписывать его.</param>
        /// <returns>true, если запись в файл идёт.</returns>
        public static bool Start(string? path = null, bool append = true)
        {
            lock (_sync)
            {
                if (_listener != null) return true; // Уже запущен

                try
                {
                    path ??= Path.Combine(AppContext.BaseDirectory, "logs", $"debug_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
                    path = Path.GetFullPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    // FileShare.ReadWrite: лог можно открыть и читать, пока программа работает
                    var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                    _listener = new TimestampedFileListener(writer) { Name = "DebugFileLogger" };
                    Trace.Listeners.Add(_listener);
                    LogFilePath = path;
                }
                catch (Exception ex)
                {
                    // Не удалось открыть файл — программа работает дальше без лога
                    _listener = null;
                    LogFilePath = null;
                    Trace.WriteLine($"[DebugFileLogger] Failed to start logging: {ex}");
                    return false;
                }
            }

            Trace.WriteLine($"[DebugFileLogger] Started logging to {path}, pid={Environment.ProcessId}");
            return true;
        }

        /// <summary>
        /// Останавливает запись и закрывает файл.
        /// </summary>
        public static void Stop()
        {
            TimestampedFileListener? listener;
            lock (_sync)
            {
                listener = _listener;
                if (listener == null) return;

                listener.WriteLine("[DebugFileLogger] Stopping logging");
                Trace.Listeners.Remove(listener);
                _listener = null;
                LogFilePath = null;
            }

            listener.Dispose();
        }

        /// <summary>
        /// true если запись в файл идёт.
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                lock (_sync) { return _listener != null; }
            }
        }

        // Добавляет к каждой строке время и номер потока — без них лог сетевого обмена не разобрать
        private sealed class TimestampedFileListener : TraceListener
        {
            private readonly StreamWriter _writer;
            private readonly object _writeLock = new object();
            private bool _atLineStart = true;
            private bool _disposed;

            public TimestampedFileListener(StreamWriter writer)
            {
                _writer = writer;
            }

            public override bool IsThreadSafe => true;

            public override void Write(string? message)
            {
                lock (_writeLock)
                {
                    if (_disposed) return;
                    WritePrefixedText(message);
                }
            }

            public override void WriteLine(string? message)
            {
                lock (_writeLock)
                {
                    if (_disposed) return;
                    WritePrefixedText(message);
                    _writer.WriteLine();
                    _atLineStart = true;
                }
            }

            public override void Flush()
            {
                lock (_writeLock)
                {
                    if (!_disposed) _writer.Flush();
                }
            }

            private void WritePrefixedText(string? message)
            {
                if (_atLineStart)
                {
                    _writer.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] ");
                    _atLineStart = false;
                }
                _writer.Write(message);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    lock (_writeLock)
                    {
                        if (!_disposed)
                        {
                            _disposed = true;
                            _writer.Dispose();
                        }
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}
