using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarGroup
{
    public class ConfiguratorForm : Form
    {
        private Config _config;

        private GroupListBox _lstGroups;
        private EntryListView _lvEntries;
        private PictureBox _picGroupIcon;
        private Label _lblGroupInfo;
        private ToolStripStatusLabel _lblStatus;
        private Label _lblGroupName;
        private TextBox _txtShortcutFolder;
        private ToolTip _toolTip = new ToolTip();

        private int _dropIndex = -1;
        private string _initialGroupId;
        private CheckBox _chkAgent;
        private CheckBox _chkArrange;
        private CheckBox _chkInstant;
        private ComboBox _cmbArrangeMode;
        private FileSystemWatcher _watcher;
        private Timer _reloadTimer;

        public const string WindowTitle = "몽작업표시줄 관리";
        private const int WM_COPYDATA = 0x004A;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        private const int WM_VSCROLL = 0x0115;
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public ConfiguratorForm() : this(null) { }

        public ConfiguratorForm(string selectGroupId)
        {
            _config = GroupStore.Load();
            _initialGroupId = selectGroupId;

            this.Text = WindowTitle;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(940, 700);
            this.MinimumSize = new Size(820, 640);
            this.Font = new Font("Segoe UI", 9f);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            RefreshGroups(selectGroupId);
            WatchConfig();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_initialGroupId != null) _lstGroups.Focus();
            // 바로가기 경로/아이콘 정리는 화면이 뜬 다음에 (아이콘 생성에 잠깐 걸릴 수 있음)
            Config cfg = _config;
            AgentContext.EnsureRunning(cfg);
            Background.RunSta(delegate
            {
                string err = null;
                try { ShortcutSync.SyncAll(cfg); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        if (err != null) Status("바로가기 정리 실패: " + err);
                        else RefreshEntries();
                    }));
                }
                catch { }
            });
        }

        // 다른 프로세스(그룹 팝업에서 오른쪽 클릭)가 특정 그룹을 열어 달라고 보낸 경우
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                try
                {
                    COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                    string id = cds.cbData > 0 ? Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) : "";
                    if (this.WindowState == FormWindowState.Minimized) this.WindowState = FormWindowState.Normal;
                    this.Activate();
                    if (id.Length > 0) RefreshGroups(id);
                }
                catch { }
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // 도우미가 드롭으로 항목을 추가하는 등 밖에서 설정 파일이 바뀌면 다시 읽는다
        private void WatchConfig()
        {
            _reloadTimer = new Timer();
            _reloadTimer.Interval = 400;
            _reloadTimer.Tick += delegate
            {
                _reloadTimer.Stop();
                string json;
                try { json = File.ReadAllText(GroupStore.ConfigPath, System.Text.Encoding.UTF8); }
                catch { return; }
                if (json == GroupStore.LastJson) return;
                Group sel = SelectedGroup;
                string selId = sel == null ? null : sel.Id;
                _config = GroupStore.Load();
                RefreshGroups(selId);
                UpdateShortcutFolderUi();
                Status("설정 파일이 바뀌어 다시 읽었습니다.");
            };
            try
            {
                _watcher = new FileSystemWatcher(GroupStore.AppDir, "groups.json");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                _watcher.SynchronizingObject = this;
                FileSystemEventHandler h = delegate { _reloadTimer.Stop(); _reloadTimer.Start(); };
                _watcher.Changed += h;
                _watcher.Created += h;
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }

        /// <summary>그룹 내용이 바뀐 뒤: 저장 + 그룹 아이콘/바로가기 갱신.</summary>
        private void Commit(Group g)
        {
            GroupStore.Save(_config);
            if (g == null) return;
            try { ShortcutSync.SyncGroup(_config, g); }
            catch (Exception ex) { Status("바로가기 갱신 실패: " + ex.Message); }
        }

        private static readonly Color Accent = Color.FromArgb(0, 103, 192);

        private void BuildUi()
        {
            this.SuspendLayout();

            // 아래쪽 상태 표시줄
            StatusStrip strip = new StatusStrip();
            strip.SizingGrip = true;
            _lblStatus = new ToolStripStatusLabel();
            _lblStatus.Spring = true;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "설정 파일: " + GroupStore.ConfigPath;
            strip.Items.Add(_lblStatus);
            ToolStripStatusLabel lblVer = new ToolStripStatusLabel();
            lblVer.Text = AppVersion.Display;
            lblVer.ForeColor = Color.DimGray;
            lblVer.ToolTipText = "몽작업표시줄 " + AppVersion.Display + " — 클릭하면 업데이트를 확인합니다";
            lblVer.Click += delegate { MongUpdate.UpdateUi.CheckNow(this, "mongtaskbar", "몽작업표시줄", AppVersion.Number, null); };
            strip.ShowItemToolTips = true;
            strip.Items.Add(lblVer);
            this.Controls.Add(strip);

            // 본문: 왼쪽(그룹) | 오른쪽(항목·설정)
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 1;
            root.Padding = new Padding(12, 12, 12, 8);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            this.Controls.Add(root);
            root.BringToFront();

            root.Controls.Add(BuildLeftPane(), 0, 0);
            root.Controls.Add(BuildRightPane(), 1, 0);

            UpdateShortcutFolderUi();

            this.FormClosing += delegate
            {
                if (_watcher != null) _watcher.EnableRaisingEvents = false;
                GroupStore.Save(_config);
            };
            this.ResumeLayout(true);
        }

        // ---------- 왼쪽: 그룹 목록 ----------

        private Control BuildLeftPane()
        {
            TableLayoutPanel t = NewColumn();
            t.Margin = new Padding(0, 0, 12, 0);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label lbl = SectionLabel("그룹");
            t.Controls.Add(lbl, 0, 0);

            _lstGroups = new GroupListBox();
            _lstGroups.Dock = DockStyle.Fill;
            _lstGroups.Margin = new Padding(0, 0, 0, 8);
            _lstGroups.SelectedIndexChanged += delegate { RefreshEntries(); };
            _lstGroups.DoubleClick += OnGroupRename;
            _lstGroups.GroupMoved += OnGroupMoved;
            t.Controls.Add(_lstGroups, 0, 1);
            _toolTip.SetToolTip(_lstGroups, "그룹을 마우스로 끌어서 순서를 바꿀 수 있습니다. (더블클릭: 이름 변경)");

            // 추가 / 이름변경 / 삭제 + ▲ ▼
            TableLayoutPanel g1 = NewButtonGrid(3);
            g1.Controls.Add(Btn("추가", OnGroupAdd), 0, 0);
            g1.Controls.Add(Btn("이름변경", OnGroupRename), 1, 0);
            g1.Controls.Add(Btn("삭제", OnGroupDelete), 2, 0);
            FillButtons(g1);
            t.Controls.Add(g1, 0, 2);

            TableLayoutPanel g2 = NewButtonGrid(2);
            Button up = Btn("▲ 위로", OnGroupUp);
            Button down = Btn("▼ 아래로", OnGroupDown);
            g2.Controls.Add(up, 0, 0);
            g2.Controls.Add(down, 1, 0);
            _toolTip.SetToolTip(up, "선택한 그룹을 위로 옮깁니다. (목록에서 끌어도 됩니다)");
            _toolTip.SetToolTip(down, "선택한 그룹을 아래로 옮깁니다. (목록에서 끌어도 됩니다)");
            FillButtons(g2);
            t.Controls.Add(g2, 0, 3);
            return t;
        }

        // ---------- 오른쪽: 선택한 그룹의 항목과 설정 ----------

        private Control BuildRightPane()
        {
            TableLayoutPanel t = NewColumn();
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 머리글
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 항목 목록
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 항목 버튼
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 작업표시줄 바로가기
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 도우미

            // 머리글: 아이콘 | 이름·개수 | 아이콘 변경 | 기본값
            TableLayoutPanel head = new TableLayoutPanel();
            head.Dock = DockStyle.Fill;
            head.AutoSize = true;
            head.ColumnCount = 4;
            head.RowCount = 1;
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.Margin = new Padding(0, 0, 0, 8);

            _picGroupIcon = new PictureBox();
            _picGroupIcon.Size = new Size(44, 44);
            _picGroupIcon.SizeMode = PictureBoxSizeMode.CenterImage;
            _picGroupIcon.Margin = new Padding(0, 0, 10, 0);
            head.Controls.Add(_picGroupIcon, 0, 0);

            TableLayoutPanel names = NewColumn();
            names.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            names.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            names.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _lblGroupName = new Label();
            _lblGroupName.AutoSize = true;
            _lblGroupName.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            _lblGroupName.Margin = new Padding(0);
            _lblGroupInfo = new Label();
            _lblGroupInfo.AutoSize = true;
            _lblGroupInfo.ForeColor = Color.DimGray;
            _lblGroupInfo.Margin = new Padding(1, 0, 0, 0);
            names.Controls.Add(_lblGroupName, 0, 0);
            names.Controls.Add(_lblGroupInfo, 0, 1);
            head.Controls.Add(names, 1, 0);

            Button btnIcon = Btn("그룹 아이콘 변경…", OnGroupIcon);
            btnIcon.Anchor = AnchorStyles.Right;
            Button btnIconReset = Btn("기본값", OnGroupIconReset);
            btnIconReset.Anchor = AnchorStyles.Right;
            head.Controls.Add(btnIcon, 2, 0);
            head.Controls.Add(btnIconReset, 3, 0);
            t.Controls.Add(head, 0, 0);

            // 항목 목록
            _lvEntries = new EntryListView();
            _lvEntries.Dock = DockStyle.Fill;
            _lvEntries.Margin = new Padding(0, 0, 0, 8);
            _lvEntries.View = View.Details;
            _lvEntries.FullRowSelect = true;
            _lvEntries.HideSelection = false;
            _lvEntries.MultiSelect = false;
            _lvEntries.GridLines = false;
            _lvEntries.BorderStyle = BorderStyle.FixedSingle;
            ImageList rowHeight = new ImageList();      // 행을 조금 높게 (그림은 없음)
            rowHeight.ImageSize = new Size(1, 26);
            _lvEntries.SmallImageList = rowHeight;
            _lvEntries.Columns.Add("표시 이름", 200);
            _lvEntries.Columns.Add("대상 (실행 파일/바로가기)", 380);
            _lvEntries.Resize += delegate { FitEntryColumns(); };
            _lvEntries.DoubleClick += OnEntryRun;
            _lvEntries.AllowDrop = true;
            _lvEntries.ItemDrag += OnEntriesItemDrag;
            _lvEntries.DragEnter += OnEntriesDragEnter;
            _lvEntries.DragOver += OnEntriesDragOver;
            _lvEntries.DragLeave += delegate { ClearDropMark(); };
            _lvEntries.DragDrop += OnEntriesDragDrop;
            t.Controls.Add(_lvEntries, 0, 1);

            // 항목 버튼
            FlowLayoutPanel eb = NewFlow();
            eb.Controls.Add(Btn("항목 추가", OnEntryAdd));
            eb.Controls.Add(Btn("삭제", OnEntryDelete));
            eb.Controls.Add(Btn("▲ 위로", OnEntryUp));
            eb.Controls.Add(Btn("▼ 아래로", OnEntryDown));
            eb.Controls.Add(Btn("이름변경", OnEntryRename));
            eb.Controls.Add(Btn("실행", OnEntryRun));
            eb.Margin = new Padding(0, 0, 0, 8);
            t.Controls.Add(eb, 0, 2);

            // 작업표시줄 바로가기
            GroupBox gbTask = NewGroup("작업표시줄 바로가기");
            TableLayoutPanel tt = new TableLayoutPanel();
            tt.Dock = DockStyle.Fill;
            tt.AutoSize = true;
            tt.ColumnCount = 4;
            tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label lblFolder = new Label();
            lblFolder.Text = "저장 폴더";
            lblFolder.AutoSize = true;
            lblFolder.Anchor = AnchorStyles.Left;
            lblFolder.Margin = new Padding(0, 0, 8, 0);
            _txtShortcutFolder = new TextBox();
            _txtShortcutFolder.ReadOnly = true;
            _txtShortcutFolder.BackColor = SystemColors.Window;
            _txtShortcutFolder.Dock = DockStyle.Fill;
            _txtShortcutFolder.Margin = new Padding(0, 4, 6, 4);
            tt.Controls.Add(lblFolder, 0, 0);
            tt.Controls.Add(_txtShortcutFolder, 1, 0);
            tt.Controls.Add(Btn("변경", OnChangeShortcutFolder), 2, 0);
            tt.Controls.Add(Btn("열기", OnOpenShortcutFolder), 3, 0);

            FlowLayoutPanel ab = NewFlow();
            Button btnCreate = Btn("작업표시줄 바로가기 만들기", OnCreateShortcut);
            btnCreate.BackColor = Accent;
            btnCreate.ForeColor = Color.White;
            btnCreate.FlatStyle = FlatStyle.Flat;
            btnCreate.FlatAppearance.BorderSize = 0;
            btnCreate.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            btnCreate.MinimumSize = new Size(0, 34);
            ab.Controls.Add(btnCreate);
            ab.Controls.Add(Btn("미리보기", OnPreview));
            ab.Controls.Add(Btn("저장", OnSave));
            Button btnRefreshIcons = Btn("아이콘 다시 읽기", OnRefreshIcons);
            _toolTip.SetToolTip(btnRefreshIcons,
                "아이콘이 깨졌을 때: 저장해 둔 앱 정보·아이콘을 비우고 다시 읽은 뒤,\n" +
                "그룹 아이콘을 새 파일로 만들어 작업표시줄에 다시 적용합니다. (탐색기는 재시작하지 않음)");
            Button btnRefreshTaskbar = Btn("탐색기 재시작", OnRefreshTaskbar);
            _toolTip.SetToolTip(btnRefreshTaskbar,
                "탐색기를 다시 시작해서 작업표시줄을 새로 그립니다.\n(열려 있는 탐색기 창은 닫힙니다)");
            Button btnOpenFolder = Btn("설정파일 폴더", OnOpenConfigFolder);
            ab.Controls.Add(btnRefreshIcons);
            ab.Controls.Add(btnRefreshTaskbar);
            ab.Controls.Add(btnOpenFolder);
            tt.Controls.Add(ab, 0, 1);
            tt.SetColumnSpan(ab, 4);
            gbTask.Controls.Add(tt);
            t.Controls.Add(gbTask, 0, 3);

            // 백그라운드 도우미
            GroupBox gbAgent = NewGroup("백그라운드 도우미");
            TableLayoutPanel ta = new TableLayoutPanel();
            ta.Dock = DockStyle.Fill;
            ta.AutoSize = true;
            ta.ColumnCount = 2;
            ta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _chkAgent = new CheckBox();
            _chkAgent.Text = "도우미 사용 (Windows 시작 시 실행 · 작업표시줄 그룹 아이콘에 파일 끌어 놓기)";
            _chkAgent.AutoSize = true;
            _chkAgent.Checked = _config.AutoStartAgent;
            _chkAgent.CheckedChanged += OnAgentOptionChanged;
            ta.Controls.Add(_chkAgent, 0, 0);
            ta.SetColumnSpan(_chkAgent, 2);

            _chkArrange = new CheckBox();
            _chkArrange.Text = "그룹 앱이 실행되면 작업표시줄 버튼 자동 정렬 :";
            _chkArrange.AutoSize = true;
            _chkArrange.Checked = _config.ArrangeRunningApps;
            _chkArrange.CheckedChanged += OnAgentOptionChanged;
            _chkArrange.Anchor = AnchorStyles.Left;
            _toolTip.SetToolTip(_chkArrange,
                "도우미가 작업표시줄 버튼을 그룹 순서에 맞게 옮깁니다 (마우스 포인터는 움직이지 않습니다).");
            ta.Controls.Add(_chkArrange, 0, 1);

            _cmbArrangeMode = new ComboBox();
            _cmbArrangeMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbArrangeMode.Items.Add("각 그룹 아이콘 바로 오른쪽에");
            _cmbArrangeMode.Items.Add("맨 오른쪽 그룹 아이콘 뒤에 모아서 (그룹 아이콘끼리는 붙여 둠)");
            _cmbArrangeMode.Dock = DockStyle.Fill;
            _cmbArrangeMode.SelectedIndex = _config.ArrangeMode == Config.ModeAfterLastGroup ? 1 : 0;
            _cmbArrangeMode.Enabled = _config.ArrangeRunningApps;
            _cmbArrangeMode.SelectedIndexChanged += OnAgentOptionChanged;
            ta.Controls.Add(_cmbArrangeMode, 1, 1);

            _chkInstant = new CheckBox();
            _chkInstant.Text = "앱 버튼이 뜰 때 바로 제자리에 배치 (고급: 작업표시줄 프로세스에 보조 DLL 적용)";
            _chkInstant.AutoSize = true;
            _chkInstant.Checked = _config.InstantPlacement;
            _chkInstant.Enabled = Placement.Available;
            _chkInstant.CheckedChanged += OnAgentOptionChanged;
            _toolTip.SetToolTip(_chkInstant,
                "끄면 앱이 뜬 뒤에 도우미가 버튼을 옮깁니다. hook\\placer.dll 이 없으면 사용할 수 없습니다.");
            ta.Controls.Add(_chkInstant, 0, 2);
            ta.SetColumnSpan(_chkInstant, 2);
            gbAgent.Controls.Add(ta);
            t.Controls.Add(gbAgent, 0, 4);
            return t;
        }

        // ---------- UI 조립 도우미 ----------

        private static TableLayoutPanel NewColumn()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Padding = new Padding(0);
            return t;
        }

        private static TableLayoutPanel NewButtonGrid(int cols)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.AutoSize = true;
            t.ColumnCount = cols;
            t.RowCount = 1;
            for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            t.Margin = new Padding(0, 0, 0, 4);
            return t;
        }

        private static FlowLayoutPanel NewFlow()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Dock = DockStyle.Fill;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0);
            return f;
        }

        private static GroupBox NewGroup(string title)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Dock = DockStyle.Fill;
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.Padding = new Padding(10, 6, 10, 8);
            g.Margin = new Padding(0, 0, 0, 8);
            return g;
        }

        private static Label SectionLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            l.Margin = new Padding(0, 0, 0, 6);
            return l;
        }

        private Button Btn(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, 30);
            b.Padding = new Padding(8, 0, 8, 0);
            b.Margin = new Padding(0, 0, 6, 4);
            b.UseVisualStyleBackColor = true;
            b.Click += onClick;
            return b;
        }

        private static void FillButtons(TableLayoutPanel t)
        {
            foreach (Control c in t.Controls)
            {
                c.Dock = DockStyle.Fill;
                ((Button)c).AutoSize = false;
                c.Margin = new Padding(0, 0, 4, 4);
            }
        }

        // 마지막 열이 남는 폭을 채우게
        private void FitEntryColumns()
        {
            if (_lvEntries == null || _lvEntries.Columns.Count < 2) return;
            int w = _lvEntries.ClientSize.Width - _lvEntries.Columns[0].Width;
            if (w > 120) _lvEntries.Columns[1].Width = w;
        }

        private Group SelectedGroup
        {
            get
            {
                int i = _lstGroups.SelectedIndex;
                if (i < 0 || i >= _config.Groups.Count) return null;
                return _config.Groups[i];
            }
        }

        private void RefreshGroups(string selectId)
        {
            _lstGroups.Items.Clear();
            int sel = 0;
            bool found = false;
            for (int i = 0; i < _config.Groups.Count; i++)
            {
                Group g = _config.Groups[i];
                _lstGroups.Items.Add(g);
                if (selectId != null && g.Id == selectId) { sel = i; found = true; }
            }
            if (_lstGroups.Items.Count > 0)
            {
                if (!found && sel >= _lstGroups.Items.Count) sel = 0;
                _lstGroups.SelectedIndex = sel;
            }
            RefreshEntries();
        }

        private void RefreshEntries()
        {
            _lvEntries.BeginUpdate();
            _lvEntries.Items.Clear();
            Group g = SelectedGroup;
            if (g != null)
            {
                for (int i = 0; i < g.Entries.Count; i++)
                {
                    Entry e = g.Entries[i];
                    ListViewItem it = new ListViewItem(e.Name);
                    it.SubItems.Add(e.Target);
                    _lvEntries.Items.Add(it);
                }
                _lblGroupName.Text = g.Name;
                _lblGroupInfo.Text = "항목 " + g.Entries.Count + "개  ·  항목을 끌어서 순서를 바꾸거나, 탐색기에서 파일을 끌어 놓을 수 있습니다.";
                SetGroupIconPreview(g);
            }
            else
            {
                _lblGroupName.Text = "그룹 없음";
                _lblGroupInfo.Text = "왼쪽에서 [추가]로 그룹을 만드세요.";
                _picGroupIcon.Image = null;
            }
            _lvEntries.EndUpdate();
        }

        private void SetGroupIconPreview(Group g)
        {
            Image old = _picGroupIcon.Image;
            try
            {
                string path = GroupIcon.EnsureGroupIcon(g);
                _picGroupIcon.Image = GroupIcon.LoadFrame(path, 32);
            }
            catch
            {
                _picGroupIcon.Image = SystemIcons.Application.ToBitmap();
            }
            if (old != null) old.Dispose();
        }

        private int SelectedEntryIndex
        {
            get
            {
                if (_lvEntries.SelectedIndices.Count == 0) return -1;
                return _lvEntries.SelectedIndices[0];
            }
        }

        private void Status(string s)
        {
            _lblStatus.Text = s;
        }

        private void OnGroupAdd(object sender, EventArgs e)
        {
            string name = InputDialog.Prompt(this, "그룹 추가", "그룹 이름:", "새 그룹");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            Group g = new Group();
            g.Name = name;
            _config.Groups.Add(g);
            Commit(g);
            RefreshGroups(g.Id);
            PinOrGuide(g);
        }

        private void OnGroupRename(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) return;
            string name = InputDialog.Prompt(this, "그룹 이름 변경", "새 이름:", g.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            g.Name = name;
            Commit(g);
            RefreshGroups(g.Id);
            Status("그룹 이름을 변경했습니다.");
        }

        private void OnGroupDelete(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) return;
            if (MessageBox.Show("'" + g.Name + "' 그룹을 삭제할까요?", "그룹 삭제",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool unpinned = true;
            try { unpinned = ShortcutSync.RemoveGroup(_config, g.Id); } catch { unpinned = false; }
            _config.Groups.Remove(g);
            GroupStore.Save(_config);
            GroupIcon.CleanupOrphans(_config);
            RefreshGroups(null);
            Status(unpinned
                ? "그룹을 삭제했습니다."
                : "그룹을 삭제했습니다. 작업표시줄의 그룹 아이콘은 오른쪽 클릭 → '작업 표시줄에서 제거'로 직접 지워 주세요.");
        }

        /// <summary>새 그룹의 바로가기를 만들고 작업표시줄 고정을 시도한다. 막힌 환경이면 폴더에서 파일을 골라 보여 준다.</summary>
        private void PinOrGuide(Group g)
        {
            string lnk;
            try { lnk = ShortcutSync.CreateGroupShortcut(_config, g); }
            catch (Exception ex) { Status("그룹을 추가했지만 바로가기 생성 실패: " + ex.Message); return; }
            string gid = g.Id;
            if (IconHelper.TryPinToTaskbar(lnk, delegate { return ShortcutSync.IsGroupPinned(gid); }))
            {
                Status("그룹을 추가하고 작업표시줄에 고정했습니다: " + g.Name);
                return;
            }
            Status("그룹을 추가했습니다. 열린 폴더에서 '" + Path.GetFileName(lnk) +
                "' 을(를) 오른쪽 클릭 → '작업 표시줄에 고정' 을 한 번만 해 주세요. (Windows 가 프로그램의 자동 고정을 막습니다)");
            try { Process.Start("explorer.exe", "/select,\"" + lnk + "\""); }
            catch { }
        }

        // ---------- 그룹 순서 변경 ----------

        private void OnGroupUp(object sender, EventArgs e)
        {
            int i = _lstGroups.SelectedIndex;
            if (i > 0) MoveGroup(i, i - 1);
        }

        private void OnGroupDown(object sender, EventArgs e)
        {
            int i = _lstGroups.SelectedIndex;
            if (i >= 0 && i < _config.Groups.Count - 1) MoveGroup(i, i + 1);
        }

        private void OnGroupMoved(int from, int to)
        {
            MoveGroup(from, to);
        }

        private void MoveGroup(int from, int to)
        {
            if (from < 0 || from >= _config.Groups.Count) return;
            if (to < 0 || to >= _config.Groups.Count || to == from) return;
            Group g = _config.Groups[from];
            _config.Groups.RemoveAt(from);
            _config.Groups.Insert(to, g);
            GroupStore.Save(_config);
            RefreshGroups(g.Id);
            Status("그룹 순서를 바꿨습니다: " + g.Name);

            // 그룹 아이콘 색이 순서에 따라 정해지므로 모든 그룹 아이콘·바로가기를 다시 맞춘다
            Config cfg = _config;
            Background.RunSta(delegate
            {
                string err = null;
                try { ShortcutSync.SyncAll(cfg); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        if (err != null) Status("바로가기 정리 실패: " + err);
                        else RefreshEntries();
                    }));
                }
                catch { }
            });
        }

        private void OnGroupIcon(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) { MessageBox.Show("먼저 그룹을 선택하세요."); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "그룹 아이콘 선택";
                d.Filter = "아이콘/프로그램 (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|모든 파일 (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    g.IconLocation = IconHelper.EnsureIndex(d.FileName);
                    Commit(g);
                    RefreshEntries();
                    Status("그룹 아이콘을 설정했습니다.");
                }
            }
        }

        private void OnGroupIconReset(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) return;
            g.IconLocation = "";
            Commit(g);
            RefreshEntries();
            Status("그룹 아이콘을 기본 모양(항목 아이콘 모음)으로 되돌렸습니다.");
        }

        // ---------- 드래그 앤 드롭 ----------

        // 목록 안의 항목을 집어서 끌기 시작 (순서 변경용)
        private void OnEntriesItemDrag(object sender, ItemDragEventArgs e)
        {
            ListViewItem it = e.Item as ListViewItem;
            if (it == null) return;
            _lvEntries.DoDragDrop(it, DragDropEffects.Move);
        }

        private void OnEntriesDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = GetDropEffect(e);
        }

        private void OnEntriesDragOver(object sender, DragEventArgs e)
        {
            e.Effect = GetDropEffect(e);
            if (e.Effect == DragDropEffects.None) { ClearDropMark(); return; }

            Point pt = _lvEntries.PointToClient(new Point(e.X, e.Y));
            AutoScrollEntries(pt);
            SetDropMark(GetInsertIndex(pt));
        }

        private DragDropEffects GetDropEffect(DragEventArgs e)
        {
            if (SelectedGroup == null) return DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.Copy;
            if (e.Data.GetDataPresent(typeof(ListViewItem))) return DragDropEffects.Move;
            return DragDropEffects.None;
        }

        // 커서 위치 -> 삽입될 인덱스 (항목의 위쪽 절반이면 앞, 아래쪽 절반이면 뒤)
        private int GetInsertIndex(Point clientPt)
        {
            if (_lvEntries.Items.Count == 0) return 0;
            ListViewItem hit = _lvEntries.GetItemAt(8, clientPt.Y);
            if (hit == null)
            {
                ListViewItem first = _lvEntries.Items[0];
                if (clientPt.Y < first.Bounds.Top) return 0;
                return _lvEntries.Items.Count;
            }
            Rectangle r = hit.Bounds;
            if (clientPt.Y > r.Top + r.Height / 2) return hit.Index + 1;
            return hit.Index;
        }

        // 삽입 위치선 표시
        private void SetDropMark(int insertIndex)
        {
            if (_dropIndex == insertIndex) return;
            _dropIndex = insertIndex;
            _lvEntries.DropLineIndex = insertIndex;
        }

        private void ClearDropMark()
        {
            _dropIndex = -1;
            _lvEntries.DropLineIndex = -1;
        }

        // 목록 가장자리에 머무르면 자동 스크롤
        private void AutoScrollEntries(Point clientPt)
        {
            const int margin = 18;
            if (clientPt.Y < margin)
                SendMessage(_lvEntries.Handle, WM_VSCROLL, (IntPtr)SB_LINEUP, IntPtr.Zero);
            else if (clientPt.Y > _lvEntries.ClientSize.Height - margin)
                SendMessage(_lvEntries.Handle, WM_VSCROLL, (IntPtr)SB_LINEDOWN, IntPtr.Zero);
        }

        private void OnEntriesDragDrop(object sender, DragEventArgs e)
        {
            Group g = SelectedGroup;
            Point pt = _lvEntries.PointToClient(new Point(e.X, e.Y));
            int insert = GetInsertIndex(pt);
            ClearDropMark();
            if (g == null) return;

            // 1) 탐색기에서 파일을 끌어다 놓은 경우 -> 해당 위치에 삽입
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                List<Entry> added = GroupStore.EntriesFromPaths(files);
                if (added.Count == 0)
                {
                    Status("추가할 수 있는 파일이 없습니다.");
                    return;
                }
                if (insert < 0) insert = 0;
                if (insert > g.Entries.Count) insert = g.Entries.Count;
                g.Entries.InsertRange(insert, added);
                Commit(g);
                RefreshGroups(g.Id);
                SelectEntry(insert);
                Status(added.Count + "개 항목을 끌어다 놓아 추가했습니다.");
                return;
            }

            // 2) 목록 내부에서 순서를 바꾼 경우
            if (e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                ListViewItem it = (ListViewItem)e.Data.GetData(typeof(ListViewItem));
                int from = it.Index;
                if (from < 0 || from >= g.Entries.Count) return;
                int to = insert;
                if (to > from) to--;            // 자기 자신을 뺀 뒤의 위치로 보정
                if (to < 0) to = 0;
                if (to > g.Entries.Count - 1) to = g.Entries.Count - 1;
                if (to == from) return;

                Entry moved = g.Entries[from];
                g.Entries.RemoveAt(from);
                g.Entries.Insert(to, moved);
                Commit(g);
                RefreshEntries();
                SelectEntry(to);
                Status("순서를 변경했습니다: " + moved.Name);
            }
        }

        private void SelectEntry(int index)
        {
            if (index < 0 || index >= _lvEntries.Items.Count) return;
            _lvEntries.Items[index].Selected = true;
            _lvEntries.Items[index].Focused = true;
            _lvEntries.EnsureVisible(index);
            _lvEntries.Focus();
        }

        // ---------- 항목 버튼 ----------

        private void OnEntryAdd(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) { MessageBox.Show("먼저 그룹을 선택하세요."); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "실행할 프로그램/바로가기 선택";
                d.Multiselect = true;
                d.Filter = "프로그램 및 바로가기 (*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc)|*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc|모든 파일 (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    foreach (string f in d.FileNames)
                    {
                        Entry en = new Entry();
                        en.Target = f;
                        en.Name = Path.GetFileNameWithoutExtension(f);
                        en.IconLocation = IconHelper.GetIconLocationForPath(f);
                        g.Entries.Add(en);
                    }
                    Commit(g);
                    RefreshGroups(g.Id);
                    Status(d.FileNames.Length + "개 항목을 추가했습니다.");
                }
            }
        }

        private void OnEntryDelete(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            int i = SelectedEntryIndex;
            if (g == null || i < 0) return;
            g.Entries.RemoveAt(i);
            Commit(g);
            RefreshGroups(g.Id);
            Status("항목을 삭제했습니다.");
        }

        private void OnEntryUp(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            int i = SelectedEntryIndex;
            if (g == null || i <= 0) return;
            Entry tmp = g.Entries[i - 1];
            g.Entries[i - 1] = g.Entries[i];
            g.Entries[i] = tmp;
            Commit(g);
            RefreshEntries();
            _lvEntries.Items[i - 1].Selected = true;
            _lvEntries.Items[i - 1].Focused = true;
        }

        private void OnEntryDown(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            int i = SelectedEntryIndex;
            if (g == null || i < 0 || i >= g.Entries.Count - 1) return;
            Entry tmp = g.Entries[i + 1];
            g.Entries[i + 1] = g.Entries[i];
            g.Entries[i] = tmp;
            Commit(g);
            RefreshEntries();
            _lvEntries.Items[i + 1].Selected = true;
            _lvEntries.Items[i + 1].Focused = true;
        }

        private void OnEntryRename(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            int i = SelectedEntryIndex;
            if (g == null || i < 0) return;
            Entry en = g.Entries[i];
            string name = InputDialog.Prompt(this, "이름 변경", "표시 이름:", en.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            en.Name = name;
            Commit(g);
            RefreshEntries();
            Status("이름을 변경했습니다.");
        }

        private void OnEntryRun(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            int i = SelectedEntryIndex;
            if (g == null || i < 0) return;
            try
            {
                IconHelper.Launch(g.Entries[i]);
                Status("실행: " + g.Entries[i].Target);
            }
            catch (Exception ex)
            {
                MessageBox.Show("실행할 수 없습니다.\n" + ex.Message, "실행 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnPreview(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) return;
            FlyoutForm f = new FlyoutForm(g);
            f.Show();
        }

        private void OnOpenConfigFolder(object sender, EventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", "\"" + GroupStore.AppDir + "\"");
            }
            catch { }
        }

        private void OnSave(object sender, EventArgs e)
        {
            GroupStore.Save(_config);
            try { ShortcutSync.SyncAll(_config); }
            catch (Exception ex) { Status("저장했지만 바로가기 정리 실패: " + ex.Message); return; }
            RefreshEntries();
            List<string> notPinned = new List<string>();
            foreach (Group g in _config.Groups)
            {
                if (ShortcutSync.IsGroupPinned(g.Id)) continue;
                string gid = g.Id;
                try
                {
                    string lnk = ShortcutSync.CreateGroupShortcut(_config, g);
                    if (IconHelper.TryPinToTaskbar(lnk, delegate { return ShortcutSync.IsGroupPinned(gid); })) continue;
                }
                catch { }
                notPinned.Add(g.Name);
            }
            Status(notPinned.Count == 0
                ? "저장했습니다. 모든 그룹이 작업표시줄에 적용되어 있습니다."
                : "저장했습니다. 아직 작업표시줄에 고정되지 않은 그룹: " + string.Join(", ", notPinned.ToArray()));
        }

        // ---------- 바로가기 저장 폴더 ----------

        private void UpdateShortcutFolderUi()
        {
            string f = GroupStore.ResolveShortcutFolder(_config);
            _txtShortcutFolder.Text = f;
            bool isDefault = string.IsNullOrEmpty(_config.ShortcutFolder)
                             || !Directory.Exists(_config.ShortcutFolder);
            _txtShortcutFolder.ForeColor = isDefault ? Color.DimGray : SystemColors.WindowText;
            _toolTip.SetToolTip(_txtShortcutFolder,
                isDefault ? f + "  (기본값: 실행 파일 폴더\\바로가기)" : f);
        }

        private void OnChangeShortcutFolder(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "작업표시줄 바로가기(.lnk)를 항상 저장할 폴더를 선택하세요.";
                d.ShowNewFolderButton = true;
                string cur = GroupStore.ResolveShortcutFolder(_config);
                if (Directory.Exists(cur)) d.SelectedPath = cur;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _config.ShortcutFolder = GroupStore.ToStoredFolder(d.SelectedPath);
                GroupStore.Save(_config);
                UpdateShortcutFolderUi();
                Status("바로가기 저장 폴더를 설정했습니다: " + d.SelectedPath);
            }
        }

        private void OnOpenShortcutFolder(object sender, EventArgs e)
        {
            string f = GroupStore.ResolveShortcutFolder(_config);
            try
            {
                if (!Directory.Exists(f)) Directory.CreateDirectory(f);
                Process.Start("explorer.exe", "\"" + f + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("폴더를 열 수 없습니다.\n" + ex.Message, "오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCreateShortcut(object sender, EventArgs e)
        {
            Group g = SelectedGroup;
            if (g == null) { MessageBox.Show("먼저 그룹을 선택하세요."); return; }
            if (g.Entries.Count == 0)
            {
                if (MessageBox.Show("이 그룹에는 항목이 없습니다. 그래도 바로가기를 만들까요?",
                    "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            string lnk;
            try
            {
                lnk = ShortcutSync.CreateGroupShortcut(_config, g);
            }
            catch (Exception ex)
            {
                MessageBox.Show("바로가기 생성 실패:\n" + ex.Message, "오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string gid = g.Id;
            if (IconHelper.TryPinToTaskbar(lnk, delegate { return ShortcutSync.IsGroupPinned(gid); }))
            {
                Status("바로가기 생성 + 작업 표시줄 고정 완료 → " + lnk);
            }
            else
            {
                // 고정이 막힌 환경이면 폴더만 열어 준다 (대화상자는 띄우지 않음)
                Status("바로가기 생성 완료 → 열린 폴더에서 '" + Path.GetFileName(lnk) +
                    "' 을(를) 오른쪽 클릭 → '작업 표시줄에 고정' 하세요. (Windows 가 프로그램의 자동 고정을 막습니다)");
                try { Process.Start("explorer.exe", "/select,\"" + lnk + "\""); }
                catch { }
            }
        }

        private void OnAgentOptionChanged(object sender, EventArgs e)
        {
            _config.AutoStartAgent = _chkAgent.Checked;
            _config.ArrangeRunningApps = _chkArrange.Checked;
            if (_chkInstant != null) _config.InstantPlacement = _chkInstant.Checked;
            if (_cmbArrangeMode != null)
            {
                _config.ArrangeMode = _cmbArrangeMode.SelectedIndex == 1 ? Config.ModeAfterLastGroup : Config.ModeNextToGroup;
                _cmbArrangeMode.Enabled = _chkArrange.Checked;
            }
            GroupStore.Save(_config);
            try { ShortcutSync.UpdateStartup(_config.AutoStartAgent); } catch { }
            if (_config.AutoStartAgent) AgentContext.EnsureRunning(_config);
            Status(_config.AutoStartAgent
                ? "도우미를 사용합니다."
                : "도우미 자동 실행을 껐습니다. (실행 중인 도우미는 트레이 아이콘 메뉴에서 종료하세요)");
        }

        private void OnRefreshIcons(object sender, EventArgs e)
        {
            Status("아이콘과 앱 정보를 다시 읽는 중...");
            this.Cursor = Cursors.WaitCursor;
            Config cfg = _config;
            Background.RunSta(delegate
            {
                string err = null;
                try { ShortcutSync.RefreshEverything(cfg); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        this.Cursor = Cursors.Default;
                        RefreshEntries();
                        Status(err == null
                            ? "아이콘과 앱 정보를 다시 읽었습니다. 작업표시줄 아이콘이 곧 새로 그려집니다."
                            : "다시 읽기 실패: " + err);
                    }));
                }
                catch { }
            });
        }

        private void OnRefreshTaskbar(object sender, EventArgs e)
        {
            if (MessageBox.Show("탐색기를 다시 시작해서 작업표시줄 아이콘을 새로 그립니다.\n" +
                "열려 있는 탐색기(폴더) 창은 닫힙니다. 계속할까요?", "작업표시줄 새로고침",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            GroupStore.Save(_config);
            try { ShortcutSync.SyncAll(_config); } catch { }
            try
            {
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
                System.Threading.Thread.Sleep(1500);
                if (Process.GetProcessesByName("explorer").Length == 0) Process.Start("explorer.exe");
                Status("작업표시줄을 새로 고쳤습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("탐색기를 다시 시작하지 못했습니다.\n" + ex.Message, "오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
