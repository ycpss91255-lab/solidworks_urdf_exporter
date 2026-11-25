using System;
using System.IO;
using System.Runtime.InteropServices;
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
            public Func<bool> FromNameBox { get; set; } = () => true;

            public int OnFileOk(IFileDialog dialog) => 0;

            public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;

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

        #region Windows

        private const uint NoChangeDir = 0x8;
        private const uint ForceFileSystem = 0x40;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid type, out IShellItem item);

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