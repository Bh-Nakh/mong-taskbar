using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarGroup
{
    /// <summary>작업표시줄에 창을 띄운 프로세스들의 실행 파일·명령줄·작업 폴더·창 제목.
    /// bat/스크립트로 실행한 앱(예: run.bat → pythonw app.py)은 버튼 이름이 "Python" 처럼
    /// 항목 이름과 전혀 달라서, 어떤 항목이 띄운 창인지 프로세스 정보로 알아낸다.</summary>
    public class RunningProcess
    {
        public int Pid;
        public string ExePath = "";
        public string CommandLine = "";
        public string CurrentDirectory = "";
        public List<string> WindowTitles = new List<string>();
    }

    public static class ProcessScan
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder s, ref int size);
        [DllImport("kernel32.dll")] private static extern bool IsWow64Process(IntPtr h, out bool wow);
        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int size, out int ret);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_APPWINDOW = 0x40000;

        /// <summary>작업표시줄에 버튼이 생기는 창(보이고, 소유자 없고, 도구 창이 아닌)을 가진 프로세스 목록.</summary>
        public static List<RunningProcess> Scan()
        {
            Dictionary<int, RunningProcess> map = new Dictionary<int, RunningProcess>();
            int self = Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                bool owned = GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero;
                if ((ex & WS_EX_APPWINDOW) == 0 && (owned || (ex & WS_EX_TOOLWINDOW) != 0)) return true;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.Length == 0) return true;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0 || pid == self) return true;
                RunningProcess rp;
                if (!map.TryGetValue(pid, out rp))
                {
                    rp = new RunningProcess();
                    rp.Pid = pid;
                    map[pid] = rp;
                }
                rp.WindowTitles.Add(sb.ToString());
                return true;
            }, IntPtr.Zero);

            foreach (RunningProcess rp in map.Values) Fill(rp);
            return new List<RunningProcess>(map.Values);
        }

        /// <summary>작업표시줄에 버튼이 생기는 창인지.</summary>
        public static bool IsTaskbarWindow(IntPtr h)
        {
            if (!IsWindowVisible(h)) return false;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            bool owned = GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero;
            return (ex & WS_EX_APPWINDOW) != 0 || (!owned && (ex & WS_EX_TOOLWINDOW) == 0);
        }

        public static int PidOf(IntPtr h)
        {
            int pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        public static string TitleOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>프로세스 하나만 조사 (실행 파일·명령줄·작업 폴더).</summary>
        public static RunningProcess Inspect(int pid)
        {
            RunningProcess rp = new RunningProcess();
            rp.Pid = pid;
            Fill(rp);
            return rp;
        }

        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

        public static bool IsAlive(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                uint code;
                return GetExitCodeProcess(h, out code) && code == 259 /* STILL_ACTIVE */;
            }
            finally { CloseHandle(h); }
        }

        private static void Fill(RunningProcess rp)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, rp.Pid);
            bool full = h != IntPtr.Zero;
            if (!full) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, rp.Pid);
            if (h == IntPtr.Zero) return;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) rp.ExePath = sb.ToString();
                if (full) ReadParameters(h, rp);
            }
            catch { }
            finally { CloseHandle(h); }
        }

        /// <summary>PEB 의 RTL_USER_PROCESS_PARAMETERS 에서 작업 폴더와 명령줄을 읽는다 (64비트 프로세스만).</summary>
        private static void ReadParameters(IntPtr h, RunningProcess rp)
        {
            if (IntPtr.Size != 8) return;
            bool wow;
            if (IsWow64Process(h, out wow) && wow) return;   // 32비트 프로세스는 구조가 달라서 건너뜀

            PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
            int ret;
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out ret) != 0) return;
            IntPtr parms = ReadPtr(h, IntPtr.Add(pbi.PebBaseAddress, 0x20));
            if (parms == IntPtr.Zero) return;
            rp.CurrentDirectory = ReadUnicodeString(h, IntPtr.Add(parms, 0x38));
            rp.CommandLine = ReadUnicodeString(h, IntPtr.Add(parms, 0x70));
        }

        private static IntPtr ReadPtr(IntPtr h, IntPtr addr)
        {
            byte[] b = new byte[8];
            IntPtr read;
            if (!ReadProcessMemory(h, addr, b, (IntPtr)8, out read)) return IntPtr.Zero;
            return (IntPtr)BitConverter.ToInt64(b, 0);
        }

        private static string ReadUnicodeString(IntPtr h, IntPtr addr)
        {
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; (pad) PWSTR Buffer; }
            byte[] hdr = new byte[16];
            IntPtr read;
            if (!ReadProcessMemory(h, addr, hdr, (IntPtr)16, out read)) return "";
            int len = BitConverter.ToUInt16(hdr, 0);
            IntPtr buf = (IntPtr)BitConverter.ToInt64(hdr, 8);
            if (len == 0 || buf == IntPtr.Zero) return "";
            byte[] data = new byte[len];
            if (!ReadProcessMemory(h, buf, data, (IntPtr)len, out read)) return "";
            return Encoding.Unicode.GetString(data);
        }

        // ---------- 항목 ↔ 프로세스 ----------

        private static readonly string[] ScriptExts = { ".bat", ".cmd", ".py", ".pyw", ".ps1", ".vbs", ".js" };

        /// <summary>항목이 실제로 무엇을 실행하는지: 실행 파일 경로, 스크립트형이면 그 폴더.</summary>
        public class EntryHint
        {
            public string ExePath = "";
            public string Folder = "";
            public bool IsScript;
            /// <summary>exe 항목의 폴더. exe 가 다른 프로그램을 띄우는 실행기(예: CapCutAutomator.exe →
            /// pythonw capcut_ui.py)일 때, exe 자체가 안 떠 있으면 이 폴더에서 도는 프로세스를 대신 찾는다.</summary>
            public string ExeFolder = "";

            /// <summary>인수를 붙여 실행하는 exe 바로가기(예: Code.exe --profile ... 폴더, pythonw.exe app.py).
            /// 실제로 뜨는 것도 그 exe 이므로, 다른 exe 는 작업 폴더가 같아도 이 항목의 것이 아니다.</summary>
            public bool ExeWithArgs { get { return IsScript && ExePath.Length > 0; } }
        }

        /// <summary>이 항목이 띄웠을 수 있는 실행 파일인지 (인수 붙은 exe 바로가기는 그 exe 만).</summary>
        public static bool ExeAllowed(EntryHint hint, string exePath)
        {
            if (!hint.ExeWithArgs) return true;
            return string.Equals(Normalize(hint.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase);
        }

        public static EntryHint Resolve(Entry e)
        {
            EntryHint hint = new EntryHint();
            string target = e.Target ?? "";
            string args = e.Arguments ?? "";
            string wd = e.WorkingDirectory ?? "";
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // 바로가기 내용은 기록해 둔 것을 쓴다 (처음 한 번만 실제로 읽음)
                LinkRec info = Knowledge.Link(target);
                if (info.Ok)
                {
                    target = info.Target;
                    args = info.Args;
                    wd = info.WorkDir;
                }
            }

            string ext = "";
            try { ext = Path.GetExtension(target).ToLowerInvariant(); } catch { }
            bool scriptExt = Array.IndexOf(ScriptExts, ext) >= 0;

            if (ext == ".exe")
            {
                hint.ExePath = target;
                try { hint.ExeFolder = Normalize(Path.GetDirectoryName(target)); } catch { }
                if (IsTooGeneric(hint.ExeFolder)) hint.ExeFolder = "";
            }
            // 스크립트 파일이거나, 인수를 붙여 실행하는 바로가기(예: pythonw.exe app.py)는
            // 실행 파일만으로는 구분이 안 되므로 폴더로 판단한다
            if (scriptExt || args.Trim().Length > 0)
            {
                hint.IsScript = true;
                string folder = wd;
                if (string.IsNullOrEmpty(folder) || !Knowledge.DirExists(folder))
                {
                    try { folder = Path.GetDirectoryName(target); } catch { folder = ""; }
                }
                hint.Folder = Normalize(folder);
                // 공용 폴더(Windows, Program Files 등)라면 폴더 판단이 너무 넓으므로 쓰지 않는다
                if (IsTooGeneric(hint.Folder)) hint.Folder = "";
            }
            return hint;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace('/', '\\').TrimEnd('\\');
        }

        private static bool IsTooGeneric(string folder)
        {
            if (folder.Length <= 3) return true;   // 드라이브 루트
            string[] generic =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            foreach (string g in generic)
                if (!string.IsNullOrEmpty(g) && string.Equals(Normalize(g), folder, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool UnderFolder(string path, string folder)
        {
            path = Normalize(path);
            if (path.Length == 0) return false;
            return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Matches(EntryHint hint, RunningProcess p)
        {
            if (hint.ExePath.Length > 0 && !hint.IsScript &&
                string.Equals(Normalize(hint.ExePath), Normalize(p.ExePath), StringComparison.OrdinalIgnoreCase))
                return true;
            if (hint.IsScript && hint.Folder.Length > 0 && ExeAllowed(hint, p.ExePath))
            {
                if (UnderFolder(p.CurrentDirectory, hint.Folder)) return true;
                if (UnderFolder(p.ExePath, hint.Folder)) return true;
                string cmd = Normalize(p.CommandLine);
                if (cmd.IndexOf(hint.Folder + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>실행기 exe 대체 판단: 작업 폴더가 exe 폴더와 같거나, 명령줄에 그 폴더의 파일이 있음.</summary>
        public static bool MatchesLauncherFolder(EntryHint hint, RunningProcess p)
        {
            if (hint.ExeFolder.Length == 0) return false;
            if (string.Equals(Normalize(p.CurrentDirectory), hint.ExeFolder, StringComparison.OrdinalIgnoreCase)) return true;
            return Normalize(p.CommandLine).IndexOf(hint.ExeFolder + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>프로세스의 작업표시줄 버튼에 보일 수 있는 이름들.</summary>
        public static void AddButtonNames(RunningProcess p, HashSet<string> set)
        {
            foreach (string t in p.WindowTitles) AddName(set, t);
            if (string.IsNullOrEmpty(p.ExePath)) return;
            AddName(set, Path.GetFileName(p.ExePath));
            AddName(set, Path.GetFileNameWithoutExtension(p.ExePath));
            ExeRec er = Knowledge.Exe(p.ExePath);
            AddName(set, er.Description);
            AddName(set, er.Product);
        }

        private static void AddName(HashSet<string> set, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Trim();
            if (s.Length > 0) set.Add(s);
        }
    }
}

namespace TaskbarGroup
{
    /// <summary>창을 가진 실행 중 프로세스 목록 (도우미 안에서 유지).
    /// 도우미가 시작할 때 한 번만 전체를 조사하고, 그 뒤로는 새 창이 뜰 때 그 프로세스 하나만 조사해서 더한다.
    /// 끝난 프로세스는 쓸 때 빠르게 확인해서 뺀다.</summary>
    public static class ProcessRegistry
    {
        private static readonly object _lock = new object();
        private static Dictionary<int, RunningProcess> _map;

        public static void Init()
        {
            Dictionary<int, RunningProcess> m = new Dictionary<int, RunningProcess>();
            foreach (RunningProcess p in ProcessScan.Scan()) m[p.Pid] = p;
            lock (_lock) { _map = m; }
        }

        /// <summary>새로 뜬 창의 프로세스를 목록에 반영하고 돌려준다. 작업표시줄 창이 아니면 null.</summary>
        public static RunningProcess OnWindowCreated(IntPtr hwnd)
        {
            if (!ProcessScan.IsTaskbarWindow(hwnd)) return null;
            int pid = ProcessScan.PidOf(hwnd);
            if (pid == 0 || pid == System.Diagnostics.Process.GetCurrentProcess().Id) return null;
            string title = ProcessScan.TitleOf(hwnd);
            RunningProcess rp;
            lock (_lock)
            {
                if (_map == null) _map = new Dictionary<int, RunningProcess>();
                if (_map.TryGetValue(pid, out rp))
                {
                    if (title.Length > 0 && !rp.WindowTitles.Contains(title)) rp.WindowTitles.Add(title);
                    return rp;
                }
            }
            rp = ProcessScan.Inspect(pid);
            if (title.Length > 0) rp.WindowTitles.Add(title);
            lock (_lock) { _map[pid] = rp; }
            return rp;
        }

        public static List<RunningProcess> Snapshot()
        {
            List<RunningProcess> list;
            lock (_lock)
            {
                if (_map == null) return new List<RunningProcess>();
                list = new List<RunningProcess>(_map.Values);
            }
            List<RunningProcess> alive = new List<RunningProcess>();
            foreach (RunningProcess p in list)
            {
                if (ProcessScan.IsAlive(p.Pid)) alive.Add(p);
                else lock (_lock) { _map.Remove(p.Pid); }
            }
            return alive;
        }
    }
}
