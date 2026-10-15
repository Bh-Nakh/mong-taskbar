using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 실행 파일의 "설명" — 작업표시줄에 관리 화면 버튼 이름으로 보인다
[assembly: AssemblyTitle("몽작업표시줄 관리")]
[assembly: AssemblyProduct("몽작업표시줄")]
// 버전은 Version.cs 에서 관리한다

namespace TaskbarGroup
{
    internal static class Program
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref ConfiguratorForm.COPYDATASTRUCT l);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [STAThread]
        private static void Main(string[] args)
        {
            // VS Code 같은 Electron 앱의 터미널에서 실행되면 ELECTRON_RUN_AS_NODE=1 이 물려 오는데,
            // 이 값이 남아 있으면 여기서 실행하는 Electron 앱(GitHub Desktop, Claude 등)이 창 없이 바로 꺼진다.
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);

            string groupId = null;
            string editId = null;
            string makeIcon = null;
            bool agent = false;
            bool refresh = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--group" && i + 1 < args.Length) { groupId = args[++i]; }
                else if (args[i] == "--edit" && i + 1 < args.Length) { editId = args[++i]; }
                else if (args[i] == "--make-app-icon" && i + 1 < args.Length) { makeIcon = args[++i]; }
                else if (args[i] == "--agent") { agent = true; }
                else if (args[i] == "--refresh") { refresh = true; }
            }

            // 빌드 때 앱 아이콘(.ico) 생성용
            if (makeIcon != null)
            {
                GroupIcon.SaveAppIcon(makeIcon);
                return;
            }

            // 아이콘·앱 정보 다시 읽기 (관리 화면 버튼과 같음)
            if (refresh)
            {
                ShortcutSync.RefreshEverything(GroupStore.Load());
                return;
            }

            if (agent)
            {
                if (!AgentContext.TryAcquire()) return;   // 이미 실행 중
                // 작업표시줄 버튼 좌표와 마우스 좌표를 같은 기준(실제 픽셀)으로 맞춘다
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 홈페이지(설치 확인 페이지)가 이 앱이 켜져 있는지 확인할 수 있게 하는 로컬 핑 서버
                MongWebCheck.WebCheck.Start("mongtaskbar", "몽작업표시줄", new int[] { 47161, 47162, 47163 }, AppVersion.Number);
                Application.Run(new AgentContext());
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Config cfg = GroupStore.Load();

            if (!string.IsNullOrEmpty(groupId))
            {
                // 고정된 그룹 바로가기와 같은 ID → 이 프로세스의 창이 다른 그룹 버튼에 붙지 않는다
                try { SetCurrentProcessExplicitAppUserModelID(ShortcutSync.GroupAppIdPrefix + groupId); } catch { }
                AgentContext.EnsureRunning(cfg);

                // 마우스를 올려서 이미 펼쳐진 팝업이 있으면 새로 띄우지 않고 그 팝업에 클릭을 알린다
                IntPtr open = FindWindow(null, FlyoutForm.TitleFor(groupId));
                if (open != IntPtr.Zero && IsWindowVisible(open))
                {
                    PostMessage(open, FlyoutForm.ClickMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Group g = GroupStore.FindGroup(cfg, groupId);
                if (g == null)
                {
                    MessageBox.Show("그룹을 찾을 수 없습니다: " + groupId + "\n\n관리 화면을 엽니다.",
                        "몽작업표시줄", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    RunConfigurator(null);
                    return;
                }
                Application.Run(new FlyoutForm(g));
                return;
            }

            AgentContext.EnsureRunning(cfg);
            RunConfigurator(editId);
        }

        /// <summary>관리 화면은 하나만: 이미 떠 있으면 그 창에 그룹 선택을 넘기고 앞으로 가져온다.</summary>
        private static void RunConfigurator(string selectId)
        {
            // 관리 화면은 별도 앱으로 — 고정된 그룹 아이콘(같은 실행 파일)에 묶이지 않게
            try { SetCurrentProcessExplicitAppUserModelID(ShortcutSync.ManagerAppId); } catch { }
            IntPtr existing = FindWindow(null, ConfiguratorForm.WindowTitle);
            if (existing != IntPtr.Zero)
            {
                string payload = selectId ?? "";
                IntPtr mem = Marshal.StringToHGlobalUni(payload);
                try
                {
                    ConfiguratorForm.COPYDATASTRUCT cds = new ConfiguratorForm.COPYDATASTRUCT();
                    cds.dwData = IntPtr.Zero;
                    cds.cbData = payload.Length * 2;
                    cds.lpData = mem;
                    SendMessage(existing, 0x004A /* WM_COPYDATA */, IntPtr.Zero, ref cds);
                }
                finally { Marshal.FreeHGlobal(mem); }
                if (IsIconic(existing)) ShowWindow(existing, 9 /* SW_RESTORE */);
                SetForegroundWindow(existing);
                return;
            }

            try
            {
                Application.Run(new ConfiguratorForm(selectId));
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show("설정 파일을 저장할 수 없습니다. 실행 파일 폴더에 쓰기 권한이 있는지 확인하세요.\n\n" +
                    GroupStore.ConfigPath + "\n" + ex.Message, "몽작업표시줄",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
