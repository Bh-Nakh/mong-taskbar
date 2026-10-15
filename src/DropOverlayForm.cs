using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarGroup
{
    /// <summary>파일을 끌고 작업표시줄의 그룹 아이콘 위에 오면 그 버튼 위에 잠깐 덮이는 반투명 창.
    /// 작업표시줄 버튼은 우리가 드롭을 받을 수 없으므로 이 창이 대신 드롭을 받는다.</summary>
    public class DropOverlayForm : Form
    {
        public string GroupId { get; private set; }
        public Rectangle ButtonRect { get; private set; }

        /// <summary>끌고 온 채로 잠깐 머무름 → 그룹 펼치기.</summary>
        public event EventHandler HoverExpand;
        /// <summary>아이콘 위에 바로 놓음 → 그룹 맨 끝에 추가.</summary>
        public event Action<string[]> FilesDropped;

        private readonly Timer _hoverTimer = new Timer();
        private bool _dragInside;

        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public DropOverlayForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.FromArgb(0, 120, 215);
            this.Opacity = 0.35;
            this.AllowDrop = true;
            this.DoubleBuffered = true;

            _hoverTimer.Interval = 600;
            _hoverTimer.Tick += delegate
            {
                _hoverTimer.Stop();
                if (_dragInside && HoverExpand != null) HoverExpand(this, EventArgs.Empty);
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ShowAt(string groupId, Rectangle rect)
        {
            bool sameTarget = this.Visible && GroupId == groupId && ButtonRect == rect;
            GroupId = groupId;
            ButtonRect = rect;
            if (sameTarget) return;
            _dragInside = false;
            _hoverTimer.Stop();
            this.Opacity = 0.35;
            this.Bounds = rect;
            if (!this.Visible) this.Show();
            SetWindowPos(this.Handle, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            this.Invalidate();
        }

        public void HideOverlay()
        {
            _hoverTimer.Stop();
            _dragInside = false;
            if (this.Visible) this.Hide();
            GroupId = null;
        }

        private static bool HasFiles(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (!HasFiles(e)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            _dragInside = true;
            this.Opacity = 0.6;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _dragInside = false;
            _hoverTimer.Stop();
            this.Opacity = 0.35;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            _hoverTimer.Stop();
            _dragInside = false;
            string[] files = HasFiles(e) ? (string[])e.Data.GetData(DataFormats.FileDrop) : null;
            if (files != null && files.Length > 0 && FilesDropped != null) FilesDropped(files);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = Math.Min(this.ClientSize.Width, this.ClientSize.Height) / 2;
            int cx = this.ClientSize.Width / 2, cy = this.ClientSize.Height / 2;
            using (Pen p = new Pen(Color.White, Math.Max(2f, s / 6f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - s / 2, cy, cx + s / 2, cy);
                g.DrawLine(p, cx, cy - s / 2, cx, cy + s / 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hoverTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
