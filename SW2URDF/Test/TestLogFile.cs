using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SW2URDF.Utilities;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The log of processes that run at the same time, tested with real
    /// processes (no window) whose home directory is a temporary one.
    /// </summary>
    public class TestLogFile
    {
        private string home;
        private readonly List<LoggingProcess> processes = new List<LoggingProcess>();

        // scripts/test_logic.ps1 does not dispose the test class, so each test cleans up itself.
        private void Run(Action test)
        {
            home = Path.Combine(Path.GetTempPath(), "sw2urdf_log_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            try { test(); }
            finally
            {
                foreach (LoggingProcess process in processes) process.Dispose();
                try { Directory.Delete(home, true); } catch (IOException) { }
            }
        }

        private LoggingProcess Start(string message)
        {
            LoggingProcess process = new LoggingProcess(home, message);
            processes.Add(process);
            return process;
        }

        private string InLogDirectory(string name)
        {
            return Path.Combine(home, "sw2urdf_logs", name);
        }

        private static string ReadShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static void AssertSamePath(string expected, string actual)
        {
            Assert.Equal(expected.ToLowerInvariant(), actual.ToLowerInvariant());
        }

        private static void AssertOtherPath(string other, string actual)
        {
            Assert.NotEqual(other.ToLowerInvariant(), actual.ToLowerInvariant());
        }

        [Fact]
        public void FirstProcessUsesTheDocumentedFile()
        {
            Run(() =>
            {
                LoggingProcess first = Start("message of the first");
                AssertSamePath(InLogDirectory("sw2urdf.log"), first.LogFile);
                first.Finish();
                Assert.Equal("", first.Errors);
                Assert.Contains("message of the first", File.ReadAllText(InLogDirectory("sw2urdf.log")));
            });
        }

        [Fact]
        public void SecondProcessHasItsOwnFileAndNoAppenderErrors()
        {
            Run(() =>
            {
                LoggingProcess first = Start("message of the first");
                LoggingProcess second = Start("message of the second");
                second.Finish();
                first.Finish();

                Assert.Equal("", second.Errors);
                Assert.Equal("", first.Errors);
                AssertOtherPath(first.LogFile, second.LogFile);
                AssertSamePath(Path.GetDirectoryName(first.LogFile), Path.GetDirectoryName(second.LogFile));
            });
        }

        [Fact]
        public void ReportedFileHoldsTheMessagesOfItsProcess()
        {
            Run(() =>
            {
                LoggingProcess first = Start("message of the first");
                LoggingProcess second = Start("message of the second");
                string firstText = ReadShared(first.LogFile);
                string secondText = ReadShared(second.LogFile);

                Assert.Contains("message of the first", firstText);
                Assert.DoesNotContain("message of the second", firstText);
                Assert.Contains("message of the second", secondText);
                Assert.DoesNotContain("message of the first", secondText);
            });
        }

        [Fact]
        public void FileOfAnEndedProcessIsUsedAgain()
        {
            Run(() =>
            {
                Start("one").Finish();
                LoggingProcess next = Start("two");
                next.Finish();
                Assert.Equal("", next.Errors);
                AssertSamePath(InLogDirectory("sw2urdf.log"), next.LogFile);
                Assert.Contains("two", File.ReadAllText(InLogDirectory("sw2urdf.log")));
                Assert.Contains("one", File.ReadAllText(InLogDirectory("sw2urdf.log.1")));
            });
        }

        // A process can also be killed. Its file is free again then.
        [Fact]
        public void FileOfAKilledProcessIsUsedAgain()
        {
            Run(() =>
            {
                Start("one").Kill();
                LoggingProcess next = Start("two");
                next.Finish();
                Assert.Equal("", next.Errors);
                AssertSamePath(InLogDirectory("sw2urdf.log"), next.LogFile);
            });
        }

        // A build that does not know the rule, or a program that shows the file, can have it open as well.
        [Fact]
        public void FileThatAnotherProgramHasOpenIsLeftAlone()
        {
            Run(() =>
            {
                Directory.CreateDirectory(InLogDirectory(""));
                string documented = InLogDirectory("sw2urdf.log");
                File.WriteAllText(documented, "kept");
                LoggingProcess process;
                using (new FileStream(documented, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    process = Start("message");
                    process.Finish();
                }
                Assert.Equal("", process.Errors);
                Assert.Equal("kept", File.ReadAllText(documented));
                AssertOtherPath(documented, process.LogFile);
                Assert.Contains("message", File.ReadAllText(process.LogFile));
            });
        }

        [Fact]
        public void BackupsOfTheFilesAreKeptApart()
        {
            Run(() =>
            {
                for (int run = 1; run <= 3; run++)
                {
                    LoggingProcess first = Start("first of run " + run);
                    LoggingProcess second = Start("second of run " + run);
                    second.Finish();
                    first.Finish();
                    Assert.Equal("", first.Errors);
                    Assert.Equal("", second.Errors);
                    if (run < 3) continue;
                    for (int back = 0; back < 3; back++)
                    {
                        string suffix = back == 0 ? "" : "." + back;
                        Assert.Contains("first of run " + (3 - back), File.ReadAllText(first.LogFile + suffix));
                        Assert.Contains("second of run " + (3 - back), File.ReadAllText(second.LogFile + suffix));
                    }
                    Assert.False(File.Exists(first.LogFile + ".3"));
                    Assert.False(File.Exists(second.LogFile + ".3"));
                }
            });
        }

        /// <summary>A process without a window that sets up the log, writes a message and waits.</summary>
        private sealed class LoggingProcess : IDisposable
        {
            private readonly Process process;
            private readonly StringBuilder errors = new StringBuilder();

            public string LogFile { get; }

            public string Errors
            {
                get { lock (errors) { return errors.ToString().Trim(); } }
            }

            public LoggingProcess(string home, string message)
            {
                string assembly = typeof(Logger).Assembly.Location;
                string script = string.Join("; ",
                    "$ErrorActionPreference = 'Stop'",
                    "[void][Reflection.Assembly]::LoadFrom('" + assembly.Replace("'", "''") + "')",
                    "[SW2URDF.Utilities.Logger]::GetLogger().Info('" + message + "')",
                    "[Console]::Out.WriteLine('LOG ' + [SW2URDF.Utilities.Logger]::GetFileName())",
                    "[void][Console]::In.ReadLine()");
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -OutputFormat Text -Command \"" + script + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                string root = Path.GetPathRoot(home).TrimEnd('\\');
                info.EnvironmentVariables["HOMEDRIVE"] = root;
                info.EnvironmentVariables["HOMEPATH"] = home.Substring(root.Length);
                process = Process.Start(info);
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null) lock (errors) { errors.AppendLine(e.Data); }
                };
                process.BeginErrorReadLine();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null && !line.StartsWith("LOG ")) { }
                if (line == null)
                {
                    process.WaitForExit();
                    throw new InvalidOperationException("The logging process ended: " + Errors);
                }
                LogFile = line.Substring(4);
            }

            public void Finish()
            {
                process.StandardInput.WriteLine();
                if (!process.WaitForExit(30000)) throw new InvalidOperationException("The logging process did not end");
                process.WaitForExit();
            }

            public void Kill()
            {
                process.Kill();
                process.WaitForExit();
            }

            public void Dispose()
            {
                if (!process.HasExited) Kill();
                process.Dispose();
            }
        }
    }
}
