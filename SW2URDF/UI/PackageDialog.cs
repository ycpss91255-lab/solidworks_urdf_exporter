using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SW2URDF.UI
{
    /// <summary>
    /// The Save As dialog of Windows for the folder of a package. SaveFileDialog cannot be used: it gives no
    /// access to the events of the dialog.
    /// </summary>
    internal sealed class PackageDialog : CommonDialog
    {
        public string InitialDirectory { get; set; }

        /// <summary>The name offered by the dialog, and the full path of the package after it was confirmed.</summary>
        public string FileName { get; set; }

        public override void Reset()
        {
            InitialDirectory = null;
            FileName = null;
        }

        // CommonDialog makes the other windows of the thread wait, as for SaveFileDialog.
        protected override bool RunDialog(IntPtr hwndOwner)
        {
            IFileDialog dialog = (IFileDialog)new FileSaveDialog();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | NoChangeDir | ForceFileSystem);
                if (Directory.Exists(InitialDirectory))
                {
                    Guid type = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(InitialDirectory, IntPtr.Zero, ref type, out IShellItem folder);
                    dialog.SetFolder(folder);
                }
                dialog.SetFileName(FileName ?? "");

                Events events = new Events();
                dialog.Advise(events, out uint cookie);
                int result;
                try
                {
                    result = dialog.Show(hwndOwner);
                }
                finally
                {
                    dialog.Unadvise(cookie);
                }
                if (result == Cancelled)
                {
                    return false;
                }
                Marshal.ThrowExceptionForHR(result);
                FileName = events.Package;
                if (FileName == null)
                {
                    dialog.GetResult(out IShellItem item);
                    FileName = PathOf(item);
                }
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        private static string PathOf(IShellItem item)
        {
            return item.GetDisplayName(FileSystemPath, out string path) == 0 ? path : null;
        }

        /// <summary>The events of the dialog.</summary>
        internal sealed class Events : IFileDialogEvents
        {
            /// <summary>The existing package that was confirmed, or null.</summary>
            public string Package { get; private set; }

            /// <summary>Whether the folder is opened from the name box or the Save button.</summary>
            public Func<bool> FromNameBox { get; set; } = NameBoxHasFocus;

            public int OnFileOk(IFileDialog dialog) => 0;

            // The dialog opens an existing folder whose name is confirmed instead of returning it. An existing
            // package is returned; every other folder is opened, as Windows does.
            public int OnFolderChanging(IFileDialog dialog, IShellItem folder)
            {
                string path = PathOf(folder);
                string current = dialog.GetFolder(out IShellItem item) == 0 ? PathOf(item) : null;
                if (path == null || dialog.GetFileName(out string name) != 0 ||
                    !Names(name, current, path) || !IsPackage(path) || !FromNameBox())
                {
                    return 0;
                }
                Package = path;
                dialog.Close(0);
                return 1;
            }

            public int OnFolderChange(IFileDialog dialog) => 0;

            public int OnSelectionChange(IFileDialog dialog) => 0;

            public int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response)
            {
                response = 0;
                return 0;
            }

            public int OnTypeChange(IFileDialog dialog) => 0;

            public int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response)
            {
                response = 0;
                return 0;
            }
        }

        // Whether the typed name, seen from the current folder, is this folder. A path that ends with a
        // separator or with dots asks to open the folder.
        private static bool Names(string name, string current, string folder)
        {
            name = (name ?? "").Trim().Trim('"');
            if (name == "" || name.EndsWith(@"\") || name.EndsWith("/") || name.EndsWith("."))
            {
                return false;
            }
            try
            {
                string path = Path.IsPathRooted(name) ? name : current == null ? null : Path.Combine(current, name);
                return path != null && string.Equals(LongPath(path), LongPath(folder), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
            {
                return false;
            }
        }

        // The packages of earlier versions have no package.xml when they were exported from a part.
        private static bool IsPackage(string folder)
        {
            return File.Exists(Path.Combine(folder, "package.xml")) || Directory.Exists(Path.Combine(folder, "urdf"));
        }

        private static string LongPath(string path)
        {
            path = Path.GetFullPath(path);
            StringBuilder text = new StringBuilder(32768);
            return GetLongPathName(path, text, (uint)text.Capacity) > 0 ? text.ToString() : path;
        }

        // In the list, the tree or the address bar another control has the focus, and the folder is opened.
        private static bool NameBoxHasFocus()
        {
            IntPtr focus = GetFocus();
            StringBuilder type = new StringBuilder(64);
            if (focus == IntPtr.Zero || GetClassName(focus, type, type.Capacity) == 0)
            {
                return true;
            }
            int id = GetDlgCtrlID(focus);
            return (type.ToString() == "Edit" && id == NameBox) || (type.ToString() == "Button" && id == SaveButton);
        }

        #region Windows

        private const int NameBox = 1001;
        private const int SaveButton = 1;
        private const uint NoChangeDir = 0x8;
        private const uint ForceFileSystem = 0x40;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid type, out IShellItem item);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetLongPathName(string path, StringBuilder longPath, uint size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr window);

        [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
        private class FileSaveDialog
        {
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid type, out IntPtr result);
            void GetParent(out IShellItem parent);
            [PreserveSig]
            int GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IFileDialogEvents events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            [PreserveSig]
            int GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig]
            int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
        }

        [ComImport, Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFileDialogEvents
        {
            [PreserveSig]
            int OnFileOk(IFileDialog dialog);
            [PreserveSig]
            int OnFolderChanging(IFileDialog dialog, IShellItem folder);
            [PreserveSig]
            int OnFolderChange(IFileDialog dialog);
            [PreserveSig]
            int OnSelectionChange(IFileDialog dialog);
            [PreserveSig]
            int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response);
            [PreserveSig]
            int OnTypeChange(IFileDialog dialog);
            [PreserveSig]
            int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response);
        }

        #endregion Windows
    }
}