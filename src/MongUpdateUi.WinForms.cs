// 몽 시리즈 공용 자동 업데이트 — WinForms 화면(안내창·진행 창). MongUpdate.cs 와 함께 앱 src 폴더에 복사한다.
// (WPF 앱은 이 파일 대신 MongUpdateUi.Wpf.cs 를 쓴다. 둘 다 클래스 이름이 UpdateUi 이므로 앱에는 하나만 넣는다.)
//
// 쓰는 법 (메인 폼 안):
//   앱 시작 때 한 번:      MongUpdate.UpdateService.CleanupOld();
//   폼이 열린 뒤(Shown):   MongUpdate.UpdateUi.CheckAtStartup(this, "mongexplorer", "몽탐색기", Program.Version, null);
//   도움말 → 업데이트 확인: MongUpdate.UpdateUi.CheckNow(this, "mongexplorer", "몽탐색기", Program.Version, null);
// 새 버전이 있으면 안내창을 띄우고, 사용자가 [예]를 눌러야 받는다. 받은 뒤 앱을 종료하고 새 버전으로 다시 시작한다.
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MongUpdate
{
    public static class UpdateUi
    {
        /// <summary>앱을 실행할 때: 백그라운드로 확인해서 새 버전이 있을 때만 안내한다 (없거나 인터넷 오류면 조용히 넘어간다).</summary>
        public static void CheckAtStartup(Form owner, string appId, string appName, string currentVersion, string restartArgs)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info;
                if (!UpdateService.TryCheck(appId, currentVersion, out info) || info == null) return;
                try { owner.BeginInvoke(new MethodInvoker(delegate { Offer(owner, appName, info, restartArgs); })); } catch { }
            });
        }

        /// <summary>사용자가 메뉴·버튼을 눌렀을 때: 결과(새 버전 없음·오류 포함)를 항상 알려 준다.</summary>
        public static void CheckNow(Form owner, string appId, string appName, string currentVersion, string restartArgs)
        {
            Cursor old = owner.Cursor;
            owner.Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info;
                bool ok = UpdateService.TryCheck(appId, currentVersion, out info);
                string err = UpdateService.LastError;
                try
                {
                    owner.BeginInvoke(new MethodInvoker(delegate
                    {
                        owner.Cursor = old;
                        if (!ok)
                            MessageBox.Show(owner, "업데이트를 확인하지 못했습니다.\n\n" + err, appName + " 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else if (info == null)
                            MessageBox.Show(owner, "현재 최신 버전입니다. (v" + currentVersion + ")", appName + " 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else
                            Offer(owner, appName, info, restartArgs);
                    }));
                }
                catch { }
            });
        }

        static void Offer(Form owner, string appName, UpdateInfo info, string restartArgs)
        {
            string msg = appName + " 새 버전 v" + info.Version + " 이(가) 있습니다.";
            if (!string.IsNullOrEmpty(info.Note)) msg += "\n\n" + info.Note;
            msg += "\n\n지금 업데이트할까요? (끝나면 자동으로 다시 시작합니다)";
            if (MessageBox.Show(owner, msg, appName + " 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            string file = null;
            Exception err = null;
            using (ProgressForm pf = new ProgressForm(appName))
            {
                Thread t = new Thread(delegate ()
                {
                    try { file = UpdateService.Download(info, pf.SetProgress); }
                    catch (Exception ex) { err = ex; }
                    pf.Finish();
                });
                t.IsBackground = true;
                t.Start();
                pf.ShowDialog(owner);
                t.Join();
            }
            if (err != null || file == null)
            {
                MessageBox.Show(owner, "업데이트를 받지 못했습니다.\n\n" + (err != null ? err.Message : "알 수 없는 오류"), appName + " 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                UpdateService.Apply(info, file, restartArgs);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "업데이트를 적용하지 못했습니다.\n\n" + ex.Message, appName + " 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Application.Exit();   // 곧바로 새 버전이 다시 시작된다
        }

        sealed class ProgressForm : Form
        {
            readonly ProgressBar bar = new ProgressBar();
            readonly Label label = new Label();
            volatile bool done;

            public ProgressForm(string appName)
            {
                Text = appName + " 업데이트";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                ControlBox = false;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(360, 84);
                label.Text = "새 버전을 받는 중… 0%";
                label.SetBounds(16, 14, 328, 20);
                bar.SetBounds(16, 44, 328, 20);
                bar.Minimum = 0;
                bar.Maximum = 100;
                Controls.Add(label);
                Controls.Add(bar);
                Shown += delegate { if (done) Close(); };   // 받기가 너무 빨리 끝난 경우
            }

            public void SetProgress(int pct)
            {
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate { bar.Value = pct; label.Text = "새 버전을 받는 중… " + pct + "%"; }));
                }
                catch { }
            }

            public void Finish()
            {
                done = true;
                try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Close)); } catch { }
            }
        }
    }
}
