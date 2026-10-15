// 작업표시줄을 담당하는 explorer.exe 에 DLL 을 넣거나(load) 뺀다(unload).
// 사용법: inject.exe load <dll 경로> | inject.exe unload <dll 이름> | inject.exe status <dll 이름>
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

class Inject
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll")] static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    const uint ACCESS = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400; // CREATE_THREAD|VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION

    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("inject.exe load <dll> | unload <dll 이름> | status <dll 이름>"); return 2; }
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { Console.WriteLine("작업표시줄 창을 찾지 못했습니다."); return 1; }
        uint pid; GetWindowThreadProcessId(tray, out pid);
        Process proc = Process.GetProcessById((int)pid);
        if (!string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("explorer 가 아닙니다: " + proc.ProcessName); return 1; }
        Console.WriteLine("대상: explorer.exe PID " + pid);

        string cmd = a[0].ToLowerInvariant();
        if (cmd == "status") { Console.WriteLine(Find(proc, a[1]) != IntPtr.Zero ? "로드됨" : "로드 안 됨"); return 0; }

        IntPtr h = OpenProcess(ACCESS, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("프로세스를 열 수 없습니다. err=" + Marshal.GetLastWin32Error()); return 1; }
        try
        {
            if (cmd == "load")
            {
                string dll = Path.GetFullPath(a[1]);
                if (!File.Exists(dll)) { Console.WriteLine("DLL 이 없습니다: " + dll); return 1; }
                if (Find(proc, Path.GetFileName(dll)) != IntPtr.Zero) { Console.WriteLine("이미 로드되어 있습니다."); return 0; }
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(dll + "\0");
                IntPtr mem = VirtualAllocEx(h, IntPtr.Zero, (UIntPtr)bytes.Length, 0x3000, 0x04);
                UIntPtr w;
                WriteProcessMemory(h, mem, bytes, (UIntPtr)bytes.Length, out w);
                IntPtr ll = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                uint code = Run(h, ll, mem);
                VirtualFreeEx(h, mem, UIntPtr.Zero, 0x8000);
                Console.WriteLine(Find(proc, Path.GetFileName(dll)) != IntPtr.Zero ? "로드했습니다." : "로드 실패 (반환 " + code + ")");
                return 0;
            }
            if (cmd == "unload")
            {
                IntPtr m = Find(proc, a[1]);
                if (m == IntPtr.Zero) { Console.WriteLine("로드되어 있지 않습니다."); return 0; }
                IntPtr fl = GetProcAddress(GetModuleHandle("kernel32.dll"), "FreeLibrary");
                Run(h, fl, m);
                Console.WriteLine(Find(proc, a[1]) == IntPtr.Zero ? "내렸습니다." : "내리지 못했습니다.");
                return 0;
            }
            Console.WriteLine("알 수 없는 명령: " + cmd);
            return 2;
        }
        finally { CloseHandle(h); }
    }

    static IntPtr Find(Process p, string dllName)
    {
        p.Refresh();
        foreach (ProcessModule m in p.Modules)
            if (string.Equals(m.ModuleName, dllName, StringComparison.OrdinalIgnoreCase)) return m.BaseAddress;
        return IntPtr.Zero;
    }

    static uint Run(IntPtr h, IntPtr fn, IntPtr param)
    {
        uint tid;
        IntPtr t = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, fn, param, 0, out tid);
        if (t == IntPtr.Zero) return 0;
        WaitForSingleObject(t, 10000);
        uint code; GetExitCodeThread(t, out code);
        CloseHandle(t);
        return code;
    }
}
