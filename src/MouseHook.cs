using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarGroup
{
    /// <summary>전역 저수준 마우스 훅을 전용 스레드에서 돌린다.
    /// UI 스레드가 잠깐 바빠도(아이콘 생성, 느린 드라이브 등) 시스템 마우스가 버벅이지 않게 하기 위함.
    /// 콜백은 훅 스레드에서 불리므로 빨리 끝내고 UI 작업은 BeginInvoke 로 넘겨야 한다.</summary>
    public sealed class MouseHook : IDisposable
    {
        public delegate void Handler(int msg, int x, int y, bool injectedByUs);

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX, ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX;
            public int ptY;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extra;
        }

        private readonly Handler _handler;
        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private uint _threadId;
        private int _disposed;

        public MouseHook(Handler handler)
        {
            _handler = handler;
            _proc = Callback;
            ManualResetEvent ready = new ManualResetEvent(false);
            Thread t = new Thread(delegate ()
            {
                _threadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            });
            t.IsBackground = true;
            t.Name = "MouseHook";
            t.Start();
            ready.WaitOne(3000);
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _disposed == 0)
            {
                try
                {
                    MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    bool ours = (m.flags & 0x01) != 0 && m.extra == Arranger.InjectTag;
                    _handler(wParam.ToInt32(), m.ptX, m.ptY, ours);
                }
                catch { }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static class Background
    {
        /// <summary>셸 COM(바로가기·아이콘)을 쓰는 작업을 STA 백그라운드 스레드에서 실행.</summary>
        public static void RunSta(Action work)
        {
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}

namespace TaskbarGroup
{
    /// <summary>도우미 동작 기록 (실행 파일 폴더의 agent.log, 256KB 넘으면 새로 시작).</summary>
    public static class Log
    {
        private static readonly object _lock = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                {
                    string path = System.IO.Path.Combine(GroupStore.AppDir, "agent.log");
                    System.IO.FileInfo fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                    System.IO.File.AppendAllText(path,
                        DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
