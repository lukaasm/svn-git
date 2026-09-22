using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Owns only this run's desktop and process tree. Never switches the user's input desktop.
public sealed class UiTestDesktop : IDisposable
{
    IntPtr desktop, job, process;

    public UiTestDesktop(string executable, string encodedCommand, string workingDirectory, string name)
    {
        IntPtr thread = IntPtr.Zero;
        try
        {
            desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
            Check(desktop != IntPtr.Zero);
            job = CreateJobObject(IntPtr.Zero, null);
            Check(job != IntPtr.Zero);
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = name, Flags = 0x81, ShowWindow = 0 };
            ProcessInfo info;
            // Suspend before assignment so no descendants can escape the cleanup job.
            Check(CreateProcess(executable, new StringBuilder("\"" + executable + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encodedCommand),
                IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, workingDirectory, ref startup, out info));
            process = info.Process;
            thread = info.Thread;
            Check(AssignProcessToJobObject(job, process));
            Check(ResumeThread(thread) != uint.MaxValue);
        }
        catch
        {
            if (process != IntPtr.Zero) TerminateProcess(process, 1);
            Dispose();
            throw;
        }
        finally { if (thread != IntPtr.Zero) CloseHandle(thread); }
    }

    public bool Wait(int milliseconds)
    {
        var result = WaitForSingleObject(process, (uint)milliseconds);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result == 0;
    }

    public uint ExitCode
    {
        get { uint code; Check(GetExitCodeProcess(process, out code)); return code; }
    }

    public void Dispose()
    {
        // Closing the job also terminates apps/WebViews left by a crashed or timed-out worker.
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
        if (desktop != IntPtr.Zero) { CloseDesktop(desktop); desktop = IntPtr.Zero; }
    }

    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
