using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using SW2URDF.UI;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Does not need SolidWorks. The copy of the log in the package is a convenience: the export is complete
    /// without it, so a copy that is not possible is a warning in the log.
    /// </summary>
    public class TestExportLogCopy
    {
        private sealed class RecordingMessageBox : IMessageBox
        {
            public readonly List<string> Messages = new List<string>();

            public MessageBoxResult Show(string message)
            {
                Messages.Add(message);
                return MessageBoxResult.OK;
            }

            public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons)
            {
                Messages.Add(message);
                return MessageBoxResult.OK;
            }
        }

        private static void Run(Action<string, RecordingMessageBox, MemoryAppender> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "sw2urdf_copy_" + Guid.NewGuid().ToString("N")) + @"\";
            Directory.CreateDirectory(directory);
            Utilities.Logger.Setup();
            Hierarchy hierarchy = (Hierarchy)LogManager.GetRepository();
            MemoryAppender log = new MemoryAppender();
            log.ActivateOptions();
            hierarchy.Root.AddAppender(log);
            IMessageBox previous = URDFPackage.MessageBox;
            RecordingMessageBox box = new RecordingMessageBox();
            URDFPackage.MessageBox = box;
            try { test(directory, box, log); }
            finally
            {
                URDFPackage.MessageBox = previous;
                hierarchy.Root.RemoveAppender(log);
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        private static void AssertWarning(MemoryAppender log, string text)
        {
            Assert.Contains(log.GetEvents(), e => e.Level == Level.Warn && e.RenderedMessage.Contains(text));
        }

        [Fact]
        public void MissingLogIsAWarningWithoutAMessageBox()
        {
            Run((directory, box, log) =>
            {
                string missing = directory + "missing.log";
                ExportHelper.CopyLogFile(missing, directory);
                Assert.Empty(box.Messages);
                AssertWarning(log, missing);
                Assert.False(File.Exists(directory + "export.log"));
            });
        }

        [Fact]
        public void CopyThatCannotBeReplacedIsAWarning()
        {
            Run((directory, box, log) =>
            {
                string source = directory + "source.log", copy = directory + "export.log";
                File.WriteAllText(source, "new");
                File.WriteAllText(copy, "old");
                using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ExportHelper.CopyLogFile(source, directory);
                }
                Assert.Empty(box.Messages);
                AssertWarning(log, copy);
            });
        }

        [Fact]
        public void ReadOnlyCopyIsAWarning()
        {
            Run((directory, box, log) =>
            {
                string source = directory + "source.log", copy = directory + "export.log";
                File.WriteAllText(source, "new");
                File.WriteAllText(copy, "old");
                File.SetAttributes(copy, FileAttributes.ReadOnly);
                try { ExportHelper.CopyLogFile(source, directory); }
                finally { File.SetAttributes(copy, FileAttributes.Normal); }
                Assert.Empty(box.Messages);
                AssertWarning(log, copy);
            });
        }

        // The log is open in this process while it is copied.
        [Fact]
        public void OpenLogIsCopied()
        {
            Run((directory, box, log) =>
            {
                string source = directory + "source.log";
                using (FileStream stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.WriteByte((byte)'x');
                    stream.Flush();
                    ExportHelper.CopyLogFile(source, directory);
                }
                Assert.Empty(box.Messages);
                Assert.Equal("x", File.ReadAllText(directory + "export.log"));
                Assert.DoesNotContain(log.GetEvents(), e => e.Level >= Level.Warn);
            });
        }

        [Fact]
        public void NoLogIsNotAnError()
        {
            Run((directory, box, log) =>
            {
                ExportHelper.CopyLogFile(null, directory);
                Assert.Empty(box.Messages);
                Assert.Empty(Directory.GetFiles(directory));
            });
        }
    }
}
