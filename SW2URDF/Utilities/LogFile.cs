using System;
using System.IO;

namespace SW2URDF.Utilities
{
    /// <summary>
    /// The log file of this process. RollingFileAppender needs a file for itself, but several processes can
    /// log at the same time (SolidWorks instances, test runners, command line hosts). The first one takes
    /// sw2urdf.log, the next one sw2urdf-2.log and so on; a number is free again when its process ends, so
    /// the files do not pile up. A lock file next to the log says that it is taken.
    /// </summary>
    public sealed class LogFile : IDisposable
    {
        private const string BaseName = "sw2urdf";
        private const int MaxFiles = 100;

        private FileStream lockStream;

        public string FileName { get; }

        private LogFile(string fileName, FileStream lockStream)
        {
            FileName = fileName;
            this.lockStream = lockStream;
        }

        /// <summary>Takes the first free log file in the directory until the process ends.</summary>
        public static LogFile Take(string directory)
        {
            string first = Path.Combine(directory, BaseName + ".log");
            try
            {
                Directory.CreateDirectory(directory);
                for (int number = 1; number <= MaxFiles; number++)
                {
                    string name = number == 1 ? BaseName : BaseName + "-" + number;
                    LogFile taken = TryTake(Path.Combine(directory, name + ".log"));
                    if (taken != null) return taken;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // The directory cannot be written: the appender reports it for the documented name.
            return new LogFile(first, null);
        }

        private static LogFile TryTake(string fileName)
        {
            FileStream taken = null;
            try
            {
                // The system removes the lock with the process, also when it crashes.
                taken = new FileStream(Path.ChangeExtension(fileName, ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

                // A build without the lock, or a program that shows the log, can have it open. It can
                // neither be renamed nor written then.
                if (File.Exists(fileName))
                {
                    using (new FileStream(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                return new LogFile(fileName, taken);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (taken != null) taken.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (lockStream == null) return;
            lockStream.Dispose();
            lockStream = null;
        }
    }
}
