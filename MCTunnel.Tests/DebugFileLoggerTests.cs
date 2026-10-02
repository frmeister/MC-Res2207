// MCTunnel.Tests/DebugFileLoggerTests.cs

using System;
using System.Diagnostics;
using System.IO;
using MC_Ref2207_NetSocketLib;
using Xunit;

namespace MCTunnel.Tests
{
    // DebugFileLogger глобальный, поэтому все его тесты в одном классе — xUnit выполняет их последовательно
    public class DebugFileLoggerTests
    {
        private static string TempLogPath() => Path.Combine(Path.GetTempPath(), $"mctunnel_test_{Guid.NewGuid():N}.log");

        [Fact]
        public void TraceOutput_IsWrittenToFileWithTimestamp()
        {
            var path = TempLogPath();
            try
            {
                Assert.True(DebugFileLogger.Start(path));
                Assert.Equal(path, DebugFileLogger.LogFilePath);

                Trace.WriteLine("hello from test");
                DebugFileLogger.Stop();

                Assert.False(DebugFileLogger.IsRunning);
                var line = Assert.Single(File.ReadAllLines(path), l => l.Contains("hello from test"));
                Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[\s*\d+\] hello from test$", line);
            }
            finally
            {
                DebugFileLogger.Stop();
                File.Delete(path);
            }
        }

        [Fact]
        public void LogFile_CanBeReadWhileLoggingIsRunning()
        {
            var path = TempLogPath();
            try
            {
                Assert.True(DebugFileLogger.Start(path));
                Trace.WriteLine("visible while running");

                using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    Assert.Contains("visible while running", reader.ReadToEnd());
                }
            }
            finally
            {
                DebugFileLogger.Stop();
                File.Delete(path);
            }
        }

        // Две копии программы из одной папки не должны бороться за один файл
        [Fact]
        public void Start_WithoutPath_CreatesFileNamedAfterProcess()
        {
            string? path = null;
            try
            {
                Assert.True(DebugFileLogger.Start());
                path = DebugFileLogger.LogFilePath;

                Assert.NotNull(path);
                Assert.Contains($"_{Environment.ProcessId}.log", Path.GetFileName(path));
            }
            finally
            {
                DebugFileLogger.Stop();
                if (path != null) File.Delete(path);
            }
        }
    }
}
