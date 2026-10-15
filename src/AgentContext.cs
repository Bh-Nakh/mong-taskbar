using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarGroup
{
    /// <summary>백그라운드 도우미 (몽작업표시줄.exe --agent).
    ///  - 파일을 끌어서 작업표시줄의 그룹 아이콘 위에 놓으면 그룹 맨 끝에 추가
    ///  - 끈 채로 잠깐 머무르면 그룹을 펼쳐서 원하는 위치에 놓게 해 줌
    ///  - 그룹 안의 앱이 실행되면 작업표시줄 버튼을 그룹 아이콘 오른쪽으로 정렬</summary>
    public class AgentContext : ApplicationContext
    {
        private const string MutexName = "Local\\TaskbarGroup.Agent";
        private static Mutex _mutex;

        public static bool TryAcquire()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            return created;
        }

        public static bool IsRunning()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(MutexName)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>설정에서 켜져 있고 아직 안 떠 있으면 도우미를 띄운다.</summary>
        public static void EnsureRunning(Config cfg)
        {
            if (cfg == null || !cfg.AutoStartAgent) return;
            if (IsRunning()) return;
            try { Process.Start(Application.ExecutablePath, "--agent"); }
            catch { }
        }

        private class GroupButton
        {
            public string GroupId;
            public Rectangle Rect;
        }

        // ---------- 상태 ----------

        private readonly NotifyIcon _tray;
        private readonly Form _uiOwner;
        private readonly ShellHookWindow _shell;
        private readonly DropOverlayForm _overlay;
        private FlyoutForm _dropFlyout;
        private readonly System.Windows.Forms.Timer _arrangeTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _endDragTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _startTimer = new System.Windows.Forms.Timer();
        private readonly FileSystemWatcher _watcher;
        private readonly List<FileSystemWatcher> _linkWatchers = new List<FileSystemWatcher>();

        // 설정은 메모리에 두고 groups.json 이 바뀔 때만 다시 읽는다
        private Config _cfg;
        private int _revision;

        private MouseHook _hook;
        private volatile bool _hookLDown;
        private int _movePending;
        private readonly System.Windows.Forms.Timer _pollTimer = new System.Windows.Forms.Timer();
        private long _pollMoves;
        private Point _pollPt;
        private long _dbgMoves;                      // 훅이 받은 이동 이벤트 수 (훅이 조용한지 판단용)
        // 훅이 받은 마지막 마우스 좌표 (x,y 를 한 값에 담아 원자적으로 주고받음).
        // 이벤트를 합칠 때 "처리하는 순간의 커서 위치" 를 읽으면 마지막 이동이 아직 반영되기 전이라
        // 멈춘 위치를 놓칠 수 있으므로, 훅이 준 좌표를 쓴다.
        private long _lastPt;

        private static long Pack(int x, int y) { return ((long)x << 32) | (uint)y; }
        private static Point Unpack(long v) { return new Point((int)(v >> 32), (int)(v & 0xFFFFFFFF)); }

        private bool _lDown;
        private bool _downOnTaskbar;
        private bool _dragging;
        private Point _downPt;
        // 모니터마다 작업표시줄이 있으므로 작업표시줄별로 그룹 버튼 위치를 따로 기억한다
        private List<KeyValuePair<IntPtr, Rectangle>> _trays = new List<KeyValuePair<IntPtr, Rectangle>>();
        // 작업표시줄별 그룹 버튼 위치. 버튼 배치가 바뀌는 일(창 생성/닫힘, 정렬 이동, 탐색기 재시작)이
        // 있을 때만 _layoutVersion 을 올려서 다시 읽게 한다 — 주기적으로 읽지 않는다.
        private readonly Dictionary<IntPtr, List<GroupButton>> _buttons = new Dictionary<IntPtr, List<GroupButton>>();
        private readonly Dictionary<IntPtr, int> _buttonsVersion = new Dictionary<IntPtr, int>();
        private readonly HashSet<IntPtr> _loading = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, DateTime> _failedAt = new Dictionary<IntPtr, DateTime>();
        private int _layoutVersion;

        // 마우스를 올리면 펼치기
        private int _hoverPending;
        private DateTime _traysAt = DateTime.MinValue;
        private GroupButton _hoverCandidate;
        private readonly System.Windows.Forms.Timer _hoverOpenTimer = new System.Windows.Forms.Timer();
        // 그룹 아이콘·목록을 벗어났을 때 한 번만 거는 0.5초 타이머 (주기적으로 확인하지 않음)
        private readonly System.Windows.Forms.Timer _leaveTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _layoutTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _retryTimer = new System.Windows.Forms.Timer();
        private int _retries;
        private ShowHook _showHook;
        private DateTime _hoverSince = DateTime.MinValue;

        public AgentContext()
        {
            Knowledge.Persist = true;
            ShortcutSync.CachePinned = true;
            _cfg = GroupStore.Load();
            _revision = _cfg.IconRevision;

            _overlay = new DropOverlayForm();
            IntPtr force = _overlay.Handle;   // UI 스레드 마샬링용 핸들을 미리 만든다
            // 업데이트 안내창의 부모(보이지 않는 작은 창). 창 없는 도우미라서 따로 둔다.
            _uiOwner = new Form();
            _uiOwner.ShowInTaskbar = false;
            _uiOwner.FormBorderStyle = FormBorderStyle.None;
            _uiOwner.StartPosition = FormStartPosition.Manual;
            _uiOwner.Location = new Point(-32000, -32000);
            _uiOwner.Size = new Size(1, 1);
            IntPtr force2 = _uiOwner.Handle;
            _overlay.HoverExpand += OnOverlayHover;
            _overlay.FilesDropped += OnOverlayDrop;

            _tray = new NotifyIcon();
            Icon small = IconHelper.GetFileIcon(Application.ExecutablePath, false);
            _tray.Icon = small ?? SystemIcons.Application;
            _tray.Text = "몽작업표시줄 도우미 " + AppVersion.Display;
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem verItem = new ToolStripMenuItem("몽작업표시줄 " + AppVersion.Display);
            verItem.Enabled = false;
            menu.Items.Add(verItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("그룹 관리 열기", null, delegate { OpenConfigurator(); });
            menu.Items.Add("실행 중인 앱 지금 정렬", null, delegate { Arranger.RunAsync(_cfg); });
            menu.Items.Add("아이콘·정보 다시 읽기", null, delegate { RefreshEverything(); });
            menu.Items.Add("업데이트 확인", null, delegate { MongUpdate.UpdateUi.CheckNow(_uiOwner, "mongtaskbar", "몽작업표시줄", AppVersion.Number, "--agent"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("도우미 종료", null, delegate { ExitThread(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenConfigurator(); };
            _tray.Visible = true;
            // 도우미가 시작될 때 새 버전이 있는지 확인 (있을 때만 안내, 사용자가 눌러야 받는다)
            MongUpdate.UpdateUi.CheckAtStartup(_uiOwner, "mongtaskbar", "몽작업표시줄", AppVersion.Number, "--agent");

            _shell = new ShellHookWindow();
            _shell.WindowCreated += OnWindowCreated;
            _shell.WindowDestroyed += InvalidateLayout;
            _shell.TaskbarCreated += OnTaskbarCreated;
            Arranger.Moved += delegate { try { _overlay.BeginInvoke(new Action(InvalidateLayout)); } catch { } };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { try { _overlay.BeginInvoke(new Action(OnTaskbarCreated)); } catch { } };

            _arrangeTimer.Interval = 1500;
            _arrangeTimer.Tick += delegate
            {
                _arrangeTimer.Stop();
                if (_cfg.ArrangeRunningApps) Arranger.RunAsync(_cfg);
            };

            _endDragTimer.Interval = 700;
            _endDragTimer.Tick += delegate
            {
                _endDragTimer.Stop();
                if (_lDown) return;
                _overlay.HideOverlay();
                if (_dropFlyout != null && !_dropFlyout.IsDisposed && _dropFlyout.DragMode)
                    _dropFlyout.Close();
            };

            _startTimer.Interval = 800;
            _startTimer.Tick += delegate
            {
                _startTimer.Stop();
                // 마우스를 올렸을 때 바로 펼칠 수 있게 버튼 위치부터 읽어 둔다 (아이콘 동기화 등은 그 뒤에)
                PreloadButtons();
                // 느린 드라이브의 아이콘을 읽느라 오래 걸릴 수 있으므로 백그라운드에서
                Background.RunSta(delegate
                {
                    ShortcutSync.SyncAll(_cfg);
                    // 처음 한 번만: 실행 중인 프로세스 전체 조사 + 시작 메뉴 기록(없을 때만)
                    try { ProcessRegistry.Init(); } catch (Exception ex) { Log.Write("프로세스 조사 실패: " + ex.Message); }
                    try { Knowledge.StartMenuNames(); } catch { }
                    try
                    {
                        int n = TaskbarUia.CleanupOrphanShadows();
                        if (n > 0) Log.Write("남아 있던 툴팁 그림자 " + n + "개 정리");
                    }
                    catch { }
                    Knowledge.Save();
                    try { Placement.Apply(_cfg); } catch { }
                    try { _overlay.BeginInvoke(new Action(delegate { PreloadButtons(); ScheduleArrange(); })); } catch { }
                });
            };
            _startTimer.Start();

            try
            {
                _watcher = new FileSystemWatcher(GroupStore.AppDir, "groups.json");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                _watcher.SynchronizingObject = _overlay;
                _watcher.Changed += delegate { ReloadConfig(); };
                _watcher.Created += delegate { ReloadConfig(); };
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }

            // 고정 폴더·시작 메뉴: 바뀐 바로가기만 기록에서 지워 다음에 다시 읽게 한다
            WatchLinks(ShortcutSync.PinnedFolder, false, true);
            foreach (string root in Knowledge.StartMenuRoots) WatchLinks(root, true, false);

            _hoverOpenTimer.Interval = 150;   // 작업표시줄을 훑고 지나갈 때 깜빡이지 않을 만큼만
            _hoverOpenTimer.Tick += OnHoverOpenTick;
            _leaveTimer.Interval = 500;
            _leaveTimer.Tick += OnLeaveTick;
            _layoutTimer.Interval = 300;
            _layoutTimer.Tick += delegate { _layoutTimer.Stop(); PreloadButtons(); };
            _retryTimer.Interval = 1000;
            _retryTimer.Tick += delegate { _retryTimer.Stop(); PreloadButtons(); };

            // 일반 아이콘의 미리보기·이름 툴팁이 뜨는 순간을 알림으로 받는다
            _showHook = new ShowHook(OnExplorerPopupShown);

            _hook = new MouseHook(OnHook);

            // 관리자 권한 창이 앞에 있으면 윈도우가 일반 권한 프로그램의 마우스 훅에 이벤트를 주지 않는다.
            // 그럴 때를 위해, 커서가 움직였는데 훅이 조용하면 커서 위치를 직접 읽어 호버를 판단한다.
            _pollTimer.Interval = 40;
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }

        private void OnPollTick(object sender, EventArgs e)
        {
            Point p = Cursor.Position;
            long moves = Interlocked.Read(ref _dbgMoves);
            bool hookSilent = moves == _pollMoves;
            _pollMoves = moves;
            if (p == _pollPt) return;
            _pollPt = p;
            if (!hookSilent || _hookLDown) return;
            if (TrayAt(p) == IntPtr.Zero && !HoverFlyoutOpen) return;
            Interlocked.Exchange(ref _lastPt, Pack(p.X, p.Y));
            OnHover(p);
        }

        private void OpenConfigurator()
        {
            try { Process.Start(Application.ExecutablePath); }
            catch { }
        }

        private void ScheduleArrange()
        {
            _arrangeTimer.Stop();
            _arrangeTimer.Start();
        }

        private void ReloadConfig()
        {
            Config cfg = GroupStore.Load();
            if (cfg.IconRevision != _revision)
            {
                // "아이콘·정보 다시 읽기" 가 눌렸다 → 기록을 비운다
                _revision = cfg.IconRevision;
                Knowledge.Reset();
            }
            _cfg = cfg;
            Arranger.ClearCaches();
            InvalidateLayout();
            ScheduleArrange();
            Background.RunSta(delegate { Placement.Apply(cfg); });
        }

        private void RefreshEverything()
        {
            Background.RunSta(delegate
            {
                Config cfg = GroupStore.Load();
                ShortcutSync.RefreshEverything(cfg);
                try { _overlay.BeginInvoke(new Action(delegate { ShowTip("아이콘과 앱 정보를 다시 읽었습니다."); })); } catch { }
            });
        }

        private void WatchLinks(string dir, bool subdirs, bool pinned)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                FileSystemWatcher w = new FileSystemWatcher(dir, "*.lnk");
                w.IncludeSubdirectories = subdirs;
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                w.SynchronizingObject = _overlay;
                FileSystemEventHandler h = delegate (object o, FileSystemEventArgs e)
                {
                    Knowledge.FileChanged(e.FullPath, e.ChangeType == WatcherChangeTypes.Deleted);
                    if (pinned) { ShortcutSync.InvalidatePinned(); InvalidateLayout(); }
                    else Arranger.ClearCaches();
                };
                w.Changed += h; w.Created += h; w.Deleted += h;
                w.Renamed += delegate (object o, RenamedEventArgs e)
                {
                    Knowledge.FileChanged(e.OldFullPath, true);
                    Knowledge.FileChanged(e.FullPath, false);
                    if (pinned) { ShortcutSync.InvalidatePinned(); InvalidateLayout(); }
                    else Arranger.ClearCaches();
                };
                w.EnableRaisingEvents = true;
                _linkWatchers.Add(w);
            }
            catch { }
        }

        /// <summary>새 창: 그 프로세스 하나만 조사해서 목록에 더하고, 그룹 항목과 관련 있을 때만 정렬한다.</summary>
        private void OnWindowCreated(IntPtr hwnd)
        {
            InvalidateLayout();
            Config cfg = _cfg;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunningProcess rp = ProcessRegistry.OnWindowCreated(hwnd);
                    if (rp == null || !cfg.ArrangeRunningApps) return;
                    if (!Arranger.IsRelevant(cfg, rp)) return;
                    Log.Write("그룹 앱 창 감지: " + Path.GetFileName(rp.ExePath));
                    _overlay.BeginInvoke(new Action(ScheduleArrange));
                }
                catch { }
            });
        }

        /// <summary>탐색기 재시작/화면 배치 변경: 작업표시줄 위치를 다시 잡고, 그룹 아이콘을 다시 읽게 알린다.</summary>
        // 탐색기가 다시 시작되면 트레이 아이콘이 사라질 수 있다. 아이콘을 껐다 켜서 새 탐색기에 다시 등록한다.
        private void ReAddTrayIcon()
        {
            try { _tray.Visible = false; _tray.Visible = true; } catch { }
        }

        private void OnTaskbarCreated()
        {
            ReAddTrayIcon();
            if (_showHook != null) _showHook.Rehook();
            _traysAt = DateTime.MinValue;
            InvalidateLayout();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 5000;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                ReAddTrayIcon();   // 탐색기 트레이가 준비된 뒤 한 번 더
                Background.RunSta(delegate { ShortcutSync.NotifyPinnedIcons(); Placement.Apply(_cfg); });
                ScheduleArrange();
            };
            t.Start();
        }

        private void InvalidateLayout()
        {
            _layoutVersion++;
            // 배치가 바뀌었으면 마우스를 올리기 전에 미리 다시 읽어 둔다 (연달아 바뀌면 한 번만)
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        private void PreloadButtons()
        {
            _trays = TaskbarUia.AllTrays();
            _traysAt = DateTime.Now;
            foreach (KeyValuePair<IntPtr, Rectangle> t in _trays) LoadButtonsAsync(t.Key);
        }

        /// <summary>작업표시줄의 그룹 버튼 위치. 기록이 최신이면 그대로, 아니면 백그라운드로 다시 읽고
        /// 그동안은 이전 기록(없으면 null)을 돌려준다.</summary>
        private List<GroupButton> ButtonsFor(IntPtr tray)
        {
            List<GroupButton> list;
            bool have = _buttons.TryGetValue(tray, out list);
            int ver;
            if (!have || !_buttonsVersion.TryGetValue(tray, out ver) || ver != _layoutVersion)
            {
                // 직전에 빈 결과였으면 1초 지나서 다시 시도 (마우스가 움직일 때마다 묻지 않게)
                DateTime failed;
                if (!_failedAt.TryGetValue(tray, out failed) || (DateTime.Now - failed).TotalSeconds >= 1)
                    LoadButtonsAsync(tray);
            }
            return list;
        }

        private void LoadButtonsAsync(IntPtr tray)
        {
            if (_loading.Contains(tray)) return;
            _loading.Add(tray);
            int version = _layoutVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<GroupButton> list = LoadGroupButtons(tray);
                try
                {
                    _overlay.BeginInvoke(new Action(delegate
                    {
                        _loading.Remove(tray);
                        if (list.Count > 0)
                        {
                            _buttons[tray] = list;
                            _buttonsVersion[tray] = version;
                            _failedAt.Remove(tray);
                            _retries = 0;
                        }
                        else
                        {
                            // 그룹 버튼을 하나도 못 찾음 = 읽기 실패로 보고, 이전 기록은 그대로 두고 최신으로 표시하지 않는다
                            if (!_buttons.ContainsKey(tray)) _buttons[tray] = list;
                            _buttonsVersion.Remove(tray);
                            _failedAt[tray] = DateTime.Now;
                            // 마우스를 움직이지 않아도 1초 뒤 다시 시도 (최대 5번)
                            if (_retries < 5 && !_retryTimer.Enabled)
                            {
                                _retries++;
                                Log.Write("작업표시줄 그룹 버튼을 못 읽음 → 1초 뒤 다시 시도 (" + _retries + ")");
                                _retryTimer.Start();
                            }
                        }
                        Point c = Cursor.Position;
                        if (TrayAt(c) != tray) return;
                        if (_lDown) { if (_dragging) UpdateOverlay(list, c); }
                        else OnHover(c);
                    }));
                }
                catch { }
            });
        }

        // ---------- 전역 마우스 훅: 작업표시줄 위로 끌고 오는지 감시 ----------

        // 훅 스레드에서 불린다: 상태 처리는 UI 스레드로 넘긴다
        private void OnHook(int msg, int x, int y, bool ours)
        {
            if (ours) return;
            Point pt = new Point(x, y);
            try
            {
                if (msg == MouseHook.WM_LBUTTONDOWN)
                {
                    _hookLDown = true;
                    _overlay.BeginInvoke(new Action(delegate { OnLeftDown(pt); }));
                }
                else if (msg == MouseHook.WM_LBUTTONUP)
                {
                    _hookLDown = false;
                    _overlay.BeginInvoke(new Action(OnLeftUp));
                }
                else if (msg == MouseHook.WM_MOUSEMOVE)
                {
                    // 좌표를 먼저 저장한 다음 대기 여부를 본다 (처리 쪽은 대기 해제 후 좌표를 읽음)
                    // → 합쳐져 건너뛴 이벤트가 있어도 마지막 좌표는 반드시 처리된다
                    Interlocked.Exchange(ref _lastPt, Pack(x, y));
                    Interlocked.Increment(ref _dbgMoves);
                    if (!_hookLDown)
                    {
                        if (Interlocked.Exchange(ref _hoverPending, 1) == 0)
                        {
                            _overlay.BeginInvoke(new Action(delegate
                            {
                                Interlocked.Exchange(ref _hoverPending, 0);
                                OnHover(Unpack(Interlocked.Read(ref _lastPt)));
                            }));
                        }
                    }
                    else if (Interlocked.Exchange(ref _movePending, 1) == 0)
                    {
                        // 이동 이벤트는 많으므로 처리 대기 중인 게 있으면 합친다
                        _overlay.BeginInvoke(new Action(delegate
                        {
                            Interlocked.Exchange(ref _movePending, 0);
                            if (_lDown) OnLeftMove(Unpack(Interlocked.Read(ref _lastPt)));
                        }));
                    }
                }
            }
            catch { }
        }

        private IntPtr TrayAt(Point pt)
        {
            foreach (KeyValuePair<IntPtr, Rectangle> t in _trays)
                if (t.Value.Contains(pt)) return t.Key;
            return IntPtr.Zero;
        }

        private void OnLeftDown(Point pt)
        {
            _lDown = true;
            _downPt = pt;
            _trays = TaskbarUia.AllTrays();
            // 작업표시줄 안에서 시작한 드래그(버튼 순서 바꾸기 등)는 건드리지 않는다
            _downOnTaskbar = TrayAt(pt) != IntPtr.Zero;
            _dragging = false;
            _endDragTimer.Stop();
        }

        private void OnLeftMove(Point pt)
        {
            if (_downOnTaskbar) return;
            if (TaskbarUia.FullscreenOn(pt)) { if (_overlay.Visible) _overlay.HideOverlay(); return; }
            if (!_dragging)
            {
                if (Math.Abs(pt.X - _downPt.X) < 8 && Math.Abs(pt.Y - _downPt.Y) < 8) return;
                _dragging = true;
            }

            IntPtr tray = TrayAt(pt);
            if (tray != IntPtr.Zero)
            {
                List<GroupButton> list = ButtonsFor(tray);
                if (list == null) return;
                UpdateOverlay(list, pt);
            }
            else if (_overlay.Visible && !_overlay.Bounds.Contains(pt))
            {
                _overlay.HideOverlay();
            }
        }

        private void OnLeftUp()
        {
            _lDown = false;
            if (_dragging && (_overlay.Visible || (_dropFlyout != null && !_dropFlyout.IsDisposed)))
            {
                // 놓는 순간의 드롭 처리가 먼저 끝나도록 조금 뒤에 정리
                _endDragTimer.Stop();
                _endDragTimer.Start();
            }
            _dragging = false;
        }

        /// <summary>작업표시줄 하나의 그룹 버튼 위치 (UI Automation, 백그라운드 스레드에서 호출).</summary>
        private static List<GroupButton> LoadGroupButtons(IntPtr tray)
        {
            List<GroupButton> list = new List<GroupButton>();
            try
            {
                List<KeyValuePair<string, string>> pinned = ShortcutSync.PinnedGroups();
                List<TaskbarButton> buttons = TaskbarUia.GetButtons(tray);
                Rectangle trayRect = Rectangle.Empty;
                foreach (KeyValuePair<IntPtr, Rectangle> t in TaskbarUia.AllTrays())
                    if (t.Key == tray) trayRect = t.Value;
                foreach (KeyValuePair<string, string> kv in pinned)
                {
                    foreach (TaskbarButton b in buttons)
                    {
                        if (string.Equals(b.Name, kv.Key, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(b.BaseName, kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            GroupButton gb = new GroupButton();
                            gb.GroupId = kv.Value;
                            gb.Rect = Extend(b, buttons, trayRect);
                            list.Add(gb);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("그룹 버튼 읽기 실패: " + ex.Message); }
            return list;
        }

        /// <summary>버튼이 작업표시줄 가장자리에서 아주 가까우면(버튼 테두리 여백 정도) 그 가장자리까지만 감지 영역을 넓힌다.
        /// 마우스를 화면 끝까지 내려도 인식되게 하되, 작업표시줄이 두 줄일 때 버튼 아래 빈 줄은 포함하지 않는다.
        /// 다른 버튼이 있는 칸은 넓히지 않는다.</summary>
        private static Rectangle Extend(TaskbarButton b, List<TaskbarButton> all, Rectangle tray)
        {
            if (tray.IsEmpty) return b.Rect;
            const int Edge = 10;   // 이 정도까지는 가장자리 여백으로 본다
            Rectangle r = b.Rect;
            bool horizontal = tray.Width >= tray.Height;
            Rectangle ext;
            if (horizontal)
            {
                int top = r.Top - tray.Top <= Edge ? tray.Top : r.Top;
                int bottom = tray.Bottom - r.Bottom <= Edge ? tray.Bottom : r.Bottom;
                ext = new Rectangle(r.X, top, r.Width, bottom - top);
            }
            else
            {
                int left = r.Left - tray.Left <= Edge ? tray.Left : r.Left;
                int right = tray.Right - r.Right <= Edge ? tray.Right : r.Right;
                ext = new Rectangle(left, r.Y, right - left, r.Height);
            }
            foreach (TaskbarButton o in all)
            {
                if (o == b) continue;
                Rectangle inter = Rectangle.Intersect(ext, o.Rect);
                if (inter.Width > 2 && inter.Height > 2) return b.Rect;
            }
            return ext;
        }

        // ---------- 마우스를 올리면 펼치기 ----------

        private bool FlyoutAlive
        {
            get { return _dropFlyout != null && !_dropFlyout.IsDisposed; }
        }

        private bool HoverFlyoutOpen
        {
            get { return FlyoutAlive && _dropFlyout.HoverMode; }
        }

        private bool InsideFlyout(Point pt)
        {
            if (!FlyoutAlive) return false;
            Rectangle fb = _dropFlyout.Bounds;
            fb.Inflate(4, 4);
            return fb.Contains(pt);
        }

        /// <summary>마우스가 움직일 때만 불린다 (주기적 확인 없음).
        ///  - 목록 안 / 그 그룹 아이콘 위: 유지
        ///  - 다른 그룹 아이콘 위: 기존 목록을 지우고 새 그룹 목록을 바로 띄움
        ///  - 그 밖: 0.5초 뒤 닫음 (일반 아이콘은 미리보기·툴팁이 뜨는 순간 닫힘 — OnExplorerPopupShown)</summary>
        private void OnHover(Point pt)
        {
            if (_lDown) return;
            // 전체 화면(동영상 등)일 때는 작업표시줄이 가려져 있으므로 펼치지 않는다
            if (TaskbarUia.FullscreenOn(pt))
            {
                CancelHoverOpen();
                if (HoverFlyoutOpen) _dropFlyout.Close();
                return;
            }
            if ((DateTime.Now - _traysAt).TotalMilliseconds > 1000)
            {
                _trays = TaskbarUia.AllTrays();
                _traysAt = DateTime.Now;
            }

            if (HoverFlyoutOpen && InsideFlyout(pt)) { _leaveTimer.Stop(); return; }

            GroupButton hit = null;
            IntPtr tray = TrayAt(pt);
            if (tray != IntPtr.Zero)
            {
                // 버튼 위치는 배치가 바뀌었을 때만 다시 읽는다
                List<GroupButton> list = ButtonsFor(tray);
                if (list != null)
                    foreach (GroupButton gb in list)
                        if (gb.Rect.Contains(pt)) { hit = gb; break; }
            }

            if (HoverFlyoutOpen)
            {
                if (hit != null && _dropFlyout.GroupId == hit.GroupId)
                {
                    _leaveTimer.Stop();
                    return;
                }
                if (hit != null)
                {
                    // 열린 상태에서 다른 그룹 아이콘으로: 처음 열 때와 같이 잠깐(0.15초) 머물면 바꾼다
                    // (훑고 지나갈 때 팝업이 급하게 번갈아 바뀌지 않게)
                    _leaveTimer.Stop();
                    if (_hoverCandidate == null || _hoverCandidate.GroupId != hit.GroupId)
                    {
                        _hoverCandidate = hit;
                        _hoverSince = DateTime.Now;
                        _hoverOpenTimer.Stop();
                        _hoverOpenTimer.Start();
                    }
                    return;
                }
                if (!_leaveTimer.Enabled) _leaveTimer.Start();
                return;
            }

            // 아직 안 열렸을 때: 그룹 아이콘 위에 잠깐(0.15초) 머물면 연다
            if (hit == null) { CancelHoverOpen(); return; }
            if (FlyoutAlive && _dropFlyout.GroupId == hit.GroupId) { CancelHoverOpen(); return; }
            if (_hoverCandidate == null || _hoverCandidate.GroupId != hit.GroupId)
            {
                _hoverCandidate = hit;
                _hoverSince = DateTime.Now;
                _hoverOpenTimer.Stop();
                _hoverOpenTimer.Start();
            }
        }

        private void OnLeaveTick(object sender, EventArgs e)
        {
            _leaveTimer.Stop();
            if (!HoverFlyoutOpen) return;
            Point c = Cursor.Position;
            if (InsideFlyout(c) || _dropFlyout.AnchorRect.Contains(c)) return;
            _dropFlyout.Close();
        }

        /// <summary>탐색기가 미리보기나 이름 툴팁을 띄웠다 = 마우스가 일반 아이콘 위로 갔다 → 그룹 목록을 즉시 닫는다.
        /// 그룹 아이콘 자신의 이름 툴팁이면 목록과 겹치지 않게 그 툴팁을 닫는다.</summary>
        private void OnExplorerPopupShown(IntPtr hwnd, string cls)
        {
            Point c = Cursor.Position;
            bool onGroup = false;
            IntPtr tray = TrayAt(c);
            if (tray != IntPtr.Zero)
            {
                List<GroupButton> list;
                if (_buttons.TryGetValue(tray, out list) && list != null)
                    foreach (GroupButton gb in list)
                        if (gb.Rect.Contains(c)) { onGroup = true; break; }
            }
            if (cls == "tooltips_class32" && onGroup)
            {
                if (HoverFlyoutOpen) TaskbarUia.PopTaskbarTooltips();
                return;
            }
            if (HoverFlyoutOpen && !InsideFlyout(c) && !onGroup)
            {
                _leaveTimer.Stop();
                _dropFlyout.Close();
            }
        }

        private void CancelHoverOpen()
        {
            _hoverCandidate = null;
            _hoverOpenTimer.Stop();
        }

        private void OnHoverOpenTick(object sender, EventArgs e)
        {
            _hoverOpenTimer.Stop();
            GroupButton c = _hoverCandidate;
            _hoverCandidate = null;
            if (c == null || _lDown || Control.MouseButtons != MouseButtons.None) return;
            Point cur = Cursor.Position;
            if (TaskbarUia.FullscreenOn(cur)) return;
            // 그사이 마우스가 다른 그룹 아이콘으로 옮겨 갔으면 지금 위치로 다시 판단한다
            if (!c.Rect.Contains(cur)) { OnHover(cur); return; }
            // 윈도우 미리보기처럼 한 번에 하나만: 이전 앱의 미리보기가 사라질 때까지 기다렸다가 띄운다
            if (TaskbarUia.ThumbnailVisible() && (DateTime.Now - _hoverSince).TotalMilliseconds < 1500)
            {
                _hoverCandidate = c;
                _hoverOpenTimer.Start();
                return;
            }
            ShowFlyout(c.GroupId, c.Rect, false, true);
            TaskbarUia.PopTaskbarTooltips();
        }

        private void ShowFlyout(string groupId, Rectangle anchor, bool dragMode, bool hoverMode)
        {
            Group g = GroupStore.FindGroup(_cfg, groupId);
            if (g == null) return;
            if (FlyoutAlive)
            {
                if (_dropFlyout.GroupId == groupId) return;
                // 마우스로 펼친 상태에서 다른 그룹 아이콘으로 옮길 때: 창을 닫지 않고 부드럽게 전환
                if (hoverMode && !dragMode && _dropFlyout.HoverMode && !_dropFlyout.DragMode)
                {
                    _dropFlyout.SwitchTo(g, anchor);
                    _leaveTimer.Stop();
                    return;
                }
                _dropFlyout.Close();
            }
            FlyoutForm f = new FlyoutForm(g, anchor, dragMode);
            f.HoverMode = hoverMode;
            f.FormClosed += delegate { if (_dropFlyout == f) _dropFlyout = null; };
            _dropFlyout = f;
            f.Show();
            _leaveTimer.Stop();
        }

        private void UpdateOverlay(List<GroupButton> list, Point pt)
        {
            foreach (GroupButton gb in list)
            {
                if (gb.Rect.Contains(pt))
                {
                    _overlay.ShowAt(gb.GroupId, gb.Rect);
                    return;
                }
            }
            if (_overlay.Visible && !_overlay.Bounds.Contains(pt)) _overlay.HideOverlay();
        }

        // ---------- 오버레이 이벤트 ----------

        private void OnOverlayHover(object sender, EventArgs e)
        {
            string id = _overlay.GroupId;
            Rectangle rect = _overlay.ButtonRect;
            if (id == null) return;
            if (FlyoutAlive && _dropFlyout.GroupId == id)
            {
                _dropFlyout.HoverMode = false;
                _dropFlyout.DragMode = true;
                return;
            }
            ShowFlyout(id, rect, true, false);
        }

        private void OnOverlayDrop(string[] files)
        {
            string id = _overlay.GroupId;
            _overlay.HideOverlay();
            if (id == null) return;
            // 드롭 콜백 안에서 오래 걸리는 일을 하면 끌어 온 쪽(탐색기)이 멈추므로 백그라운드에서 처리
            if (_dropFlyout != null && !_dropFlyout.IsDisposed && _dropFlyout.DragMode) _dropFlyout.Close();
            Background.RunSta(delegate
            {
                int added;
                Group g = GroupStore.InsertEntries(id, files, -1, out added);
                string msg = g != null
                    ? "'" + g.Name + "' 그룹에 " + added + "개를 추가했습니다."
                    : "추가할 수 있는 파일이 없습니다. (폴더는 넣을 수 없습니다)";
                try { _overlay.BeginInvoke(new Action(delegate { ShowTip(msg); })); } catch { }
            });
        }

        private void ShowTip(string text)
        {
            try { _tray.ShowBalloonTip(2500, "몽작업표시줄", text, ToolTipIcon.Info); }
            catch { }
        }

        protected override void ExitThreadCore()
        {
            if (_hook != null) { _hook.Dispose(); _hook = null; }
            if (_showHook != null) { _showHook.Dispose(); _showHook = null; }
            try { _shell.Destroy(); } catch { }
            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            foreach (FileSystemWatcher w in _linkWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            Knowledge.Save();
            try { Placement.Eject(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            if (_dropFlyout != null && !_dropFlyout.IsDisposed) _dropFlyout.Close();
            _overlay.Dispose();
            _uiOwner.Dispose();
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
            base.ExitThreadCore();
        }

        // ---------- 탐색기의 미리보기·툴팁이 뜨는 것을 감지 ----------

        /// <summary>탐색기 프로세스에서 창이 나타날 때(EVENT_OBJECT_SHOW) 알림을 받는다.
        /// 미리보기(TaskListThumbnailWnd)·이름 툴팁이 작업표시줄 근처에 뜬 경우만 넘긴다.</summary>
        private class ShowHook : IDisposable
        {
            private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
            [DllImport("user32.dll")]
            private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
            [DllImport("user32.dll")]
            private static extern bool UnhookWinEvent(IntPtr hook);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
            [DllImport("user32.dll")]
            private static extern bool GetWindowRect(IntPtr h, out RECT r);
            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int L, T, R, B; }

            private const uint EVENT_OBJECT_SHOW = 0x8002;
            private const uint WINEVENT_OUTOFCONTEXT = 0;

            private readonly Action<IntPtr, string> _onShown;
            private readonly WinEventProc _proc;
            private IntPtr _hook = IntPtr.Zero;

            public ShowHook(Action<IntPtr, string> onShown)
            {
                _onShown = onShown;
                _proc = Callback;
                Rehook();
            }

            /// <summary>탐색기가 다시 시작되면 프로세스가 바뀌므로 다시 건다.</summary>
            public void Rehook()
            {
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
                int pid = TaskbarUia.ExplorerPid();
                if (pid == 0) return;
                _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, (uint)pid, 0, WINEVENT_OUTOFCONTEXT);
            }

            private void Callback(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                if (idObject != 0 /* OBJID_WINDOW */ || hwnd == IntPtr.Zero) return;
                try
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
                    GetClassName(hwnd, sb, sb.Capacity);
                    string cls = sb.ToString();
                    if (cls != "TaskListThumbnailWnd" && cls != "tooltips_class32") return;
                    RECT r;
                    if (!GetWindowRect(hwnd, out r)) return;
                    if (!TaskbarUia.NearTaskbar(Rectangle.FromLTRB(r.L, r.T, r.R, r.B))) return;
                    _onShown(hwnd, cls);
                }
                catch { }
            }

            public void Dispose()
            {
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
            }
        }

        // ---------- 새 창이 뜨는 것을 감지 ----------

        private class ShellHookWindow : NativeWindow
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int RegisterWindowMessage(string s);
            [DllImport("user32.dll")]
            private static extern bool RegisterShellHookWindow(IntPtr h);
            [DllImport("user32.dll")]
            private static extern bool DeregisterShellHookWindow(IntPtr h);

            private const int HSHELL_WINDOWCREATED = 1;
            private const int HSHELL_WINDOWDESTROYED = 2;
            private readonly int _msg;
            private readonly int _taskbarCreated;

            public event Action<IntPtr> WindowCreated;
            public event Action WindowDestroyed;
            /// <summary>탐색기(작업표시줄)가 다시 시작됨.</summary>
            public event Action TaskbarCreated;

            public ShellHookWindow()
            {
                CreateHandle(new CreateParams());
                _msg = RegisterWindowMessage("SHELLHOOK");
                _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
                RegisterShellHookWindow(this.Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == _msg)
                {
                    int code = m.WParam.ToInt32() & 0x7FFF;
                    if (code == HSHELL_WINDOWCREATED && WindowCreated != null) WindowCreated(m.LParam);
                    else if (code == HSHELL_WINDOWDESTROYED && WindowDestroyed != null) WindowDestroyed();
                }
                else if (m.Msg == _taskbarCreated && _taskbarCreated != 0)
                {
                    // 탐색기가 다시 시작되면 셸 훅 등록도 풀리므로 다시 등록한다
                    RegisterShellHookWindow(this.Handle);
                    if (TaskbarCreated != null) TaskbarCreated();
                }
                base.WndProc(ref m);
            }

            public void Destroy()
            {
                DeregisterShellHookWindow(this.Handle);
                DestroyHandle();
            }
        }
    }
}
