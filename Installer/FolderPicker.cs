using System;
using System.Runtime.InteropServices;

namespace ManjuInstaller
{
    /// <summary>
    /// THE "BROWSE" BUTTON OF THE LOCATION PAGE: the Windows folder dialog.
    ///
    /// The dialog is `IFileOpenDialog` with `FOS_PICKFOLDERS`. The few methods it needs are called
    /// through their vtable slots, in the order `ShObjIdl_core.h` declares them: IUnknown 0 to 2,
    /// `IModalWindow::Show` 3, then `IFileDialog` from `SetFileTypes` at 4, so `SetOptions` 9,
    /// `GetOptions` 10, `SetFolder` 12, `SetTitle` 17, `GetResult` 20; `IShellItem::GetDisplayName` 5.
    /// </summary>
    public static unsafe class FolderPicker
    {
        private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid IID_IFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
        private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid iid, out IntPtr item);

        private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);
        }

        /// <summary>Shows the dialog over <paramref name="owner"/>, opened at
        /// <paramref name="startIn"/> when that folder exists. Returns the chosen folder, or null when
        /// the dialog was cancelled or could not be shown.</summary>
        public static string? Pick(IntPtr owner, string? startIn, string title)
        {
            if (CoCreateInstance(CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IFileOpenDialog,
                                 out IntPtr dialog) < 0 || dialog == IntPtr.Zero)
                return null;
            IntPtr result = IntPtr.Zero;
            try
            {
                uint options;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, 10))(dialog, &options);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, 9))(
                    dialog, options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

                fixed (char* t = title)
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 17))(dialog, t);

                if (!string.IsNullOrWhiteSpace(startIn) && System.IO.Directory.Exists(startIn)
                    && SHCreateItemFromParsingName(startIn, IntPtr.Zero, IID_IShellItem, out IntPtr folder) >= 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 12))(dialog, folder);
                    Release(folder);
                }

                // Show answers HRESULT_FROM_WIN32(ERROR_CANCELLED) when the dialog is closed without a choice.
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 3))(dialog, owner) < 0)
                    return null;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dialog, 20))(dialog, &result) < 0
                    || result == IntPtr.Zero)
                    return null;

                IntPtr name;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(result, 5))(result, SIGDN_FILESYSPATH, &name) < 0
                    || name == IntPtr.Zero)
                    return null;
                try { return Marshal.PtrToStringUni(name); }
                finally { CoTaskMemFree(name); }
            }
            finally
            {
                Release(result);
                Release(dialog);
            }
        }
    }
}
