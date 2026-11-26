using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SW2URDF.UI;
using Xunit;

namespace SW2URDF.Test
{
    /// <summary>
    /// Needs neither SolidWorks nor a window. The Save As dialog tells that it is about to open a folder; when
    /// that is the existing package whose name was confirmed, the dialog is closed with it (issue #224). The
    /// dialog of Windows is replaced by one that only answers questions.
    /// </summary>
    public class TestPackageDialog
    {
        private sealed class FakeItem : PackageDialog.IShellItem
        {
            private readonly string path;

            public FakeItem(string path)
            {
                this.path = path;
            }

            // Not a folder of the file system, as "This PC".
            public int GetDisplayName(uint form, out string name)
            {
                name = path;
                return path == null ? unchecked((int)0x80004005) : 0;
            }

            public void BindToHandler(IntPtr context, ref Guid handler, ref Guid type, out IntPtr result)
            {
                throw new NotSupportedException();
            }

            public void GetParent(out PackageDialog.IShellItem parent)
            {
                throw new NotSupportedException();
            }

            public void GetAttributes(uint mask, out uint attributes)
            {
                throw new NotSupportedException();
            }

            public void Compare(PackageDialog.IShellItem other, uint hint, out int order)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeDialog : PackageDialog.IFileDialog
        {
            public string Folder;
            public string Name;
            public readonly List<int> Closed = new List<int>();

            public int GetFolder(out PackageDialog.IShellItem folder)
            {
                folder = Folder == null ? null : new FakeItem(Folder);
                return Folder == null ? unchecked((int)0x80004005) : 0;
            }

            public int GetFileName(out string name)
            {
                name = Name;
                return Name == null ? unchecked((int)0x80004005) : 0;
            }

            public void Close(int result)
            {
                Closed.Add(result);
            }

            public int Show(IntPtr owner) => throw new NotSupportedException();
            public void SetFileTypes(uint count, IntPtr types) => throw new NotSupportedException();
            public void SetFileTypeIndex(uint index) => throw new NotSupportedException();
            public void GetFileTypeIndex(out uint index) => throw new NotSupportedException();
            public void Advise(PackageDialog.IFileDialogEvents events, out uint cookie) => throw new NotSupportedException();
            public void Unadvise(uint cookie) => throw new NotSupportedException();
            public void SetOptions(uint options) => throw new NotSupportedException();
            public void GetOptions(out uint options) => throw new NotSupportedException();
            public void SetDefaultFolder(PackageDialog.IShellItem folder) => throw new NotSupportedException();
            public void SetFolder(PackageDialog.IShellItem folder) => throw new NotSupportedException();
            public void GetCurrentSelection(out PackageDialog.IShellItem item) => throw new NotSupportedException();
            public void SetFileName(string name) => throw new NotSupportedException();
            public void SetTitle(string title) => throw new NotSupportedException();
            public void SetOkButtonLabel(string text) => throw new NotSupportedException();
            public void SetFileNameLabel(string text) => throw new NotSupportedException();
            public void GetResult(out PackageDialog.IShellItem item) => throw new NotSupportedException();
            public void AddPlace(PackageDialog.IShellItem item, int place) => throw new NotSupportedException();
            public void SetDefaultExtension(string extension) => throw new NotSupportedException();
        }

        // A folder "out" with the packages robot_pkg and old_pkg (no package.xml), a package with Chinese
        // characters and a space, and a folder that is no package.
        private sealed class Folders : IDisposable
        {
            public readonly string Out = Path.Combine(Path.GetTempPath(), "sw2urdf-dialog-" + Guid.NewGuid().ToString("N"), "out");
            public readonly string Chinese = new string(new[] { (char)0x6A5F, (char)0x5668, ' ', 'p', 'k', 'g' });

            public Folders()
            {
                foreach (string name in new[] { "robot_pkg", Chinese, "plain", @"robot_pkg\robot_pkg" })
                {
                    Directory.CreateDirectory(Path.Combine(Out, name));
                }
                Directory.CreateDirectory(Path.Combine(Out, @"old_pkg\urdf"));
                foreach (string name in new[] { "robot_pkg", Chinese })
                {
                    File.WriteAllText(Path.Combine(Out, name, "package.xml"), "<package/>");
                }
            }

            public string In(string name)
            {
                return Path.Combine(Out, name);
            }

            public void Dispose()
            {
                Directory.Delete(Path.GetDirectoryName(Out), true);
            }
        }

        // What the dialog returns when it is about to open a folder: the package, or null when it opens it.
        private static string Opening(string folder, string current, string name, bool fromNameBox = true)
        {
            FakeDialog dialog = new FakeDialog { Folder = current, Name = name };
            PackageDialog.Events events = new PackageDialog.Events { FromNameBox = () => fromNameBox };
            int result = events.OnFolderChanging(dialog, new FakeItem(folder));
            if (events.Package == null)
            {
                Assert.Equal(0, result);
                Assert.Empty(dialog.Closed);
            }
            else
            {
                // S_FALSE: the folder is not opened. The dialog is closed as with Save.
                Assert.Equal(1, result);
                Assert.Equal(new[] { 0 }, dialog.Closed);
            }
            return events.Package;
        }

        [Fact]
        public void OfferedNameOfAnExistingPackageIsConfirmed()
        {
            using (Folders folders = new Folders())
            {
                Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), folders.Out, "robot_pkg"));
                Assert.Equal(folders.In("ROBOT_PKG"), Opening(folders.In("ROBOT_PKG"), folders.Out, "robot_pkg"));
                Assert.Equal(folders.In(folders.Chinese), Opening(folders.In(folders.Chinese), folders.Out, folders.Chinese));
            }
        }

        [Fact]
        public void FullPathOfAnExistingPackageIsConfirmed()
        {
            using (Folders folders = new Folders())
            {
                foreach (string current in new[] { folders.Out, Path.GetTempPath(), folders.In("plain"), null })
                {
                    Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), current, folders.In("robot_pkg")));
                    Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), current, "\"" + folders.In("robot_pkg") + "\""));
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetShortPathName(string path, StringBuilder shortPath, uint size);

        [Fact]
        public void ShortPathOfAnExistingPackageIsConfirmed()
        {
            using (Folders folders = new Folders())
            {
                StringBuilder path = new StringBuilder(1024);
                Assert.True(GetShortPathName(folders.In("robot_pkg"), path, (uint)path.Capacity) > 0);
                Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), folders.Out, path.ToString()));
            }
        }

        [Fact]
        public void RelativePathOfAnExistingPackageIsConfirmed()
        {
            using (Folders folders = new Folders())
            {
                Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), folders.In("plain"), @"..\robot_pkg"));
                Assert.Equal(folders.In("robot_pkg"), Opening(folders.In("robot_pkg"), folders.In("plain"), "../robot_pkg"));
            }
        }

        [Fact]
        public void PackageOfAnEarlierVersionIsConfirmed()
        {
            // The part export wrote no package.xml before.
            using (Folders folders = new Folders())
            {
                Assert.Equal(folders.In("old_pkg"), Opening(folders.In("old_pkg"), folders.Out, "old_pkg"));
            }
        }

        [Fact]
        public void FolderThatIsNoPackageIsOpened()
        {
            using (Folders folders = new Folders())
            {
                Assert.Null(Opening(folders.In("plain"), folders.Out, "plain"));
                Assert.Null(Opening(folders.Out, Path.GetTempPath(), folders.Out));
                // The package folder the user is in holds an empty folder of the same name.
                Assert.Null(Opening(folders.In(@"robot_pkg\robot_pkg"), folders.In("robot_pkg"), "robot_pkg"));
            }
        }

        [Fact]
        public void FolderWithAnotherNameThanTheTypedOneIsOpened()
        {
            using (Folders folders = new Folders())
            {
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, "new_pkg"));
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, ""));
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, null));
                Assert.Null(Opening(folders.In("robot_pkg"), null, "robot_pkg"));
                // Up one level, from the folder inside the package.
                Assert.Null(Opening(folders.In("robot_pkg"), folders.In(@"robot_pkg\robot_pkg"), "robot_pkg"));
            }
        }

        [Fact]
        public void PathThatAsksToOpenTheFolderOpensIt()
        {
            using (Folders folders = new Folders())
            {
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, @"robot_pkg\"));
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, folders.In("robot_pkg") + "/"));
                Assert.Null(Opening(folders.In("robot_pkg"), folders.In(@"robot_pkg\robot_pkg"), ".."));
                Assert.Null(Opening(folders.In("robot_pkg"), folders.In("robot_pkg"), "."));
            }
        }

        [Fact]
        public void PackageThatIsOpenedInTheListIsOpened()
        {
            using (Folders folders = new Folders())
            {
                Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, "robot_pkg", false));
            }
        }

        [Fact]
        public void OddNamesAndFoldersAreLeftToTheDialog()
        {
            using (Folders folders = new Folders())
            {
                Assert.Null(Opening(null, folders.Out, "robot_pkg"));
                foreach (string name in new[] { "a|b", "<robot>", "*.urdf", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "C:", new string('x', 40000) })
                {
                    Assert.Null(Opening(folders.In("robot_pkg"), folders.Out, name));
                }
            }
        }
    }
}