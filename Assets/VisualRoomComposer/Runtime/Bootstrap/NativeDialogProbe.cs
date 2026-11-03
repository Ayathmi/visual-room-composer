using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisualRoomComposer.Infrastructure;

namespace VisualRoomComposer.Bootstrap
{
    /// <summary>Automates only a new common dialog owned by this development Player's Unity window.</summary>
    public static class NativeDialogProbe
    {
        private const int FindTimeoutMilliseconds = 10000;
        private const int CloseTimeoutMilliseconds = 10000;
        private const int MessageTimeoutMilliseconds = 1000;
        private const int FileNameTimeoutMilliseconds = 5000;
        private const int DiagnosticTimeoutMilliseconds = 1000;
        private const int CancelGraceMilliseconds = 2000;
        private const int PollMilliseconds = 25;
        private const uint WmCommand = 0x0111;
        private const uint CdmSetControlText = 0x0468;
        private const uint WmSetText = 0x000c;
        private const uint WmGetText = 0x000d;
        private const int FileNameEditId = 1152;
        private const int FileNameComboId = 1148;
        private const int TextCapacity = 32768;
        private const int IdOk = 1;
        private const int IdCancel = 2;
        private const uint OwnerCommand = 4;
        private static int running;
        private static string lastDiagnostic = "NotRun";

        /// <summary>Upper bound of helper deadlines, excluding native-call failure and scheduling delays.</summary>
        public const int MaximumAutomationMilliseconds = FindTimeoutMilliseconds + FileNameTimeoutMilliseconds + CloseTimeoutMilliseconds + MessageTimeoutMilliseconds + DiagnosticTimeoutMilliseconds + CancelGraceMilliseconds;

        /// <summary>Describes the last automation result; a runner must still enforce a process timeout.</summary>
        public static string LastDiagnostic => Volatile.Read(ref lastDiagnostic);

        /// <summary>Verifies user cancellation, correct Player ownership and unchanged working directory.</summary>
        public static bool Cancel(bool save) => Run(save, null);

        /// <summary>Selects an existing local file using Unicode text and verifies the exact returned path.</summary>
        public static bool SelectOpen(string desiredPath)
        {
            try
            {
                var path = NormalizePath(desiredPath);
                if (path.Length >= 32768 || path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path))
                {
                    Volatile.Write(ref lastDiagnostic, "The selection probe requires an existing local file within the dialog buffer limit.");
                    return false;
                }
                return Run(false, path);
            }
            catch (Exception error)
            {
                Volatile.Write(ref lastDiagnostic, "Invalid selection path: " + error.Message);
                return false;
            }
        }

        private static bool Run(bool save, string desiredPath)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
            ProbeState state = null;
            Task<bool> automation = null;
            string selected = null;
            Exception dialogError = null;
            try
            {
                Volatile.Write(ref lastDiagnostic, "Running");
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Volatile.Write(ref lastDiagnostic, "Windows is required.");
                    return false;
                }
                uint processId;
                IntPtr owner;
                using (var process = Process.GetCurrentProcess())
                {
                    processId = (uint)process.Id;
                    owner = GetActiveWindow();
                    if (!IsPlayerWindow(owner, processId)) owner = process.MainWindowHandle;
                }
                if (!IsPlayerWindow(owner, processId))
                {
                    Volatile.Write(ref lastDiagnostic, "No current-process UnityWndClass owner; no dialog was opened.");
                    return false;
                }
                state = new ProbeState(processId, owner, desiredPath);
                Enumerate(window => { if (IsRootDialog(window, state)) state.Existing.Add(window); });
                var before = Environment.CurrentDirectory;
                automation = Task.Run(() => Automate(state));
                try { selected = save ? WindowsFileDialog.SaveFbx() : WindowsFileDialog.OpenModel(); }
                catch (Exception error) { dialogError = error; }
                finally { state.Returned.Set(); }

                var automated = automation.GetAwaiter().GetResult();
                var matchingPath = desiredPath == null ? selected == null : selected != null &&
                    string.Equals(NormalizePath(selected), desiredPath, StringComparison.OrdinalIgnoreCase) && File.Exists(selected);
                var success = automated && dialogError == null && matchingPath && before == Environment.CurrentDirectory;
                Volatile.Write(ref lastDiagnostic, dialogError != null ? dialogError.ToString() : success ?
                    (desiredPath == null ? "Cancelled; Player owner verified; working directory unchanged." : "Selected the exact existing path; Player owner verified; working directory unchanged. " + state.SelectionDiagnostic) :
                    state.Diagnostic + "; returned=" + (selected ?? "<cancelled>") + "; cwdUnchanged=" + (before == Environment.CurrentDirectory));
                return success;
            }
            catch (Exception error)
            {
                Volatile.Write(ref lastDiagnostic, error.ToString());
                return false;
            }
            finally
            {
                if (state != null)
                {
                    state.Returned.Set();
                    if (automation != null && !automation.IsCompleted)
                    {
                        try { automation.GetAwaiter().GetResult(); }
                        catch { }
                    }
                    state.Dispose();
                }
                Volatile.Write(ref running, 0);
            }
        }

        private static bool Automate(ProbeState state)
        {
            try
            {
                var find = Stopwatch.StartNew();
                while (!state.Returned.IsSet && find.ElapsedMilliseconds < FindTimeoutMilliseconds)
                {
                    IntPtr found = IntPtr.Zero;
                    var count = 0;
                    Enumerate(window =>
                    {
                        if (state.Existing.Contains(window) || !IsRootDialog(window, state) || !IsWindowVisible(window) || GetDlgItem(window, IdCancel) == IntPtr.Zero) return;
                        found = window;
                        count++;
                    });
                    if (count == 1) { state.Dialog = found; break; }
                    if (count > 1) return FailAndCancel(state, "Ambiguous new dialogs; no acceptance command sent.");
                    state.Returned.Wait(PollMilliseconds);
                }
                if (state.Dialog == IntPtr.Zero) return FailAndCancel(state, "Dialog discovery deadline exceeded or dialog returned before verification.");
                if (state.Returned.IsSet || !IsRootDialog(state.Dialog, state)) return FailAndCancel(state, "Dialog ownership changed before automation.");

                if (state.PathText != IntPtr.Zero)
                {
                    if (!PrepareFileName(state)) return FailAndCancel(state, "The visible filename control was not verified before its deadline.");
                }
                var command = state.PathText == IntPtr.Zero ? IdCancel : IdOk;
                var button = GetDlgItem(state.Dialog, command);
                if (state.Returned.IsSet || !IsRootDialog(state.Dialog, state) ||
                    button == IntPtr.Zero || !IsOwnedControl(button, state) || !IsWindowEnabled(button) ||
                    !PostMessage(state.Dialog, WmCommand, new IntPtr(command), button))
                    return FailAndCancel(state, "Dialog command could not be queued for the verified owner.");

                var close = Stopwatch.StartNew();
                while (close.ElapsedMilliseconds < CloseTimeoutMilliseconds)
                {
                    if (state.Returned.Wait(PollMilliseconds))
                    {
                        state.Diagnostic = "The native call returned after the verified dialog command.";
                        return true;
                    }
                }
                return FailAndCancel(state, "Dialog acceptance/cancellation deadline exceeded.");
            }
            catch (Exception error) { return FailAndCancel(state, "Automation failed: " + error.Message); }
        }

        private static bool FailAndCancel(ProbeState state, string reason)
        {
            state.Diagnostic = reason + "; selection=" + state.SelectionDiagnostic;
            if (state.PathText != IntPtr.Zero && !state.Returned.IsSet && IsRootDialog(state.Dialog, state))
            {
                try { state.Diagnostic += "; controls=" + DescribeControls(state); }
                catch (Exception error) { state.Diagnostic += "; control diagnostic failed: " + error.Message; }
            }
            Volatile.Write(ref lastDiagnostic, state.Diagnostic + " Attempting scoped cancellation; an external Player timeout is still required.");
            var grace = Stopwatch.StartNew();
            while (!state.Returned.IsSet && grace.ElapsedMilliseconds < CancelGraceMilliseconds)
            {
                if (state.Dialog == IntPtr.Zero)
                {
                    IntPtr found = IntPtr.Zero;
                    var count = 0;
                    Enumerate(window => { if (!state.Existing.Contains(window) && IsRootDialog(window, state)) { found = window; count++; } });
                    if (count == 1) state.Dialog = found;
                }
                if (IsRootDialog(state.Dialog, state))
                {
                    // Dismiss only nested common-dialog warnings whose verified owner chain reaches this dialog.
                    Enumerate(window =>
                    {
                        if (IsNestedDialog(window, state)) PostMessage(window, WmCommand, new IntPtr(IdCancel), IntPtr.Zero);
                    });
                    if (IsRootDialog(state.Dialog, state)) PostMessage(state.Dialog, WmCommand, new IntPtr(IdCancel), IntPtr.Zero);
                }
                state.Returned.Wait(PollMilliseconds);
            }
            // A native call that ignores cancellation remains subject to the runner's hard process timeout.
            return false;
        }

        private static bool PrepareFileName(ProbeState state)
        {
            var watch = Stopwatch.StartNew();
            while (!state.Returned.IsSet && watch.ElapsedMilliseconds < FileNameTimeoutMilliseconds)
            {
                var controls = ReadControls(state);
                ControlInfo selected = null;
                var best = int.MaxValue;
                var ties = 0;
                foreach (var control in controls)
                {
                    if (!control.Visible || !control.Enabled) continue;
                    var priority = FileNamePriority(control, state);
                    if (priority < best) { selected = control; best = priority; ties = 1; }
                    else if (priority == best && priority != int.MaxValue) ties++;
                }
                if (selected != null && best != int.MaxValue && ties == 1)
                {
                    state.SelectionDiagnostic = selected.Describe();
                    var timeout = Remaining(watch, FileNameTimeoutMilliseconds, MessageTimeoutMilliseconds);
                    if (timeout == 0 || !IsOwnedControl(selected.Window, state)) break;
                    // The actual Edit under cmb13 can differ from the legacy edt1 addressed by CDM messages.
                    var delivered = SendMessageTimeout(selected.Window, WmSetText, IntPtr.Zero, state.PathText,
                        0x0001 | 0x0002 | 0x0020, timeout, out var setResult);
                    timeout = Remaining(watch, FileNameTimeoutMilliseconds, MessageTimeoutMilliseconds);
                    if (delivered != IntPtr.Zero && setResult != UIntPtr.Zero && timeout != 0 &&
                        ReadControlText(selected.Window, state, TextCapacity, timeout, out var actual))
                    {
                        state.SelectionDiagnostic += "; readback=" + Abbreviate(actual);
                        if (string.Equals(actual, state.DesiredPath, StringComparison.Ordinal)) return true;
                    }
                    // Retain a common-dialog fallback, but it is accepted only after reading the visible control back.
                    timeout = Remaining(watch, FileNameTimeoutMilliseconds, MessageTimeoutMilliseconds);
                    if (timeout != 0 && IsRootDialog(state.Dialog, state))
                    {
                        SendMessageTimeout(state.Dialog, CdmSetControlText, new IntPtr(FileNameComboId), state.PathText,
                            0x0001 | 0x0002 | 0x0020, timeout, out _);
                        timeout = Remaining(watch, FileNameTimeoutMilliseconds, MessageTimeoutMilliseconds);
                        if (timeout != 0 && ReadControlText(selected.Window, state, TextCapacity, timeout, out var fallback) &&
                            string.Equals(fallback, state.DesiredPath, StringComparison.Ordinal))
                        {
                            state.SelectionDiagnostic += "; cmb13 fallback readback matched";
                            return true;
                        }
                    }
                }
                else state.SelectionDiagnostic = ties > 1 ? "Ambiguous visible filename controls." : "No visible edt1 or cmb13 filename control yet.";
                state.Returned.Wait(PollMilliseconds);
            }
            return false;
        }

        private static int FileNamePriority(ControlInfo control, ProbeState state)
        {
            if (string.Equals(control.ClassName, "Edit", StringComparison.OrdinalIgnoreCase))
            {
                var current = control.Window;
                for (var depth = 0; depth < 8 && current != state.Dialog; depth++)
                {
                    if (!IsOwnedControl(current, state)) break;
                    if (GetDlgCtrlID(current) == FileNameComboId) return 0;
                    current = GetParent(current);
                }
                if (control.Id == FileNameEditId) return 1;
            }
            if (control.Id == FileNameComboId && string.Equals(control.ClassName, "ComboBox", StringComparison.OrdinalIgnoreCase)) return 2;
            if (control.Id == FileNameComboId && string.Equals(control.ClassName, "ComboBoxEx32", StringComparison.OrdinalIgnoreCase)) return 3;
            return int.MaxValue;
        }

        private static List<ControlInfo> ReadControls(ProbeState state)
        {
            var result = new List<ControlInfo>();
            if (!IsRootDialog(state.Dialog, state)) return result;
            WindowCallback callback = (window, data) =>
            {
                if (!IsOwnedControl(window, state)) return true;
                var name = new StringBuilder(128);
                GetClassName(window, name, name.Capacity);
                result.Add(new ControlInfo(window, GetDlgCtrlID(window), name.ToString(), GetDlgCtrlID(GetParent(window)), IsWindowVisible(window), IsWindowEnabled(window)));
                return result.Count < 256;
            };
            EnumChildWindows(state.Dialog, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        private static string DescribeControls(ProbeState state)
        {
            var watch = Stopwatch.StartNew();
            var result = new StringBuilder();
            var controls = ReadControls(state);
            controls.Sort((left, right) => DiagnosticPriority(left).CompareTo(DiagnosticPriority(right)));
            for (var index = 0; index < controls.Count && index < 40; index++)
            {
                var item = controls[index];
                if (index != 0) result.Append(" | ");
                result.Append(item.Describe());
                var timeout = Remaining(watch, DiagnosticTimeoutMilliseconds, 50);
                if (timeout == 0) { result.Append(" [diagnostic deadline]"); break; }
                if (ReadControlText(item.Window, state, 256, timeout, out var text)) result.Append(" text=").Append(Abbreviate(text));
                else result.Append(" text=<unavailable>");
            }
            return result.ToString();
        }

        private static bool ReadControlText(IntPtr window, ProbeState state, int capacity, uint timeout, out string text)
        {
            text = null;
            if (!IsOwnedControl(window, state) || state.Returned.IsSet) return false;
            Marshal.WriteInt16(state.ReadText, 0, 0);
            var delivered = SendMessageTimeout(window, WmGetText, new IntPtr(capacity), state.ReadText,
                0x0001 | 0x0002 | 0x0020, timeout, out var count);
            if (delivered == IntPtr.Zero || count.ToUInt64() >= (ulong)capacity) return false;
            text = Marshal.PtrToStringUni(state.ReadText, (int)count.ToUInt64());
            return true;
        }

        private static bool IsOwnedControl(IntPtr window, ProbeState state)
        {
            if (window == IntPtr.Zero || !IsRootDialog(state.Dialog, state) || !IsChild(state.Dialog, window) || !IsWindow(window)) return false;
            GetWindowThreadProcessId(window, out var processId);
            return processId == state.ProcessId;
        }

        private static uint Remaining(Stopwatch watch, int total, int perMessage)
        {
            var remaining = total - watch.ElapsedMilliseconds;
            return remaining <= 0 ? 0 : (uint)Math.Min(remaining, perMessage);
        }

        private static string Abbreviate(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 200 ? text : text.Substring(0, 200) + "...";
        }

        private static int DiagnosticPriority(ControlInfo control)
        {
            if (control.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase)) return 0;
            if (control.ClassName.StartsWith("ComboBox", StringComparison.OrdinalIgnoreCase)) return 1;
            if (control.ClassName.Equals("Button", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        private sealed class ControlInfo
        {
            public readonly IntPtr Window;
            public readonly int Id, ParentId;
            public readonly string ClassName;
            public readonly bool Visible, Enabled;
            public ControlInfo(IntPtr window, int id, string className, int parentId, bool visible, bool enabled)
            { Window = window; Id = id; ClassName = className; ParentId = parentId; Visible = visible; Enabled = enabled; }
            public string Describe() => "id=" + Id + ",class=" + ClassName + ",parentId=" + ParentId + ",visible=" + Visible + ",enabled=" + Enabled;
        }

        private static bool IsRootDialog(IntPtr window, ProbeState state) => window != IntPtr.Zero &&
            IsProcessClass(window, state.ProcessId, "#32770") && GetWindow(window, OwnerCommand) == state.Owner && IsPlayerWindow(state.Owner, state.ProcessId);

        private static bool IsNestedDialog(IntPtr window, ProbeState state)
        {
            if (window == state.Dialog || state.Existing.Contains(window)) return false;
            var current = window;
            for (var depth = 0; depth < 8; depth++)
            {
                if (!IsProcessClass(current, state.ProcessId, "#32770")) return false;
                current = GetWindow(current, OwnerCommand);
                if (current == state.Dialog) return IsRootDialog(state.Dialog, state);
                if (current == IntPtr.Zero) return false;
            }
            return false;
        }

        private static bool IsPlayerWindow(IntPtr window, uint processId) => IsProcessClass(window, processId, "UnityWndClass");

        private static bool IsProcessClass(IntPtr window, uint processId, string expectedClass)
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return false;
            GetWindowThreadProcessId(window, out var ownerProcess);
            if (ownerProcess != processId) return false;
            var name = new StringBuilder(128);
            return GetClassName(window, name, name.Capacity) != 0 && name.ToString() == expectedClass;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            return Path.GetFullPath(path);
        }

        private static void Enumerate(Action<IntPtr> action)
        {
            WindowCallback callback = (window, data) => { action(window); return true; };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }

        private sealed class ProbeState : IDisposable
        {
            public readonly uint ProcessId;
            public readonly IntPtr Owner;
            public readonly IntPtr PathText;
            public readonly IntPtr ReadText;
            public readonly string DesiredPath;
            public readonly HashSet<IntPtr> Existing = new HashSet<IntPtr>();
            public readonly ManualResetEventSlim Returned = new ManualResetEventSlim(false);
            public IntPtr Dialog;
            public string Diagnostic = "The dialog result did not match the probe request.";
            public string SelectionDiagnostic = "No filename mutation requested.";

            public ProbeState(uint processId, IntPtr owner, string desiredPath)
            {
                ProcessId = processId;
                Owner = owner;
                DesiredPath = desiredPath;
                if (desiredPath != null) PathText = Marshal.StringToHGlobalUni(desiredPath);
                try { ReadText = Marshal.AllocHGlobal(TextCapacity * 2); }
                catch
                {
                    if (PathText != IntPtr.Zero) Marshal.FreeHGlobal(PathText);
                    Returned.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (PathText != IntPtr.Zero) Marshal.FreeHGlobal(PathText);
                Marshal.FreeHGlobal(ReadText);
                Returned.Dispose();
            }
        }

        private delegate bool WindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int controlId);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    }
}
