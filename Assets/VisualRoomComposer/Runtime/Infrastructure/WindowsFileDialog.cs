using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VisualRoomComposer.Infrastructure
{
    /// <summary>Platform implementation of the neutral modal dialog boundary.</summary>
    public sealed class WindowsFileDialogService : VisualRoomComposer.Core.IFileDialogService
    {
        public string OpenModel() => WindowsFileDialog.OpenModel();
        public string SaveFbx() => WindowsFileDialog.SaveFbx();
    }

    /// <summary>Windows-owned common dialogs; a null path means user cancellation.</summary>
    public static class WindowsFileDialog
    {
        /// <summary>Opens a local model without changing the process working directory.</summary>
        public static string OpenModel() => Show(false, "Models\0*.fbx;*.glb;*.obj\0All files\0*.*\0\0", null);

        /// <summary>Chooses an FBX destination and asks before replacing an existing file.</summary>
        public static string SaveFbx() => Show(true, "FBX model\0*.fbx\0\0", "fbx");

        private static string Show(bool save, string filter, string extension)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            const int capacity = 32768;
            var owner = GetActiveWindow();
            if (owner == IntPtr.Zero) owner = Process.GetCurrentProcess().MainWindowHandle;
            var previousDirectory = Environment.CurrentDirectory;
            var allocations = new List<IntPtr>();
            try
            {
                var buffer = Marshal.AllocHGlobal(capacity * 2);
                allocations.Add(buffer);
                Marshal.WriteInt16(buffer, 0);
                var filterPointer = Marshal.StringToHGlobalUni(filter);
                allocations.Add(filterPointer);
                var extensionPointer = extension == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(extension);
                if (extensionPointer != IntPtr.Zero) allocations.Add(extensionPointer);
                var value = new OpenFileName
                {
                    size = Marshal.SizeOf<OpenFileName>(), owner = owner, filter = filterPointer,
                    file = buffer, maxFile = capacity, defaultExtension = extensionPointer,
                    flags = 0x00080000 | 0x00000008 | 0x00000800 | 0x00000004 | (save ? 2 : 0x00001000)
                };
                var accepted = save ? GetSaveFileName(ref value) : GetOpenFileName(ref value);
                if (accepted) return Marshal.PtrToStringUni(buffer);
                var error = CommDlgExtendedError();
                if (error != 0) throw new InvalidOperationException("Windows file dialog failed: 0x" + error.ToString("X"));
                return null;
            }
            finally
            {
                foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
                if (Environment.CurrentDirectory != previousDirectory) Directory.SetCurrentDirectory(previousDirectory);
            }
#else
            throw new PlatformNotSupportedException("This application currently supports Windows.");
#endif
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenFileName
        {
            public int size;
            public IntPtr owner, instance;
            public IntPtr filter, customFilter;
            public int maxCustomFilter, filterIndex;
            public IntPtr file;
            public int maxFile;
            public IntPtr fileTitle;
            public int maxFileTitle;
            public IntPtr initialDirectory, title;
            public int flags;
            public short fileOffset, fileExtension;
            public IntPtr defaultExtension;
            public IntPtr customData, hook;
            public IntPtr templateName;
            public IntPtr reserved;
            public int reservedValue, flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(ref OpenFileName data);
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSaveFileName(ref OpenFileName data);
        [DllImport("comdlg32.dll")] private static extern int CommDlgExtendedError();
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    }
}
