using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace TaskbarGroup
{
    public class TaskbarButton
    {
        public string Name;
        /// <summary>" - 1개의 실행 중인 창" 같은 꼬리를 뗀 이름.</summary>
        public string BaseName;
        public bool Running;
        public Rectangle Rect;
        /// <summary>즉시 배치 DLL 이 알려 준 앱 ID 와 이 버튼의 창들. 못 받았으면 null.</summary>
        public string AppId;
        public List<IntPtr> Windows;

        public Point Center
        {
            get { return new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2); }
        }
    }

    /// <summary>주 작업표시줄의 앱 버튼 목록을 UI Automation 으로 읽는다.</summary>
    public static class TaskbarUia
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // "이름 - 1개의 실행 중인 창" / "Name - 2 running windows": 마지막 " - " 뒤에 숫자가 있고 '-' 가 없음
        private static readonly Regex RunningSuffix = new Regex("^(.+?) - (?=[^-]*\\d)[^-]*$");

        public static IntPtr TrayHwnd()
        {
            return FindWindow("Shell_TrayWnd", null);
        }

        public static Rectangle TrayRect()
        {
            IntPtr h = TrayHwnd();
            RECT r;
            if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>모든 모니터의 작업표시줄 (주 작업표시줄이 첫 번째).</summary>
        public static List<KeyValuePair<IntPtr, Rectangle>> AllTrays()
        {
            List<KeyValuePair<IntPtr, Rectangle>> list = new List<KeyValuePair<IntPtr, Rectangle>>();
            IntPtr h = TrayHwnd();
            RECT r;
            if (h != IntPtr.Zero && GetWindowRect(h, out r))
                list.Add(new KeyValuePair<IntPtr, Rectangle>(h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
            IntPtr after = IntPtr.Zero;
            while (true)
            {
                after = FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null);
                if (after == IntPtr.Zero) break;
                if (!IsWindowVisible(after) || !GetWindowRect(after, out r)) continue;
                list.Add(new KeyValuePair<IntPtr, Rectangle>(after, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
            }
            return list;
        }

        /// <summary>작업표시줄 창 아래의 앱 버튼 목록 창 (주/보조 작업표시줄 구조가 달라서 자식 전체에서 찾는다).</summary>
        public static IntPtr TaskListHwnd(IntPtr tray)
        {
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            EnumChildWindows(tray, delegate (IntPtr c, IntPtr l)
            {
                sb.Length = 0;
                GetClassName(c, sb, sb.Capacity);
                if (sb.ToString() == "MSTaskListWClass") { found = c; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private static string ClassOf(IntPtr h)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Windows 작업표시줄 미리보기(실행 중인 앱 위에 마우스를 올리면 뜨는 창)가 떠 있는지.</summary>
        public static bool ThumbnailVisible()
        {
            bool found = false;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (IsWindowVisible(h) && ClassOf(h) == "TaskListThumbnailWnd") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static int ExplorerPid()
        {
            int pid;
            GetWindowThreadProcessId(TrayHwnd(), out pid);
            return pid;
        }

        /// <summary>창이 작업표시줄 위쪽(툴팁·미리보기가 뜨는 자리)에 있는지.</summary>
        public static bool NearTaskbar(Rectangle r)
        {
            foreach (KeyValuePair<IntPtr, Rectangle> t in AllTrays())
            {
                Rectangle zone = t.Value;
                zone.Inflate(0, 120);   // 작업표시줄 위아래로 툴팁이 뜨는 여유
                if (zone.IntersectsWith(r)) return true;
            }
            return false;
        }

        /// <summary>작업표시줄 위에 뜬 탐색기 이름 툴팁을 닫는다 — 그룹 목록과 겹치지 않게.
        /// 바깥에서 강제로 숨기면 그림자 창이 남으므로, 툴팁 스스로 닫는 TTM_POP 메시지를 보낸다.
        /// 바탕화면 아이콘 툴팁은 건드리지 않는다.</summary>
        public static void PopTaskbarTooltips()
        {
            int explorer = ExplorerPid();
            if (explorer == 0) return;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || ClassOf(h) != "tooltips_class32") return true;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != explorer) return true;
                RECT r;
                if (GetWindowRect(h, out r) && NearTaskbar(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)))
                    PostMessage(h, 0x041C /* TTM_POP */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>주인 잃은 툴팁 그림자(SysShadow) 정리: 탐색기의 보이는 그림자 창 중
        /// 겹치는 곳에 보이는 탐색기 창(툴팁·메뉴 등)이 없는 것만 숨긴다.</summary>
        public static int CleanupOrphanShadows()
        {
            int explorer = ExplorerPid();
            if (explorer == 0) return 0;
            List<KeyValuePair<IntPtr, Rectangle>> shadows = new List<KeyValuePair<IntPtr, Rectangle>>();
            List<Rectangle> others = new List<Rectangle>();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != explorer) return true;
                string cls = ClassOf(h);
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                Rectangle rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (cls == "SysShadow") shadows.Add(new KeyValuePair<IntPtr, Rectangle>(h, rc));
                else if (cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd")
                    others.Add(rc);
                return true;
            }, IntPtr.Zero);
            int n = 0;
            foreach (KeyValuePair<IntPtr, Rectangle> sh in shadows)
            {
                Rectangle near = sh.Value;
                near.Inflate(12, 12);
                bool owned = false;
                foreach (Rectangle o in others)
                    if (o.Width < 2000 && near.IntersectsWith(o)) { owned = true; break; }
                if (!owned) { ShowWindow(sh.Key, 0 /* SW_HIDE */); n++; }
            }
            return n;
        }

        /// <summary>그 위치의 모니터를 앞쪽 창이 꽉 채우고 있는지 (전체 화면 동영상·게임 등).</summary>
        public static bool FullscreenOn(Point pt)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            Rectangle b = System.Windows.Forms.Screen.FromPoint(pt).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        public static string ParseBaseName(string name, out bool running)
        {
            running = false;
            if (string.IsNullOrEmpty(name)) return "";
            Match m = RunningSuffix.Match(name);
            if (m.Success)
            {
                running = true;
                return m.Groups[1].Value.Trim();
            }
            return name.Trim();
        }

        public static List<TaskbarButton> GetButtons()
        {
            return GetButtons(TrayHwnd());
        }

        // 여러 스레드가 동시에 탐색기에 물으면 빈 결과가 오기도 해서, 작업표시줄 읽기는 한 번에 하나씩
        private static readonly object _uiaLock = new object();

        public static List<TaskbarButton> GetButtons(IntPtr tray)
        {
            lock (_uiaLock) { return GetButtonsCore(tray); }
        }

        private static List<TaskbarButton> GetButtonsCore(IntPtr tray)
        {
            List<TaskbarButton> list = new List<TaskbarButton>();
            IntPtr h = TaskListHwnd(tray);
            if (h == IntPtr.Zero) return list;
            AutomationElement root = AutomationElement.FromHandle(h);
            TreeWalker w = TreeWalker.RawViewWalker;
            AutomationElement k = w.GetFirstChild(root);
            while (k != null)
            {
                try
                {
                    AutomationElement.AutomationElementInformation c = k.Current;
                    if (c.ControlType == ControlType.Button || c.ControlType == ControlType.MenuItem)
                    {
                        System.Windows.Rect r = c.BoundingRectangle;
                        if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                        {
                            TaskbarButton b = new TaskbarButton();
                            b.Name = c.Name ?? "";
                            b.BaseName = ParseBaseName(b.Name, out b.Running);
                            b.Rect = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                            list.Add(b);
                        }
                    }
                }
                catch { }
                k = w.GetNextSibling(k);
            }
            return list;
        }
    }
}
