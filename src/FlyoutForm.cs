using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarGroup
{
    public class FlyoutForm : Form
    {
        private Group _group;
        private Color _bg;
        private Color _hover;
        private Color _text;
        private Color _border;
        private Color _accent = Color.FromArgb(0, 120, 215);
        private ToolTip _tip;
        private Panel _dropBar;
        private Point _anchor;
        private bool _hasAnchorRect;
        private int _cols = 1;
        private int _count;

        // Launch 중에는 OnDeactivate 가 창을 닫지 못하게 막는다
        private bool _launching = false;

        // 팝업 바깥 클릭을 감지하는 전역 마우스 훅 (전용 스레드)
        private MouseHook _mouseHook;
        private Rectangle _screenBounds;   // 훅 스레드에서 읽는 창 위치 사본
        private volatile bool _busy;

        private const int CellW = 100;
        private const int CellH = 86;
        private const int Pad = 12;
        private const int MaxCols = 6;
        private const int Radius = 10;

        // WS_EX_NOACTIVATE — 창이 포커스를 가져가지 않으므로 첫 클릭부터 바로 항목에 전달된다
        private const int WS_EX_NOACTIVATE = 0x08000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | 0x80 /* WS_EX_TOOLWINDOW: Alt+Tab 에 안 보이게 */;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>파일을 끌고 온 채로 펼쳐진 상태. 여기에 놓지 않고 드래그가 끝나면 도우미가 닫는다.</summary>
        public bool DragMode { get; set; }

        /// <summary>마우스를 올려서 펼친 상태. 마우스가 벗어나면 도우미가 닫는다.
        /// 이 상태에서 그룹 버튼을 클릭하면 고정(false)된다.</summary>
        public bool HoverMode { get; set; }

        /// <summary>작업표시줄 그룹 버튼 위치 (없으면 Empty).</summary>
        public Rectangle AnchorRect { get; private set; }

        /// <summary>그룹 버튼을 클릭해서 뜬 프로세스가 이미 열린 팝업을 찾을 때 쓰는 창 제목.</summary>
        public static string TitleFor(string groupId)
        {
            return "TaskbarGroupFlyout " + groupId;
        }

        /// <summary>그룹 버튼 클릭 알림 (열려 있는 팝업이 받음): 펼친 상태면 고정, 고정 상태면 닫기.</summary>
        public static readonly int ClickMessage = RegisterWindowMessage("TaskbarGroup.FlyoutClick");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string s);

        public string GroupId
        {
            get { return _group == null ? null : _group.Id; }
        }

        public FlyoutForm(Group g) : this(g, Rectangle.Empty, false) { }

        /// <param name="anchorRect">작업표시줄 그룹 버튼 위치 (비어 있으면 마우스 위치 기준)</param>
        public FlyoutForm(Group g, Rectangle anchorRect, bool dragMode)
        {
            _group = g;
            DragMode = dragMode;
            _hasAnchorRect = !anchorRect.IsEmpty;
            AnchorRect = anchorRect;
            _anchor = _hasAnchorRect
                ? new Point(anchorRect.X + anchorRect.Width / 2, anchorRect.Y + anchorRect.Height / 2)
                : Cursor.Position;

            // 테마와 상관없이 검정 계열 반투명
            _bg = Color.FromArgb(22, 22, 26);
            _hover = Color.FromArgb(62, 62, 70);
            _text = Color.FromArgb(236, 236, 240);
            _border = Color.FromArgb(78, 78, 88);
            this.Opacity = 0.88;

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.KeyPreview = true;
            this.DoubleBuffered = true;
            // 크기가 바뀔 때 전체를 다시 그린다 (안 그러면 이전 크기의 테두리선이 창 안에 잔상으로 남는다)
            this.SetStyle(ControlStyles.ResizeRedraw, true);
            this.BackColor = _bg;
            this.Text = TitleFor(_group == null ? "" : _group.Id);
            this._tip = new ToolTip();
            this._tip.InitialDelay = 350;

            this.AllowDrop = true;
            WireCommon(this);

            Rebuild();
        }

        private static bool IsLightTheme()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) != 0;
                    }
                }
            }
            catch { }
            return true;
        }

        /// <summary>오른쪽 클릭 → 그룹 편집, 파일 끌어 놓기 → 원하는 위치에 추가.</summary>
        private void WireCommon(Control c)
        {
            c.MouseUp += OnAnyMouseUp;
            if (c != this)
            {
                c.AllowDrop = true;
                c.DragEnter += OnAnyDragOver;
                c.DragOver += OnAnyDragOver;
                c.DragDrop += OnAnyDragDrop;
            }
        }

        // ---------- 다른 그룹으로 부드럽게 전환 ----------

        private const int SwitchMs = 160;
        private System.Windows.Forms.Timer _animTimer;
        private Rectangle _animFrom, _animTo;
        private DateTime _animStart;

        /// <summary>창을 닫지 않고 다른 그룹의 내용으로 바꾼다. 내용은 바로 바뀌고, 창은 새 위치·크기로 미끄러지듯 이동한다.</summary>
        public void SwitchTo(Group g, Rectangle anchorRect)
        {
            if (g == null) return;
            Rectangle from = this.Bounds;
            _group = g;
            this.Text = TitleFor(g.Id);
            _hasAnchorRect = !anchorRect.IsEmpty;
            AnchorRect = anchorRect;
            _anchor = _hasAnchorRect
                ? new Point(anchorRect.X + anchorRect.Width / 2, anchorRect.Y + anchorRect.Height / 2)
                : Cursor.Position;

            // 창은 건드리지 않고 내용만 바꾼다 (창 크기·위치를 먼저 바꾸면 엉뚱한 곳에 한 프레임 그려진다)
            Rebuild(false);
            Rectangle to = _targetBounds;
            if (from == to) return;

            _animFrom = from;
            _animTo = to;
            _animStart = DateTime.Now;
            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Interval = 12;
                _animTimer.Tick += OnAnimTick;
            }
            _animTimer.Start();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            double t = (DateTime.Now - _animStart).TotalMilliseconds / SwitchMs;
            if (t >= 1.0) { t = 1.0; _animTimer.Stop(); }
            double k = 1.0 - Math.Pow(1.0 - t, 3);   // 점점 느려지며 도착
            Rectangle r = new Rectangle(
                (int)Math.Round(_animFrom.X + (_animTo.X - _animFrom.X) * k),
                (int)Math.Round(_animFrom.Y + (_animTo.Y - _animFrom.Y) * k),
                (int)Math.Round(_animFrom.Width + (_animTo.Width - _animFrom.Width) * k),
                (int)Math.Round(_animFrom.Height + (_animTo.Height - _animFrom.Height) * k));
            this.Bounds = r;
            ApplyRegion();
            this.Invalidate(true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); _animTimer = null; }
            base.OnFormClosed(e);
            if (_mouseHook != null) { _mouseHook.Dispose(); _mouseHook = null; }
        }

        private Rectangle _targetBounds;

        private void Rebuild()
        {
            Rebuild(true);
        }

        /// <param name="applyBounds">false 면 창 크기·위치·모양은 바꾸지 않고 _targetBounds 에 목표만 계산한다.</param>
        private void Rebuild(bool applyBounds)
        {
            this.SuspendLayout();
            List<Control> old = new List<Control>();
            foreach (Control c in this.Controls) old.Add(c);
            this.Controls.Clear();
            foreach (Control c in old) c.Dispose();

            BuildCells();

            _dropBar = new Panel();
            _dropBar.BackColor = _accent;
            _dropBar.Visible = false;
            _dropBar.Size = new Size(4, CellH - 12);
            this.Controls.Add(_dropBar);
            _dropBar.BringToFront();

            this.ResumeLayout();
            Point loc = PositionFor(_clientSize);
            _targetBounds = new Rectangle(loc, _clientSize);
            if (applyBounds)
            {
                this.ClientSize = _clientSize;
                ApplyRegion();
                this.Location = loc;
            }
            this.Invalidate(true);
        }

        private void BuildCells()
        {
            List<Entry> list = _group == null ? null : _group.Entries;
            int n = list == null ? 0 : list.Count;
            _count = n;
            // 줄 수를 먼저 정하고 줄마다 비슷하게 나눈다 (7개 → 6+1 이 아니라 4+3)
            int rows = n < 1 ? 1 : (n + MaxCols - 1) / MaxCols;
            int cols = n < 1 ? 1 : (n + rows - 1) / rows;
            _cols = cols;

            _clientSize = new Size(cols * CellW + Pad * 2, rows * CellH + Pad * 2);

            if (n == 0)
            {
                Label empty = new Label();
                empty.Text = "항목이 없습니다\n(파일을 끌어다 놓으세요)";
                empty.ForeColor = _text;
                empty.BackColor = _bg;
                empty.TextAlign = ContentAlignment.MiddleCenter;
                empty.SetBounds(Pad, Pad, CellW, CellH);
                WireCommon(empty);
                this.Controls.Add(empty);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                Entry e = list[i];
                int cx = Pad + (i % cols) * CellW;
                int cy = Pad + (i / cols) * CellH;

                Panel cell = new Panel();
                cell.SetBounds(cx, cy, CellW, CellH);
                cell.BackColor = _bg;
                cell.Cursor = Cursors.Hand;

                PictureBox pic = new PictureBox();
                pic.Image = EntryIconCache.Get(e);
                pic.SizeMode = PictureBoxSizeMode.CenterImage;
                pic.SetBounds((CellW - 44) / 2, 8, 44, 44);
                pic.BackColor = Color.Transparent;
                pic.Cursor = Cursors.Hand;

                Label lbl = new Label();
                // 밑줄·점·하이픈 뒤에서 줄바꿈할 수 있게 보이지 않는 구분 문자를 넣는다 (긴 파일 이름이 글자 중간에서 잘리지 않게)
                lbl.Text = e.Name.Replace("_", "_​").Replace(".", ".​").Replace("-", "-​");
                lbl.ForeColor = _text;
                lbl.BackColor = Color.Transparent;
                lbl.TextAlign = ContentAlignment.MiddleCenter;
                lbl.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
                lbl.SetBounds(4, 54, CellW - 8, CellH - 58);
                lbl.Cursor = Cursors.Hand;

                cell.Controls.Add(pic);
                cell.Controls.Add(lbl);

                Panel cellRef = cell;
                EventHandler onEnter = delegate { cellRef.BackColor = _hover; };
                EventHandler onLeave = delegate { cellRef.BackColor = _bg; };
                cell.MouseEnter += onEnter; cell.MouseLeave += onLeave;
                pic.MouseEnter += onEnter; pic.MouseLeave += onLeave;
                lbl.MouseEnter += onEnter; lbl.MouseLeave += onLeave;

                Entry entry = e;
                MouseEventHandler onClick = delegate (object s, MouseEventArgs me)
                {
                    if (me.Button == MouseButtons.Left) Launch(entry);
                };
                cell.MouseClick += onClick; pic.MouseClick += onClick; lbl.MouseClick += onClick;

                WireCommon(cell); WireCommon(pic); WireCommon(lbl);

                string tip = e.Name + "\n" + e.Target + "\n(오른쪽 클릭: 그룹 편집)";
                _tip.SetToolTip(cell, tip);
                _tip.SetToolTip(pic, tip);
                _tip.SetToolTip(lbl, tip);

                this.Controls.Add(cell);
            }
        }

        private void ApplyRegion()
        {
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, this.Width, this.Height), Radius))
            {
                Region old = this.Region;
                this.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private Size _clientSize;

        private Point PositionFor(Size size)
        {
            Point cur = _anchor;
            Screen scr = Screen.FromPoint(cur);
            Rectangle wa = scr.WorkingArea;
            Rectangle ba = scr.Bounds;
            int gap = 8;
            int x, y;

            int w = size.Width, h = size.Height;

            if (wa.Bottom < ba.Bottom)
            {
                x = cur.X - w / 2;
                y = wa.Bottom - h - gap;
            }
            else if (wa.Top > ba.Top)
            {
                x = cur.X - w / 2;
                y = wa.Top + gap;
            }
            else if (wa.Left > ba.Left)
            {
                x = wa.Left + gap;
                y = cur.Y - h / 2;
            }
            else if (wa.Right < ba.Right)
            {
                x = wa.Right - w - gap;
                y = cur.Y - h / 2;
            }
            else
            {
                // 작업표시줄 자동 숨김: 마우스(또는 버튼) 쪽 가장자리 위에 띄운다
                x = cur.X - w / 2;
                y = cur.Y - h - gap * 3;
            }

            if (x < wa.Left + 4) x = wa.Left + 4;
            if (y < wa.Top + 4) y = wa.Top + 4;
            if (x + w > wa.Right - 4) x = wa.Right - 4 - w;
            if (y + h > wa.Bottom - 4) y = wa.Bottom - 4 - h;

            return new Point(x, y);
        }

        // 앱을 먼저 실행한 다음 닫는다 (_launching 동안 OnDeactivate 가 닫지 못함)
        private void Launch(Entry e)
        {
            _launching = true;
            try
            {
                IconHelper.Launch(e);
            }
            catch (Exception ex)
            {
                MessageBox.Show("실행할 수 없습니다.\n" + e.Target + "\n\n" + ex.Message,
                    "몽작업표시줄", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _launching = false;
            }
            this.Close();
        }

        // ---------- 오른쪽 클릭 → 그룹 편집 ----------

        private void OnAnyMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || _group == null) return;
            try
            {
                Process.Start(Application.ExecutablePath, "--edit \"" + _group.Id + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("그룹 편집 화면을 열 수 없습니다.\n" + ex.Message, "몽작업표시줄",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            this.Close();
        }

        // ---------- 끌어 놓기 → 원하는 위치에 추가 ----------

        /// <summary>화면 좌표 → 끼워 넣을 위치 (칸의 왼쪽 절반이면 앞, 오른쪽 절반이면 뒤).</summary>
        private int InsertIndexAt(Point screenPt)
        {
            if (_count == 0) return 0;
            Point p = this.PointToClient(screenPt);
            int col = (p.X - Pad) / CellW;
            int row = (p.Y - Pad) / CellH;
            if (p.X < Pad) col = 0;
            if (p.Y < Pad) row = 0;
            if (col >= _cols) col = _cols - 1;
            int rows = (_count + _cols - 1) / _cols;
            if (row >= rows) return _count;
            int idx = row * _cols + col;
            if (idx >= _count) return _count;
            int cellLeft = Pad + col * CellW;
            if (p.X > cellLeft + CellW / 2) idx++;
            return idx;
        }

        private void ShowDropBar(int idx)
        {
            int col, row;
            if (_count == 0) { col = 0; row = 0; }
            else if (idx >= _count && _count % _cols == 0)
            {
                // 마지막 줄이 꽉 찼으면 그 줄 오른쪽 끝에 표시
                row = (_count - 1) / _cols;
                col = _cols;
            }
            else
            {
                row = idx / _cols;
                col = idx % _cols;
            }
            int x = Pad + col * CellW - 2;
            if (x < 2) x = 2;
            if (x > this.ClientSize.Width - 6) x = this.ClientSize.Width - 6;
            _dropBar.Location = new Point(x, Pad + row * CellH + 6);
            _dropBar.Visible = true;
            _dropBar.BringToFront();
        }

        private static bool HasFiles(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void OnAnyDragOver(object sender, DragEventArgs e)
        {
            if (!HasFiles(e)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            ShowDropBar(InsertIndexAt(new Point(e.X, e.Y)));
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            OnAnyDragOver(this, e);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            OnAnyDragOver(this, e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            // 자식 컨트롤로 넘어갈 때도 불리므로 실제로 창 밖일 때만 숨긴다
            if (_dropBar != null && !this.Bounds.Contains(Cursor.Position)) _dropBar.Visible = false;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            OnAnyDragDrop(this, e);
        }

        private void OnAnyDragDrop(object sender, DragEventArgs e)
        {
            if (_dropBar != null) _dropBar.Visible = false;
            if (!HasFiles(e) || _group == null) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            int idx = InsertIndexAt(new Point(e.X, e.Y));
            string groupId = _group.Id;
            DragMode = false;
            _busy = true;
            this.Cursor = Cursors.WaitCursor;
            // 드롭 콜백 안에서 오래 걸리는 일을 하면 끌어 온 쪽이 멈추므로 백그라운드에서 처리
            Background.RunSta(delegate
            {
                int added;
                Group g = null;
                try { g = GroupStore.InsertEntries(groupId, files, idx, out added); }
                catch { }
                try
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        this.Cursor = Cursors.Default;
                        if (g == null)
                        {
                            MessageBox.Show("추가할 수 있는 파일이 없습니다. (폴더는 넣을 수 없습니다)",
                                "몽작업표시줄", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        _group = g;
                        Rebuild();
                    }));
                }
                catch { }
            });
        }

        // ---------- 바깥 클릭 시 닫기 ----------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _screenBounds = this.Bounds;
            // WS_EX_NOACTIVATE 라서 OnDeactivate 가 오지 않으므로 바깥 클릭은 전역 훅으로 감지한다
            _mouseHook = new MouseHook(OnHook);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            _screenBounds = this.Bounds;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            _screenBounds = this.Bounds;
        }

        // 훅 스레드에서 불린다
        private void OnHook(int msg, int x, int y, bool ours)
        {
            if (ours || _launching || _busy) return;
            if (msg != MouseHook.WM_LBUTTONDOWN && msg != MouseHook.WM_RBUTTONDOWN) return;
            if (_screenBounds.Contains(x, y)) return;
            // 그룹 버튼 자체를 누른 경우는 닫지 않는다 (버튼 클릭 → 고정/닫기는 ClickMessage 로 처리)
            if (_hasAnchorRect && AnchorRect.Contains(x, y)) return;
            try
            {
                this.BeginInvoke(new Action(delegate
                {
                    if (!this.IsDisposed) this.Close();
                }));
            }
            catch { }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_launching) this.Close();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ClickMessage && ClickMessage != 0)
            {
                if (HoverMode) HoverMode = false;
                else this.Close();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) this.Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), Radius))
            using (Pen pen = new Pen(_border))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }
    }
}
