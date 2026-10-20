using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarGroup
{
    /// <summary>"즉시 배치": 작업표시줄을 담당하는 explorer.exe 에 hook\placer.dll 을 넣어,
    /// 새 버튼이 만들어지는 순간 그룹 순서에 맞는 자리에 붙게 한다.
    /// 이 클래스는 (1) DLL 이 읽을 배치 표(placement.txt)를 쓰고 (2) DLL 을 넣고 뺀다.
    /// DLL 이 없거나 explorer 빌드가 달라 동작하지 못해도 기존 정렬(Arranger)이 뒤에서 보정한다.</summary>
    public static class Placement
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint prot);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll")] static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        private const uint ACCESS = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const string DllName = "placer.dll";
        private static readonly object _lock = new object();
        private static string _lastTable;

        public static string HookDir { get { return Path.Combine(GroupStore.AppDir, "hook"); } }
        public static string DllPath { get { return Path.Combine(HookDir, DllName); } }
        public static string TablePath { get { return Path.Combine(HookDir, "placement.txt"); } }

        public static bool Available { get { return File.Exists(DllPath) && Environment.Is64BitProcess; } }

        // ---------- 버튼별 창 ----------

        private static readonly object _btnLock = new object();
        private static DateTime _attachFailedAt = DateTime.MinValue;

        /// <summary>DLL 에게 주 작업표시줄 버튼마다 앱 ID·창 핸들을 물어 buttons 에 채운다.
        /// 버튼 이름("Python" 등)이 같아도 창 → 프로세스로 어느 항목의 버튼인지 알 수 있게 된다.
        /// DLL 이 없거나 답이 UIA 목록과 맞지 않으면 아무것도 채우지 않고 false (이름으로 판단하는 기존 방식).</summary>
        public static bool AttachWindows(Config cfg, List<TaskbarButton> buttons)
        {
            foreach (TaskbarButton b in buttons) { b.AppId = null; b.Windows = null; }
            if (cfg == null || !cfg.InstantPlacement || buttons.Count == 0 || !Available) return false;
            // 답이 없던 직후에는 매번 기다리지 않는다
            if ((DateTime.Now - _attachFailedAt).TotalSeconds < 60) return false;
            IntPtr list = TaskbarUia.TaskListHwnd(TaskbarUia.TrayHwnd());
            if (list == IntPtr.Zero) return false;

            string req = Path.Combine(HookDir, "buttons_request.txt");
            string res = Path.Combine(HookDir, "buttons.txt");
            string[] lines = null;
            lock (_btnLock)
            {
                try
                {
                    if (File.Exists(res)) File.Delete(res);
                    File.WriteAllText(req, list.ToInt64().ToString("x"));
                }
                catch { return false; }
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 800)
                {
                    System.Threading.Thread.Sleep(30);
                    if (!File.Exists(res)) continue;
                    try { lines = File.ReadAllLines(res, Encoding.UTF8); break; }
                    catch { }
                }
            }
            if (lines == null || lines.Length == 0)
            {
                _attachFailedAt = DateTime.Now;
                try { File.Delete(req); } catch { }
                Log.Write("버튼별 창 정보를 받지 못함 (이름으로 판단)");
                return false;
            }

            string[] head = lines[0].Split(' ');
            int count;
            if (head.Length < 3 || head[0] != "LIST" || head[1] != list.ToInt64().ToString("x") ||
                !int.TryParse(head[2], out count) || count != buttons.Count || lines.Length < count + 1)
                return false;

            string[] appIds = new string[count];
            List<IntPtr>[] wins = new List<IntPtr>[count];
            for (int i = 0; i < count; i++)
            {
                string[] f = lines[i + 1].Split('|');
                if (f.Length < 4) return false;
                appIds[i] = f[1];
                wins[i] = new List<IntPtr>();
                foreach (string h in f[3].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    long v;
                    if (long.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out v)) wins[i].Add(new IntPtr(v));
                }
                // 같은 순서의 버튼인지: 창이 있는 버튼 = UIA 에서 실행 중인 버튼
                if ((wins[i].Count > 0) != buttons[i].Running) return false;
            }
            for (int i = 0; i < count; i++) { buttons[i].AppId = appIds[i]; buttons[i].Windows = wins[i]; }
            return true;
        }

        // ---------- 배치 표 ----------

        /// <summary>그룹·항목 순서와 항목별 식별 키(실행 파일 경로, 앱 ID)를 DLL 이 읽을 파일로 쓴다.
        /// 내용이 그대로면 쓰지 않는다.</summary>
        public static void WriteTable(Config cfg)
        {
            if (cfg == null || !Directory.Exists(HookDir)) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("M ").Append(cfg.ArrangeMode == Config.ModeAfterLastGroup ? 1 : 0).Append('\n');
            foreach (Group g in cfg.Groups)
            {
                sb.Append("G ").Append(g.Id).Append('\n');
                foreach (Entry e in g.Entries)
                {
                    foreach (List<string> keys in KeyLinesFor(e))
                        sb.Append("E ").Append(string.Join("|", keys.ToArray())).Append('\n');
                }
            }
            string text = sb.ToString();
            lock (_lock)
            {
                if (text == _lastTable) return;
                try
                {
                    File.WriteAllText(TablePath, text, new UTF8Encoding(false));
                    _lastTable = text;
                }
                catch { }
            }
        }

        private static void AddKey(List<string> keys, string k)
        {
            if (string.IsNullOrEmpty(k)) return;
            k = k.Trim().ToLowerInvariant();
            if (k.Length == 0 || k.IndexOf('|') >= 0 || k.IndexOf('\n') >= 0) return;
            if (!keys.Contains(k)) keys.Add(k);
        }

        /// <summary>항목 하나의 "E" 줄들. 본체 창 줄 다음에, 하위 창(같은 항목이 띄운 다른 앱 ID)이 있으면 그 줄을 하나 더 쓴다
        /// (DLL 이 줄 순서대로 놓으므로 본체 버튼이 앞에 온다).</summary>
        private static List<List<string>> KeyLinesFor(Entry e)
        {
            string ek = Arranger.EntryKey(e);
            List<string> main = Knowledge.AppIdsOf(ek, true);
            List<string> sub = Knowledge.AppIdsOf(ek, false);
            List<string> keys = KeysFor(e, main.Count + sub.Count > 0);
            foreach (string a in main) AddKey(keys, a);
            List<List<string>> lines = new List<List<string>>();
            lines.Add(keys);
            if (sub.Count > 0)
            {
                List<string> subKeys = new List<string>();
                foreach (string a in sub) AddKey(subKeys, a);
                lines.Add(subKeys);
            }
            return lines;
        }

        private static List<string> KeysFor(Entry e, bool knowAppIds)
        {
            List<string> keys = new List<string>();
            string target = e.Target ?? "";
            try
            {
                if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    AddKey(keys, Knowledge.Link(target).Target);
                    try { AddKey(keys, ShellLink.Read(target).AppId); } catch { }
                }
                else
                {
                    AddKey(keys, target);
                }
                ProcessScan.EntryHint hint = ProcessScan.Resolve(e);
                if (!hint.IsScript) AddKey(keys, hint.ExePath);
                // 스크립트/실행기가 실제로 띄운 것으로 확인된 프로세스의 실행 파일.
                // 스크립트가 띄운 것은 보통 pythonw.exe 같은 공용 실행기라서, 앱 ID 를 알게 되면 쓰지 않는다
                // (안 그러면 관계없는 다른 파이썬 앱도 이 자리에 붙는다)
                if (!(hint.IsScript && knowAppIds))
                    foreach (string exe in Knowledge.LaunchedExes(Arranger.EntryKey(e)))
                        if (ProcessScan.ExeAllowed(hint, exe)) AddKey(keys, exe);
            }
            catch { }
            return keys;
        }

        // ---------- explorer 에 DLL 넣고 빼기 ----------

        private static int ShellPid()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return 0;
            uint pid; GetWindowThreadProcessId(tray, out pid);
            try
            {
                if (!string.Equals(Process.GetProcessById((int)pid).ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) return 0;
            }
            catch { return 0; }
            return (int)pid;
        }

        private static bool IsLoaded(Process p)
        {
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, DllName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>explorer 에 DLL 이 없으면 넣는다. (이미 있으면 아무것도 안 함)</summary>
        public static bool Inject()
        {
            if (!Available) return false;
            int pid = ShellPid();
            if (pid == 0) return false;
            Process proc;
            try { proc = Process.GetProcessById(pid); } catch { return false; }
            if (IsLoaded(proc)) return true;

            IntPtr h = OpenProcess(ACCESS, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                byte[] bytes = Encoding.Unicode.GetBytes(DllPath + "\0");
                IntPtr mem = VirtualAllocEx(h, IntPtr.Zero, (UIntPtr)bytes.Length, 0x3000, 0x04);
                if (mem == IntPtr.Zero) return false;
                UIntPtr w;
                WriteProcessMemory(h, mem, bytes, (UIntPtr)bytes.Length, out w);
                IntPtr ll = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                RunRemote(h, ll, mem);
                VirtualFreeEx(h, mem, UIntPtr.Zero, 0x8000);
                proc.Refresh();
                bool ok = IsLoaded(proc);
                Log.Write(ok ? "즉시 배치 DLL 적용" : "즉시 배치 DLL 적용 실패");
                return ok;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>explorer 에서 DLL 을 뺀다. DLL 은 작업 스레드를 갖고 있어서, 종료 요청 파일(placer.stop)을
        /// 만들어 스스로 후킹을 풀고 내려가게 한다 (밖에서 강제로 FreeLibrary 하면 스레드가 죽은 코드를 실행한다).</summary>
        public static void Eject()
        {
            int pid = ShellPid();
            if (pid == 0) return;
            Process proc;
            try { proc = Process.GetProcessById(pid); } catch { return; }
            if (!IsLoaded(proc)) return;
            try { File.WriteAllText(Path.Combine(HookDir, "placer.stop"), "1"); } catch { return; }
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(100);
                proc.Refresh();
                if (!IsLoaded(proc)) { Log.Write("즉시 배치 DLL 해제"); return; }
            }
            Log.Write("즉시 배치 DLL 해제 시간 초과");
        }

        /// <summary>explorer 안의 DLL 에게 작업표시줄 고정/해제를 시킨다 (explorer 밖에서는 이 호출이 효과가 없다).
        /// 결과: true/false = 처리됨, null = DLL 을 쓸 수 없거나 응답 없음.</summary>
        public static bool? PinViaExplorer(string path, bool pin)
        {
            if (!Available) return null;
            int pid = ShellPid();
            if (pid == 0) return null;
            Process proc;
            try { proc = Process.GetProcessById(pid); } catch { return null; }
            if (!IsLoaded(proc))
            {
                if (!Inject()) return null;
                System.Threading.Thread.Sleep(600);   // 작업 스레드가 시작할 시간
            }
            string req = Path.Combine(HookDir, "pin_request.txt");
            string res = Path.Combine(HookDir, "pin_result.txt");
            try
            {
                if (File.Exists(res)) File.Delete(res);
                File.WriteAllText(req, (pin ? "PIN|" : "UNPIN|") + path, new UTF8Encoding(false));
            }
            catch { return null; }
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(150);
                if (!File.Exists(res)) continue;
                try
                {
                    string r = File.ReadAllText(res);
                    File.Delete(res);
                    return r.Contains("hr=0x00000000");
                }
                catch { }
            }
            return null;
        }

        private static void RunRemote(IntPtr h, IntPtr fn, IntPtr param)
        {
            uint tid;
            IntPtr t = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, fn, param, 0, out tid);
            if (t == IntPtr.Zero) return;
            WaitForSingleObject(t, 10000);
            CloseHandle(t);
        }

        /// <summary>설정에 맞춰 표를 쓰고 DLL 을 넣거나 뺀다. (백그라운드에서 호출)</summary>
        public static void Apply(Config cfg)
        {
            try
            {
                if (cfg != null && cfg.InstantPlacement && Available)
                {
                    WriteTable(cfg);
                    Inject();
                }
                else
                {
                    Eject();
                }
            }
            catch (Exception ex) { Log.Write("즉시 배치 적용 오류: " + ex.Message); }
        }
    }
}
