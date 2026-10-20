using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarGroup
{
    /// <summary>그룹 안의 앱이 실행 중이면 그 작업표시줄 버튼을 그룹 아이콘 바로 오른쪽으로,
    /// 그룹 안의 순서대로 옮긴다. Windows 에는 버튼 순서를 바꾸는 API 가 없으므로
    /// 작업표시줄 창에 드래그 메시지를 직접 보내서 옮긴다 (실제 마우스 포인터는 움직이지 않음).</summary>
    public static class Arranger
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        /// <summary>우리가 보낸 입력임을 표시 (도우미의 마우스 훅이 무시하도록).</summary>
        public static readonly IntPtr InjectTag = new IntPtr(0x54424752);

        private static int _busy;
        private static readonly Dictionary<string, HashSet<string>> _nameCache =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ProcessScan.EntryHint> _hintCache =
            new Dictionary<string, ProcessScan.EntryHint>(StringComparer.OrdinalIgnoreCase);

        public static bool IsBusy { get { return _busy != 0; } }

        /// <summary>버튼을 실제로 옮긴 뒤 (버튼 위치가 바뀌었음을 알림).</summary>
        public static event Action Moved;

        /// <summary>그룹 항목이 바뀌었을 때 이름/항목 해석 캐시를 비운다.</summary>
        public static void ClearCaches()
        {
            lock (_nameCache) { _nameCache.Clear(); }
            lock (_hintCache) { _hintCache.Clear(); }
        }

        public static void RunAsync(Config cfg)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            Thread t = new Thread(delegate ()
            {
                try { Run(cfg); }
                catch (Exception ex) { Log.Write("정렬 오류: " + ex.Message); }
                finally
                {
                    // 이번에 새로 알게 된 항목-프로세스 연결을 즉시 배치 표에 반영
                    try { if (cfg.InstantPlacement) Placement.WriteTable(cfg); } catch { }
                    Knowledge.Save();
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        private static bool ButtonsDown()
        {
            return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0
                || (GetAsyncKeyState(0x04) & 0x8000) != 0;
        }

        /// <summary>사용자가 마우스를 놓고 잠깐 쉴 때까지 기다린다 (최대 timeoutMs).</summary>
        private static bool CursorOnTaskbar()
        {
            POINT p;
            if (!GetCursorPos(out p)) return false;
            foreach (KeyValuePair<IntPtr, Rectangle> t in TaskbarUia.AllTrays())
                if (t.Value.Contains(p.X, p.Y)) return true;
            return false;
        }

        private static bool WaitForIdle(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                // 포인터는 건드리지 않지만, 사용자가 마우스 버튼을 누르고 있거나
                // 작업표시줄 위에서 마우스를 움직이면 보낸 메시지와 섞이므로 그때만 기다린다
                if (!ButtonsDown() && !CursorOnTaskbar()) return true;
                Thread.Sleep(150);
            }
            return false;
        }

        private static void Run(Config cfg)
        {
            int moves = 0;
            for (int pass = 0; pass < 12; pass++)
            {
                // 정렬 도중에 그룹 아이콘이 새로 고정될 수 있으므로 매번 최신 고정 목록을 쓴다
                // (처음에 읽은 목록을 계속 쓰면 새 그룹 아이콘을 앱 버튼으로 착각해 밀어낸다)
                List<KeyValuePair<string, string>> pinned = ShortcutSync.PinnedGroups();
                if (pinned.Count == 0) return;
                HashSet<string> groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in pinned) groupNames.Add(kv.Key);

                List<TaskbarButton> buttons = TaskbarUia.GetButtons();
                if (buttons.Count == 0) return;
                if (!OnScreen(buttons[0].Rect)) return;   // 자동 숨김 상태 등

                int from, to;
                string movedName;
                List<RunningProcess> procs = ProcessRegistry.Snapshot();
                if (!FindNextMove(cfg, pinned, groupNames, buttons, procs, out from, out to, out movedName))
                {
                    if (moves > 0) Log.Write("정렬 완료 (" + moves + "번 이동)");
                    return;
                }
                if (moves >= 10) { Log.Write("정렬 중단: 이동 횟수 한도"); return; }
                if (!WaitForIdle(60000)) { Log.Write("정렬 보류: 마우스를 작업표시줄 위에서 쓰는 중 (" + movedName + ")"); return; }

                // 기다리는 동안 바뀌었을 수 있으니 다시 읽어서 같은 움직임인지 확인
                pinned = ShortcutSync.PinnedGroups();
                groupNames.Clear();
                foreach (KeyValuePair<string, string> kv in pinned) groupNames.Add(kv.Key);
                buttons = TaskbarUia.GetButtons();
                string again;
                procs = ProcessRegistry.Snapshot();
                if (!FindNextMove(cfg, pinned, groupNames, buttons, procs, out from, out to, out again)) return;

                Log.Write("이동: '" + again + "' " + from + " → " + to);
                DragButton(buttons[from].Center, DropPoint(buttons[from].Rect, buttons[to].Rect));
                moves++;
                Thread.Sleep(350);
                if (Moved != null) { try { Moved(); } catch { } }

                // 실제로 옮겨졌는지 확인. 안 됐으면 더 시도하지 않는다 (마우스를 계속 끌지 않도록)
                List<TaskbarButton> after = TaskbarUia.GetButtons();
                if (to >= after.Count || !string.Equals(after[to].BaseName, buttons[from].BaseName, StringComparison.Ordinal))
                {
                    Log.Write("이동 실패: 그 자리에 '" + (to < after.Count ? after[to].Name : "?") + "' 이(가) 있음. 정렬 중단");
                    return;
                }
            }
        }

        private static bool OnScreen(Rectangle r)
        {
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                if (s.Bounds.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)) return true;
            return false;
        }

        /// <summary>옮겨야 할 첫 번째 버튼과 놓을 칸을 찾는다.</summary>
        private static bool FindNextMove(Config cfg, List<KeyValuePair<string, string>> pinned,
            HashSet<string> groupNames, List<TaskbarButton> buttons, List<RunningProcess> procs,
            out int from, out int to, out string name)
        {
            from = -1; to = -1; name = null;
            bool[] claimed = new bool[buttons.Count];

            // 작업표시줄에 있는 그룹 버튼 위치
            List<KeyValuePair<int, Group>> placed = new List<KeyValuePair<int, Group>>();
            foreach (Group g in cfg.Groups)
            {
                int gi = FindGroupButton(g.Id, pinned, buttons);
                if (gi < 0) continue;
                claimed[gi] = true;
                placed.Add(new KeyValuePair<int, Group>(gi, g));
            }
            if (placed.Count == 0) return false;

            // 먼저 그룹 아이콘 자체를 설정의 그룹 순서대로 맞춘다 (placed 는 설정 순서).
            // 현재 작업표시줄 위치 순으로 늘어놓고, 처음 어긋난 자리에 와야 할 그룹 아이콘을 그 앞으로 옮긴다.
            List<KeyValuePair<int, Group>> byPos = new List<KeyValuePair<int, Group>>(placed);
            byPos.Sort(delegate (KeyValuePair<int, Group> a, KeyValuePair<int, Group> b) { return a.Key.CompareTo(b.Key); });
            for (int k = 0; k < placed.Count; k++)
            {
                if (placed[k].Value == byPos[k].Value) continue;
                from = placed[k].Key; to = byPos[k].Key;
                name = buttons[from].Name;
                return true;
            }

            if (cfg.ArrangeMode == Config.ModeAfterLastGroup)
            {
                // 그룹 아이콘 사이에는 앱을 두지 않고, 맨 오른쪽 그룹 아이콘 뒤에
                // 작업표시줄의 그룹 순서 → 그룹 안의 항목 순서대로 모은다
                placed.Sort(delegate (KeyValuePair<int, Group> a, KeyValuePair<int, Group> b) { return a.Key.CompareTo(b.Key); });
                List<Entry> all = new List<Entry>();
                foreach (KeyValuePair<int, Group> kv in placed) all.AddRange(kv.Value.Entries);
                int last = placed[placed.Count - 1].Key;
                return PlaceAfter(last, all, buttons, procs, groupNames, claimed, out from, out to, out name);
            }

            // 각 그룹 아이콘 바로 오른쪽 (설정의 그룹 순서대로 처리)
            foreach (KeyValuePair<int, Group> kv in placed)
            {
                if (PlaceAfter(kv.Key, kv.Value.Entries, buttons, procs, groupNames, claimed, out from, out to, out name))
                    return true;
            }
            return false;
        }

        /// <summary>anchor 버튼 바로 뒤부터 entries 순서대로 실행 중인 앱 버튼이 오도록, 옮겨야 할 첫 버튼을 찾는다.</summary>
        private static bool PlaceAfter(int anchor, List<Entry> entries, List<TaskbarButton> buttons,
            List<RunningProcess> procs, HashSet<string> groupNames, bool[] claimed,
            out int from, out int to, out string name)
        {
            from = -1; to = -1; name = null;
            int pos = anchor + 1;
            foreach (Entry e in entries)
            {
                int procCount;
                HashSet<string> names = NamesFor(e, procs, out procCount);
                // 한 항목이 창을 여러 개 띄우면(예: 클리버스 스튜디오 + 하위 도구) 버튼이 같은 이름("Python")으로
                // 여러 개 생기므로, 띄운 프로세스 수만큼 모두 모은다. 이름만 같은 다른 앱까지 끌고 오지 않게 그 수로 제한.
                int limit = Math.Max(1, procCount);
                List<int> found = new List<int>();
                for (int i = 0; i < buttons.Count && found.Count < limit; i++)
                {
                    TaskbarButton b = buttons[i];
                    if (claimed[i] || !b.Running) continue;
                    if (groupNames.Contains(b.BaseName)) continue;
                    if (names.Contains(b.BaseName)) found.Add(i);
                }
                foreach (int i in found) claimed[i] = true;
                foreach (int idx in found)
                {
                    if (idx == pos) { pos++; continue; }

                    // 놓을 칸보다 왼쪽에 있던 버튼을 빼면 그 뒤가 한 칸씩 당겨지므로 놓을 칸도 한 칸 앞
                    int target = idx < pos ? pos - 1 : pos;
                    if (target >= buttons.Count) target = buttons.Count - 1;
                    if (target == idx) { pos++; continue; }
                    from = idx; to = target; name = buttons[idx].Name;
                    return true;
                }
            }
            return false;
        }

        private static int FindGroupButton(string groupId, List<KeyValuePair<string, string>> pinned,
            List<TaskbarButton> buttons)
        {
            foreach (KeyValuePair<string, string> kv in pinned)
            {
                if (kv.Value != groupId) continue;
                for (int i = 0; i < buttons.Count; i++)
                {
                    if (string.Equals(buttons[i].Name, kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(buttons[i].BaseName, kv.Key, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        /// <summary>정적 후보 이름 + 이 항목이 띄운 것으로 보이는 실행 중 프로세스의 이름/창 제목
        /// (bat 로 실행한 python 앱처럼 버튼 이름이 항목과 다른 경우를 위해).</summary>
        public static string EntryKey(Entry e)
        {
            return (e.Target ?? "") + "|" + (e.Arguments ?? "") + "|" + (e.WorkingDirectory ?? "");
        }

        private static ProcessScan.EntryHint HintFor(Entry e)
        {
            string key = EntryKey(e);
            lock (_hintCache)
            {
                ProcessScan.EntryHint hint;
                if (!_hintCache.TryGetValue(key, out hint))
                {
                    hint = ProcessScan.Resolve(e);
                    _hintCache[key] = hint;
                }
                return hint;
            }
        }

        /// <summary>이 프로세스가 이 항목이 띄운 것인지 (기록된 적 있음 → 실행 파일/폴더 판단 → 실행기 폴더 판단).</summary>
        private static bool Launches(Entry e, ProcessScan.EntryHint hint, RunningProcess p, bool allowLauncherFolder)
        {
            // 예전 판단으로 잘못 기록된 것(예: VS Code 폴더 바로가기 ↔ 같은 폴더의 python 앱)도 여기서 걸러진다
            if (!ProcessScan.ExeAllowed(hint, p.ExePath)) return false;
            string key = EntryKey(e);
            if (Knowledge.WasLaunched(key, p)) return true;
            bool hit = ProcessScan.Matches(hint, p) || (allowLauncherFolder && ProcessScan.MatchesLauncherFolder(hint, p));
            // 스크립트/실행기처럼 다른 프로그램을 띄우는 항목은 실제로 띄운 프로세스를 기록해 둔다
            if (hit && (hint.IsScript || !string.Equals(hint.ExePath, p.ExePath, StringComparison.OrdinalIgnoreCase)))
                Knowledge.RecordLaunched(key, p);
            return hit;
        }

        /// <summary>정적 후보 이름 + 이 항목이 띄운 실행 중 프로세스의 이름/창 제목
        /// (bat 로 실행한 python 앱처럼 버튼 이름이 항목과 다른 경우를 위해).</summary>
        private static HashSet<string> NamesFor(Entry e, List<RunningProcess> procs, out int procCount)
        {
            HashSet<string> set = new HashSet<string>(CandidateNames(e), StringComparer.OrdinalIgnoreCase);
            ProcessScan.EntryHint hint = HintFor(e);
            procCount = 0;
            foreach (RunningProcess p in procs)
                if (Launches(e, hint, p, false)) { ProcessScan.AddButtonNames(p, set); procCount++; }
            if (procCount == 0 && !hint.IsScript)
            {
                foreach (RunningProcess p in procs)
                    if (Launches(e, hint, p, true)) { ProcessScan.AddButtonNames(p, set); procCount++; }
            }
            return set;
        }

        /// <summary>새로 뜬 창의 프로세스가 어느 그룹 항목과 관련 있는지 — 저장된 정보만으로 비교한다.
        /// 관련 없으면 정렬을 시도하지 않는다.</summary>
        public static bool IsRelevant(Config cfg, RunningProcess p)
        {
            if (p == null || string.IsNullOrEmpty(p.ExePath)) return false;
            HashSet<string> pnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ProcessScan.AddButtonNames(p, pnames);
            foreach (Group g in cfg.Groups)
            {
                foreach (Entry e in g.Entries)
                {
                    ProcessScan.EntryHint hint = HintFor(e);
                    if (Launches(e, hint, p, true)) return true;
                    if (CandidateNames(e).Overlaps(pnames)) return true;
                }
            }
            return false;
        }

        /// <summary>작업표시줄 버튼에 보일 수 있는 이름들: 항목 이름, 바로가기 이름,
        /// 실행 파일의 설명/제품명/파일 이름.</summary>
        public static HashSet<string> CandidateNames(Entry e)
        {
            string key = (e.Target ?? "") + "|" + (e.Name ?? "");
            lock (_nameCache)
            {
                HashSet<string> cached;
                if (_nameCache.TryGetValue(key, out cached)) return cached;
            }

            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(set, e.Name);
            string exe = e.Target;
            try
            {
                if (!string.IsNullOrEmpty(e.Target) && e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Add(set, Path.GetFileNameWithoutExtension(e.Target));
                    exe = Knowledge.Link(e.Target).Target;
                }
                if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    ExeRec er = Knowledge.Exe(exe);
                    if (er.Exists)
                    {
                        Add(set, Path.GetFileName(exe));
                        Add(set, Path.GetFileNameWithoutExtension(exe));
                        Add(set, er.Description);
                        Add(set, er.Product);

                        // 작업표시줄 버튼 이름은 그 프로그램을 가리키는 시작 메뉴 바로가기 이름이 되기도 한다
                        // (예: KakaoTalk.exe → 시작 메뉴의 "카카오톡"). 시작 메뉴는 기록해 둔 것을 쓴다.
                        List<string> shortcutNames;
                        if (Knowledge.StartMenuNames().TryGetValue(exe, out shortcutNames))
                            foreach (string n in shortcutNames) Add(set, n);
                    }
                }
            }
            catch { }

            lock (_nameCache) { _nameCache[key] = set; }
            return set;
        }

        private static void Add(HashSet<string> set, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Trim();
            if (s.Length > 0) set.Add(s);
        }

        // ---------- 마우스 드래그 흉내 ----------

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);

        /// <summary>놓을 위치: 끄는 버튼은 목표 칸의 가운데를 지나야 자리가 바뀌므로,
        /// 오른쪽으로 옮길 때는 목표 칸의 3/4 지점, 왼쪽으로 옮길 때는 1/4 지점에 놓는다.</summary>
        private static Point DropPoint(Rectangle from, Rectangle to)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                int x = dx > 0 ? to.X + to.Width * 3 / 4 : to.X + to.Width / 4;
                return new Point(x, to.Y + to.Height / 2);
            }
            int y = dy > 0 ? to.Y + to.Height * 3 / 4 : to.Y + to.Height / 4;
            return new Point(to.X + to.Width / 2, y);
        }

        private static IntPtr LParam(POINT p)
        {
            return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        }

        /// <summary>작업표시줄 버튼 목록 창에 "눌러서 끌고 놓기" 메시지를 직접 보낸다.
        /// 실제 마우스 포인터는 움직이지 않으므로 사용자의 마우스를 빼앗지 않는다.</summary>
        private static void DragButton(Point from, Point to)
        {
            IntPtr list = TaskbarUia.TaskListHwnd(TaskbarUia.TrayHwnd());
            if (list == IntPtr.Zero) return;
            POINT a = new POINT(); a.X = from.X; a.Y = from.Y;
            POINT b = new POINT(); b.X = to.X; b.Y = to.Y;
            ScreenToClient(list, ref a);
            ScreenToClient(list, ref b);
            const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
            IntPtr MK_LBUTTON = (IntPtr)1;

            PostMessage(list, WM_MOUSEMOVE, IntPtr.Zero, LParam(a));
            Thread.Sleep(30);
            PostMessage(list, WM_LBUTTONDOWN, MK_LBUTTON, LParam(a));
            Thread.Sleep(60);
            const int steps = 16;
            for (int s = 1; s <= steps; s++)
            {
                POINT m = new POINT();
                m.X = a.X + (b.X - a.X) * s / steps;
                m.Y = a.Y + (b.Y - a.Y) * s / steps;
                PostMessage(list, WM_MOUSEMOVE, MK_LBUTTON, LParam(m));
                Thread.Sleep(16);
            }
            Thread.Sleep(150);
            PostMessage(list, WM_LBUTTONUP, IntPtr.Zero, LParam(b));
            Thread.Sleep(60);
        }
    }
}
