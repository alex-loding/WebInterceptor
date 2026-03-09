using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// ═════════════════════════════════════════════
// 读取 config.ini
// ═════════════════════════════════════════════
internal static class AppConfig
{
    public static int Port { get; set; } = 8888;
    public static int InstanceCount { get; set; } = 5;

    public static void Load()
    {
        var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        if (!File.Exists(iniPath)) { Save(); return; } // 不存在就生成默认配置

        foreach (var line in File.ReadAllLines(iniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(";") || !trimmed.Contains("=")) continue;
            var parts = trimmed.Split('=', 2);
            var key = parts[0].Trim().ToLower();
            var val = parts[1].Trim();
            if (key == "port" && int.TryParse(val, out var port) && port >= 1 && port <= 65535)
                Port = port;
            if (key == "instance_count" && int.TryParse(val, out var count) && count >= 1 && count <= 20)
                InstanceCount = count;
        }
    }

    // 保存配置到 config.ini
    public static void Save()
    {
        var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        File.WriteAllText(iniPath,
            "; WebInterceptor 配置文件\n" +
            "; 修改后重启程序生效\n\n" +
            "[server]\n" +
            $"port = {Port}\n\n" +
            "[webview2]\n" +
            $"instance_count = {InstanceCount}\n");
    }
}

internal static class Program
{
    [STAThread]
    static void Main()
    {
        AppConfig.Load();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

record InterceptRequest(string Url, string Filter, int? timeout_seconds = null, bool? keep_page = null, bool? wait_for_complete = null, bool? include_body = null, string? instances = null, bool? include_headers = null, int? collect_delay_seconds = null);
record InterceptedChunk(string RequestUrl, string? Body = null, Dictionary<string, string>? Headers = null, Dictionary<string, string>? Cookies = null);

public class MainForm : Form
{
    private static readonly JsonSerializerOptions _requestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private static void HandleNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private static void ConfigureWebView2(WebView2 webView)
    {
        if (webView.CoreWebView2 == null) return;

        webView.CoreWebView2.Settings.UserAgent = DefaultUserAgent;
        webView.CoreWebView2.Settings.IsReputationCheckingRequired = false;
        webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
        webView.CoreWebView2.NewWindowRequested -= HandleNewWindowRequested;
        webView.CoreWebView2.NewWindowRequested += HandleNewWindowRequested;
    }

    private static InterceptRequest? TryDeserializeRequest(string rawBody)
    {
        try
        {
            return JsonSerializer.Deserialize<InterceptRequest>(rawBody, _requestJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SanitizeRequestBody(string rawBody)
    {
        return rawBody.Replace("`", "")
                      .Replace("\n", "")
                      .Replace("\r", "")
                      .Replace("  ", " ");
    }

    private HttpListener? _httpListener;
    private CancellationTokenSource? _httpServerCts;
    private Task? _httpServerLoopTask;
    private readonly object _httpServerLock = new();
    private RichTextBox _logBox = null!;
    private NotifyIcon _trayIcon = null!;
    private TabControl _tabControl = null!;
    private bool _realExit = false;

    // 会话数据结构
    record SessionData(System.Threading.Channels.Channel<string> Channel, bool IncludeBody, bool IncludeHeaders);
    record CollectionData(List<InterceptedChunk> Items, bool IncludeBody, bool IncludeHeaders);

    private readonly ConcurrentDictionary<(int, string), SessionData> _sessions = new();
    private int _runningTasks = 0;
    private int _waitingTasks = 0;

    // ── 请求统计（功能B）──
    private long _statTotalRequests = 0;
    private long _statSuccess = 0;
    private long _statFailed = 0;
    private long _statTimeout = 0;
    private long _statTotalMs = 0;      // 累计响应时间（ms），用于计算平均值
    // 每实例状态：0=空闲, 1=繁忙, 负数=等待队列长度
    private readonly ConcurrentDictionary<int, string> _instanceStatus = new();
    // 统计面板 UI 引用（在 BuildStatsPanel 中赋值）
    private Label? _statLabelTotal, _statLabelSuccess, _statLabelFailed,
                   _statLabelTimeout, _statLabelAvgMs;
    private FlowLayoutPanel? _instanceStatusPanel;
    private System.Windows.Forms.Timer? _statsRefreshTimer;

    // WebView2实例池
    private readonly ConcurrentQueue<(WebView2, int)> _webViewQueue = new(); // 用于公平分配的队列
    private readonly List<(WebView2, int)> _allInstances = new(); // 存储所有实例，用于调整池大小时使用
    private readonly object _instancesLock = new(); // 保护_allInstances的锁
    private SemaphoreSlim _poolSemaphore = new(AppConfig.InstanceCount, AppConfig.InstanceCount); // 初始值与配置一致
    private readonly object _poolInitLock = new();
    private bool _poolInitialized = false;
    private int _nextInstanceId = 1;
    // 记录临时实例ID，归还时走销毁路径而非重新入队
    private readonly ConcurrentDictionary<int, bool> _tempInstanceIds = new();

    // 每个 instanceId 对应一个 FIFO 等待队列，保证同一实例的请求按到达顺序处理
    private readonly ConcurrentDictionary<int, Queue<TaskCompletionSource<(WebView2, int)>>> _instanceWaiters = new();
    private readonly ConcurrentDictionary<int, object> _instanceWaitersLock = new();



    // 请求限流机制
    private readonly SemaphoreSlim _requestSemaphore = new(10, 10); // 最多10个并发请求

    public MainForm()
    {
        // ── 深色主题配色（VS Code Dark+）──
        var clrBg          = Color.FromArgb(30,  30,  30);
        var clrPanel       = Color.FromArgb(37,  37,  38);
        var clrBorder      = Color.FromArgb(60,  60,  60);
        var clrAccent      = Color.FromArgb(0,  122, 204);
        var clrAccentHover = Color.FromArgb(28, 151, 234);
        var clrText        = Color.FromArgb(212, 212, 212);
        var clrTextDim     = Color.FromArgb(130, 130, 130);
        var clrInput       = Color.FromArgb(58,  58,  58);
        var fontUI         = new Font("微软雅黑", 9f);
        var fontMono       = new Font("Cascadia Code", 9.5f);
        if (fontMono.Name != "Cascadia Code") { fontMono.Dispose(); fontMono = new Font("Consolas", 9.5f); }

        Text            = "WebInterceptor";
        Width           = 1440;
        Height          = 900;
        MinimumSize     = new Size(900, 600);
        BackColor       = clrBg;
        ForeColor       = clrText;

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);

        // ══ 顶部工具栏 ══════════════════════════════
        var toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 48,
            BackColor = clrPanel,
            Padding   = new Padding(10, 0, 10, 0),
        };
        toolbar.Paint += (s, e) =>
        {
            using var pen = new Pen(clrBorder, 1);
            e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };

        var titleLabel = new Label
        {
            Text      = "⬡  WebInterceptor",
            ForeColor = clrAccent,
            Font      = new Font("微软雅黑", 11f, FontStyle.Bold),
            Location  = new Point(12, 0),
            AutoSize  = false,
            Size      = new Size(200, 48),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var instanceLabel = new Label
        {
            Text      = "实例数",
            ForeColor = clrTextDim,
            Font      = fontUI,
            Location  = new Point(218, 0),
            AutoSize  = false,
            Size      = new Size(48, 48),
            TextAlign = ContentAlignment.MiddleRight,
        };
        var instanceComboBox = new ComboBox
        {
            Location      = new Point(272, 12),
            Width         = 68,
            Font          = fontUI,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = clrInput,
            ForeColor     = clrText,
        };
        for (int i = 1; i <= 20; i++) instanceComboBox.Items.Add(i);
        int defaultInstanceCount = AppConfig.InstanceCount;
        instanceComboBox.SelectedIndex = (defaultInstanceCount >= 1 && defaultInstanceCount <= 20)
            ? defaultInstanceCount - 1 : 4;

        var portLabel = new Label
        {
            Text      = "端口",
            ForeColor = clrTextDim,
            Font      = fontUI,
            Location  = new Point(356, 0),
            AutoSize  = false,
            Size      = new Size(36, 48),
            TextAlign = ContentAlignment.MiddleRight,
        };
        var portTextBox = new TextBox
        {
            Location    = new Point(398, 13),
            Width       = 72,
            Font        = fontUI,
            Text        = AppConfig.Port.ToString(),
            BackColor   = clrInput,
            ForeColor   = clrText,
            BorderStyle = BorderStyle.FixedSingle,
        };

        // 应用按钮
        var applyBtn = new Button
        {
            Text      = "应用",
            Location  = new Point(482, 11),
            Size      = new Size(64, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = clrAccent,
            Font      = fontUI,
            Cursor    = Cursors.Hand,
        };
        applyBtn.FlatAppearance.BorderSize            = 0;
        applyBtn.FlatAppearance.MouseOverBackColor    = clrAccentHover;
        applyBtn.FlatAppearance.MouseDownBackColor    = Color.FromArgb(0, 100, 180);
        
        applyBtn.Click += async (_, _) =>
        {
            try
            {
                string? selectedText = instanceComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    if (string.IsNullOrWhiteSpace(portTextBox.Text))
                    {
                        Log("配置更新失败：端口不能为空", LogLevel.Error);
                        return;
                    }

                    if (!int.TryParse(portTextBox.Text.Trim(), out int port) || !IsValidPort(port))
                    {
                        Log("配置更新失败：端口必须是 1-65535 的整数", LogLevel.Error);
                        return;
                    }

                    int instanceCount = int.Parse(selectedText);
                    await UpdateInstancePoolSize(instanceCount);
                    int oldPort = AppConfig.Port;
                    if (port != oldPort)
                    {
                        if (!RestartHttpServer(port, out var restartError))
                        {
                            Log($"HTTP 服务重启失败，保持端口 {oldPort}: {restartError}", LogLevel.Error);
                            AppConfig.Port = oldPort;
                            return;
                        }
                        AppConfig.Port = port;
                        Log($"HTTP 服务已重启  →  端口 {port}", LogLevel.Success);
                    }

                    AppConfig.InstanceCount = instanceCount;
                    AppConfig.Save();
                    Log($"配置已保存  实例数: {instanceCount}  端口: {AppConfig.Port}", LogLevel.Success);
                }
                else
                {
                    Log("配置更新失败：请选择实例数", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                Log($"配置更新失败：{ex.Message}", LogLevel.Error);
            }
        };

        toolbar.Controls.AddRange(new Control[]
        {
            titleLabel, instanceLabel, instanceComboBox,
            portLabel, portTextBox, applyBtn,
        });

        // ══ 统计面板（功能B）══════════════════════════════
        var statsPanel = BuildStatsPanel(clrPanel, clrBorder, clrText, clrTextDim, clrAccent, fontUI);

        // ══ 主体布局（标签页 / 日志 上下分割）══════
        var splitContainer = new SplitContainer
        {
            Dock             = DockStyle.Fill,
            Orientation      = Orientation.Horizontal,
            SplitterDistance = 560,
            Panel1MinSize    = 150,
            Panel2MinSize    = 140,
            BackColor        = clrBorder,   // 分割线颜色
        };
        splitContainer.Panel1.BackColor = clrBg;
        splitContainer.Panel2.BackColor = clrBg;

        // ── 标签页（WebView2 实例）──
        _tabControl = new TabControl
        {
            Dock          = DockStyle.Fill,
            Multiline     = false,
            Alignment     = TabAlignment.Top,
            Appearance    = TabAppearance.Normal,
            DrawMode      = TabDrawMode.OwnerDrawFixed,
            ItemSize      = new Size(110, 28),
            BackColor     = clrBg,
            Padding       = new Point(12, 4),
        };
        // 自绘标签页标题，实现深色风格
        _tabControl.DrawItem += (s, e) =>
        {
            var tab      = _tabControl.TabPages[e.Index];
            bool active  = e.Index == _tabControl.SelectedIndex;
            var bgColor  = active ? clrBg : Color.FromArgb(45, 45, 48);
            var txtColor = active ? clrText : clrTextDim;
            using var brush = new SolidBrush(bgColor);
            e.Graphics.FillRectangle(brush, e.Bounds);
            if (active)
            {
                using var accentPen = new Pen(clrAccent, 2);
                e.Graphics.DrawLine(accentPen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
            }
            TextRenderer.DrawText(e.Graphics, tab.Text, fontUI, e.Bounds, txtColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        splitContainer.Panel1.Controls.Add(_tabControl);

        // ── 日志面板 ──
        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = clrBg };

        // 日志头部工具栏
        var logHeader = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 34,
            BackColor = clrPanel,
        };
        logHeader.Paint += (s, e) =>
        {
            using var pen = new Pen(clrBorder, 1);
            e.Graphics.DrawLine(pen, 0, logHeader.Height - 1, logHeader.Width, logHeader.Height - 1);
        };

        var logTitle = new Label
        {
            Text      = "OUTPUT",
            ForeColor = clrTextDim,
            Font      = new Font("微软雅黑", 8.5f, FontStyle.Bold),
            Dock      = DockStyle.Left,
            Padding   = new Padding(14, 0, 0, 0),
            AutoSize  = false,
            Width     = 120,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var clearBtn = new Button
        {
            Text      = "清空",
            Dock      = DockStyle.Right,
            Width     = 56,
            FlatStyle = FlatStyle.Flat,
            ForeColor = clrTextDim,
            BackColor = Color.Transparent,
            Font      = fontUI,
            Cursor    = Cursors.Hand,
        };
        clearBtn.FlatAppearance.BorderSize            = 0;
        clearBtn.FlatAppearance.MouseOverBackColor    = Color.FromArgb(55, 55, 55);
        clearBtn.FlatAppearance.MouseDownBackColor    = Color.FromArgb(70, 70, 70);
        clearBtn.Click += (_, _) => _logBox.Clear();

        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(clearBtn);

        _logBox = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            BackColor   = Color.FromArgb(22, 22, 22),
            ForeColor   = clrText,
            Font        = fontMono,
            ReadOnly    = true,
            WordWrap    = false,
            ScrollBars  = RichTextBoxScrollBars.Both,
            BorderStyle = BorderStyle.None,
            Padding     = new Padding(6, 4, 0, 0),
        };

        logPanel.Controls.Add(_logBox);
        logPanel.Controls.Add(logHeader);
        splitContainer.Panel2.Controls.Add(logPanel);

        Controls.Add(splitContainer);
        Controls.Add(statsPanel);
        Controls.Add(toolbar);

        Load += async (_, _) =>
        {
            await InitWebView2PoolAsync();
            if (!StartHttpServer(AppConfig.Port, out var serverError))
            {
                Log($"HTTP 服务启动失败: {serverError}", LogLevel.Error);
                MessageBox.Show(
                    $"HTTP 服务启动失败，当前端口 {AppConfig.Port} 无法监听。\n{serverError}",
                    "WebInterceptor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            InitTray();
            // 启动统计刷新定时器（每秒刷新一次）
            _statsRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statsRefreshTimer.Tick += (_, _) => RefreshStatsUI();
            _statsRefreshTimer.Start();
        };

        // 点关闭按钮 → 隐藏到托盘，不真正退出
        FormClosing += (_, e) =>
        {
            if (!_realExit)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.ShowBalloonTip(1500, "WebInterceptor", "程序已最小化到托盘，右键托盘图标可退出", ToolTipIcon.Info);
            }
        };

        FormClosed += (_, _) =>
        {
            _trayIcon?.Dispose();
            StopHttpServer();
            _requestSemaphore?.Dispose();
            _poolSemaphore?.Dispose();
            
            // 清理WebView2实例
            lock (_instancesLock)
            {
                foreach (var (webView, _) in _allInstances)
                {
                    try
                    {
                        webView.Dispose();
                    }
                    catch { }
                }
                _allInstances.Clear();
            }
            
            // 清空队列
            while (_webViewQueue.TryDequeue(out _)) { }
        };
    }

    // ── 初始化系统托盘 ──
    private void InitTray()
    {
        // 尝试使用外部图标文件
        Icon icon = SystemIcons.Application;
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
        {
            icon = new Icon(iconPath);
        }

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "WebInterceptor 运行中",
            Visible = true,
        };

        // 右键菜单
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _realExit = true;
            Application.Exit();
        };

        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;

        // 单击托盘图标 → 显示/隐藏窗口
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Visible)
                    Hide();
                else
                    ShowMainWindow();
            }
        };
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ── 日志级别 ──
    private enum LogLevel { Info, Success, Warning, Error, Request, Navigate, Intercept, Data, Debug }

    // ── 更新程序标题（显示任务计数） ──
    private void UpdateWindowTitle()
    {
        if (InvokeRequired) { BeginInvoke(UpdateWindowTitle); return; }
        Text = (_runningTasks > 0 || _waitingTasks > 0)
            ? $"WebInterceptor  ·  运行 {_runningTasks}  等待 {_waitingTasks}"
            : "WebInterceptor";
    }

    // ── 构建统计面板（功能B）──
    private Panel BuildStatsPanel(Color clrPanel, Color clrBorder, Color clrText, Color clrTextDim, Color clrAccent, Font fontUI)
    {
        var panel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 68,
            BackColor = clrPanel,
            Padding   = new Padding(14, 0, 14, 0),
        };
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(clrBorder, 1);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
        };

        // 左侧：全局数字统计
        static Label MakeLbl(string txt, Color fg, Font f) => new Label
        {
            Text = txt, ForeColor = fg, Font = f,
            AutoSize = false, Height = 68,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var lblTitle = MakeLbl("STATS", clrTextDim, new Font("微软雅黑", 8f, FontStyle.Bold));
        lblTitle.Width = 46;

        _statLabelTotal   = MakeLbl("总计 —", clrTextDim,                         fontUI); _statLabelTotal.Width   = 90;
        _statLabelSuccess = MakeLbl("成功 —", Color.FromArgb(78,  201, 176),       fontUI); _statLabelSuccess.Width = 90;
        _statLabelFailed  = MakeLbl("失败 —", Color.FromArgb(241,  76,  76),       fontUI); _statLabelFailed.Width  = 90;
        _statLabelTimeout = MakeLbl("超时 —", Color.FromArgb(220, 166,  60),       fontUI); _statLabelTimeout.Width = 90;
        _statLabelAvgMs   = MakeLbl("均耗 —", Color.FromArgb( 86, 156, 214),       fontUI); _statLabelAvgMs.Width   = 110;

        // 分隔线
        var sep = new Label { Width = 1, Height = 40, BackColor = clrBorder, Margin = new Padding(8, 14, 8, 14) };

        // 右侧：实例状态指示器（FlowLayoutPanel，动态生成）
        _instanceStatusPanel = new FlowLayoutPanel
        {
            Dock        = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = true,
            BackColor   = Color.Transparent,
            AutoScroll  = true,
        };

        // 用 TableLayoutPanel 把左右两侧分开
        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            BackColor   = Color.Transparent,
            ColumnCount = 9,
            RowCount    = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));   // STATS 标题
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // 总计
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // 成功
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // 失败
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // 超时
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));  // 均耗
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 17));   // 分隔
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));   // "实例状态" 标签
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));  // 实例指示器

        var instLbl = MakeLbl("实例状态", clrTextDim, new Font("微软雅黑", 8f, FontStyle.Bold));

        layout.Controls.Add(lblTitle,             0, 0);
        layout.Controls.Add(_statLabelTotal,      1, 0);
        layout.Controls.Add(_statLabelSuccess,    2, 0);
        layout.Controls.Add(_statLabelFailed,     3, 0);
        layout.Controls.Add(_statLabelTimeout,    4, 0);
        layout.Controls.Add(_statLabelAvgMs,      5, 0);
        layout.Controls.Add(sep,                  6, 0);
        layout.Controls.Add(instLbl,              7, 0);
        layout.Controls.Add(_instanceStatusPanel, 8, 0);

        panel.Controls.Add(layout);
        return panel;
    }

    // ── 刷新统计 UI（每秒由 Timer 调用）──
    // ── 颜色常量（供 dot 更新复用）──
    private static readonly Color _clrIdle    = Color.FromArgb( 78, 201, 176);
    private static readonly Color _clrBusy    = Color.FromArgb( 86, 156, 214);
    private static readonly Color _clrCrashed = Color.FromArgb(241,  76,  76);
    private static readonly Font  _dotFont    = new Font("微软雅黑", 7.5f, FontStyle.Bold);

    // 每个实例对应一个持久 dot Panel，不再每秒重建
    private readonly ConcurrentDictionary<int, Panel> _instanceDots = new();

    private void RefreshStatsUI()
    {
        if (InvokeRequired) { BeginInvoke(RefreshStatsUI); return; }

        // ── 数字统计更新（极轻量）──
        long total   = Interlocked.Read(ref _statTotalRequests);
        long success = Interlocked.Read(ref _statSuccess);
        long failed  = Interlocked.Read(ref _statFailed);
        long timeout = Interlocked.Read(ref _statTimeout);
        long totalMs = Interlocked.Read(ref _statTotalMs);
        long finished = success + failed + timeout;
        double avgMs  = finished > 0 ? (double)totalMs / finished : 0;

        if (_statLabelTotal   != null) _statLabelTotal.Text   = $"总计 {total}";
        if (_statLabelSuccess != null) _statLabelSuccess.Text = $"成功 {success}";
        if (_statLabelFailed  != null) _statLabelFailed.Text  = $"失败 {failed}";
        if (_statLabelTimeout != null) _statLabelTimeout.Text = $"超时 {timeout}";
        if (_statLabelAvgMs   != null) _statLabelAvgMs.Text   = $"均耗 {avgMs:F0}ms";

        if (_instanceStatusPanel == null) return;

        // ── 实例 dot 更新：只改颜色/文字，不重建控件 ──
        List<(WebView2, int)> snapshot;
        lock (_instancesLock) { snapshot = _allInstances.ToList(); }

        // 如果实例集合有变化（新增/删除），才重建 dot 控件列表
        var snapshotIds = snapshot.Select(x => x.Item2).ToHashSet();
        bool needRebuild = !snapshotIds.SetEquals(_instanceDots.Keys);

        if (needRebuild)
        {
            _instanceStatusPanel.SuspendLayout();
            _instanceStatusPanel.Controls.Clear();
            // 移除不再存在的 dot
            foreach (var id in _instanceDots.Keys.Except(snapshotIds).ToList())
            {
                if (_instanceDots.TryRemove(id, out var old)) old.Dispose();
            }
            // 为新实例创建 dot
            foreach (var (_, id) in snapshot)
            {
                if (!_instanceDots.ContainsKey(id))
                {
                    var dot = new Panel
                    {
                        Width     = 32,
                        Height    = 26,
                        BackColor = _clrIdle,
                        Margin    = new Padding(0, 0, 6, 0),
                        Tag       = id,
                    };
                    dot.Paint += DotPaint;
                    _instanceDots[id] = dot;
                }
                _instanceStatusPanel.Controls.Add(_instanceDots[id]);
            }
            _instanceStatusPanel.ResumeLayout();
        }

        // 每次刷新只更新颜色和宽度（不创建任何新对象）
        foreach (var (_, id) in snapshot)
        {
            if (!_instanceDots.TryGetValue(id, out var dot)) continue;
            _instanceStatus.TryGetValue(id, out var status);
            int waitCount = 0;
            if (_instanceWaiters.TryGetValue(id, out var q))
            {
                var wl = _instanceWaitersLock.GetOrAdd(id, _ => new object());
                lock (wl) { waitCount = q.Count; }
            }
            var newColor = status switch
            {
                "busy"    => _clrBusy,
                "crashed" => _clrCrashed,
                _         => _clrIdle,
            };
            int newWidth = waitCount > 0 ? 46 : 32;
            bool changed = dot.BackColor != newColor || dot.Width != newWidth;
            if (changed)
            {
                dot.BackColor = newColor;
                dot.Width     = newWidth;
                dot.Invalidate(); // 触发重绘文字
            }
        }
    }

    private void DotPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel dot || dot.Tag is not int id) return;
        _instanceStatus.TryGetValue(id, out var status);
        int waitCount = 0;
        if (_instanceWaiters.TryGetValue(id, out var q))
        {
            var wl = _instanceWaitersLock.GetOrAdd(id, _ => new object());
            lock (wl) { waitCount = q.Count; }
        }
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(20, 20, 20));
        var txt = waitCount > 0 ? $"#{id}+{waitCount}" : $"#{id}";
        e.Graphics.DrawString(txt, _dotFont, brush,
            new RectangleF(0, 0, dot.Width, dot.Height),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    // ── 初始化时为实例创建标签页（在 init 阶段的 UI 线程，不在请求热路径）──
    private void CreateTabForInstance(WebView2 webView, int instanceId)
    {
        // 已存在就不重复创建
        var existing = _tabControl.TabPages.Cast<TabPage>()
            .FirstOrDefault(tp => tp.Tag?.ToString() == instanceId.ToString());
        if (existing != null) return;

        var tabPage = new TabPage { Text = $"实例 #{instanceId}", Tag = instanceId, Padding = new Padding(0) };

        var splitContainer = new SplitContainer
        {
            Dock             = DockStyle.Fill,
            Orientation      = Orientation.Horizontal,
            SplitterDistance = 35,
            Panel1MinSize    = 35,
            Panel2MinSize    = 100,
            BackColor        = Color.FromArgb(30, 30, 30),
            FixedPanel       = FixedPanel.Panel1,
        };
        splitContainer.Panel1.BackColor = Color.FromArgb(37, 37, 38);
        splitContainer.Panel2.BackColor = Color.FromArgb(30, 30, 30);

        var toolbar = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.FromArgb(37, 37, 38),
            Padding   = new Padding(10, 0, 10, 0),
        };

        var backBtn    = MakeToolbarButton("←", 0);
        var fwdBtn     = MakeToolbarButton("→", 45);
        var reloadBtn  = MakeToolbarButton("⟳", 90);
        var urlBox     = new TextBox
        {
            BackColor   = Color.FromArgb(58, 58, 58),
            ForeColor   = Color.FromArgb(212, 212, 212),
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("微软雅黑", 11f),
        };
        var goBtn = new Button
        {
            Text      = "前往",
            Size      = new Size(60, 35),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            Font      = new Font("微软雅黑", 11f, FontStyle.Bold),
        };
        goBtn.FlatAppearance.BorderSize = 0;

        backBtn.Click   += (_, _) => { if (webView.CoreWebView2?.CanGoBack   == true) webView.CoreWebView2.GoBack(); };
        fwdBtn.Click    += (_, _) => { if (webView.CoreWebView2?.CanGoForward == true) webView.CoreWebView2.GoForward(); };
        reloadBtn.Click += (_, _) => { webView.CoreWebView2?.Reload(); };

        void Navigate()
        {
            var url = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
            webView.CoreWebView2?.Navigate(url);
        }
        urlBox.KeyPress += (_, e) => { if (e.KeyChar == (char)Keys.Enter) Navigate(); };
        goBtn.Click     += (_, _) => Navigate();

        void UpdateLayout()
        {
            const int bw = 40, bh = 35, sp = 5, gbw = 60, mg = 10;
            backBtn.Location   = new Point(mg, 0);
            fwdBtn.Location    = new Point(mg + bw + sp, 0);
            reloadBtn.Location = new Point(mg + (bw + sp) * 2, 0);
            goBtn.Location     = new Point(toolbar.Width - mg - gbw, 0);
            int urlLeft = mg + (bw + sp) * 3;
            urlBox.Location = new Point(urlLeft, 0);
            urlBox.Size     = new Size(goBtn.Left - urlLeft - sp, bh);
        }
        toolbar.SizeChanged += (_, _) => UpdateLayout();

        webView.CoreWebView2.NavigationStarting  += (_, e) => { if (urlBox?.IsDisposed == false && e.Uri != null) urlBox.Text = e.Uri; };
        webView.CoreWebView2.NavigationCompleted += (_, _) => { if (urlBox?.IsDisposed == false) urlBox.Text = webView.CoreWebView2?.Source ?? ""; };
        webView.CoreWebView2.HistoryChanged      += (_, _) => { if (urlBox?.IsDisposed == false) urlBox.Text = webView.CoreWebView2?.Source ?? ""; };

        toolbar.Controls.AddRange(new Control[] { backBtn, fwdBtn, reloadBtn, urlBox, goBtn });
        splitContainer.Panel1.Controls.Add(toolbar);
        splitContainer.Panel2.Padding = new Padding(0);
        webView.Dock  = DockStyle.Fill;
        webView.Margin = new Padding(0);
        splitContainer.Panel2.Controls.Add(webView);
        tabPage.Controls.Add(splitContainer);
        _tabControl.TabPages.Add(tabPage);
        UpdateLayout();
    }

    private Button MakeToolbarButton(string text, int x)
    {
        var btn = new Button
        {
            Text      = text,
            Size      = new Size(40, 35),
            Location  = new Point(x, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 58, 58),
            ForeColor = Color.FromArgb(212, 212, 212),
            Font      = new Font("微软雅黑", 12f, FontStyle.Bold),
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    // 在控件树中查找指定控件引用，避免仅按 instanceId 误匹配
    private static bool ControlTreeContains(Control root, Control target)
    {
        if (ReferenceEquals(root, target)) return true;
        foreach (Control child in root.Controls)
        {
            if (ControlTreeContains(child, target)) return true;
        }
        return false;
    }

    // 仅删除“实例号 + WebView 引用”都匹配的标签页，避免删除重建后的新实例标签
    private void RemoveTabForInstanceWebView(WebView2 webView, int instanceId)
    {
        var tabPage = _tabControl.TabPages.Cast<TabPage>()
            .FirstOrDefault(tp =>
                tp.Tag?.ToString() == instanceId.ToString() &&
                ControlTreeContains(tp, webView));

        if (tabPage != null)
        {
            _tabControl.TabPages.Remove(tabPage);
            tabPage.Dispose();
        }
    }

    // 仅从实例列表中移除“实例号 + WebView 引用”都匹配的条目
    private void RemoveExactInstance(WebView2? webView, int instanceId)
    {
        if (webView == null) return;
        lock (_instancesLock)
        {
            _allInstances.RemoveAll(i => i.Item2 == instanceId && ReferenceEquals(i.Item1, webView));
        }
    }

    // 统一安全解绑导航完成事件，避免超时分支遗漏导致事件残留
    private static void SafeUnsubscribeNavigationCompleted(
        WebView2 webView,
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler)
    {
        if (handler == null) return;

        void RemoveHandler()
        {
            try
            {
                if (!webView.IsDisposed && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.NavigationCompleted -= handler;
                }
            }
            catch { }
        }

        try
        {
            if (webView.IsDisposed) return;
            if (webView.InvokeRequired) webView.Invoke((Action)RemoveHandler);
            else RemoveHandler();
        }
        catch { }
    }

    // ── 请求获取实例时仅激活对应标签页（超轻量）──
    private void ActivateTabForInstance(int instanceId)
    {
        var tab = _tabControl.TabPages.Cast<TabPage>()
            .FirstOrDefault(tp => tp.Tag?.ToString() == instanceId.ToString());
        if (tab != null) _tabControl.SelectedTab = tab;
    }

    // ── 日志队列，用于批量处理日志，避免频繁UI调用 ──
    private readonly ConcurrentQueue<(string timeStr, string msgStr, Color msgColor)> _logQueue = new();
    private System.Windows.Forms.Timer? _logFlushTimer;
    private readonly object _logFlushTimerLock = new();
    private bool _logFlushRunning = false;
    private const int MaxLogLines = 5000;
    private const int LogTrimToLines = 4500;

    private void EnsureLogFlushTimerStarted()
    {
        if (_logFlushTimer != null) return;

        lock (_logFlushTimerLock)
        {
            if (_logFlushTimer == null)
            {
                _logFlushTimer = new System.Windows.Forms.Timer { Interval = 50 };
                _logFlushTimer.Tick += FlushLogQueue;
                _logFlushTimer.Start();
            }
        }
    }

    // ── 日志输出（线程安全，使用队列批量处理）──
    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        Color c = level switch
        {
            LogLevel.Success   => Color.FromArgb( 78, 201, 176),
            LogLevel.Warning   => Color.FromArgb(220, 166,  60),
            LogLevel.Error     => Color.FromArgb(241,  76,  76),
            LogLevel.Request   => Color.FromArgb( 86, 156, 214),
            LogLevel.Navigate  => Color.FromArgb( 79, 193, 255),
            LogLevel.Intercept => Color.FromArgb(197, 134, 192),
            LogLevel.Data      => Color.FromArgb( 78, 201, 176),
            LogLevel.Debug     => Color.FromArgb( 90,  90,  90),
            _                  => Color.FromArgb(180, 180, 180),
        };
        var time = DateTime.Now.ToString("HH:mm:ss.fff");
        var tStr = $"[{time}]";
        var mStr = $"  {message}\n";
        _logQueue.Enqueue((tStr, mStr, c));

        EnsureLogFlushTimerStarted();
    }

    // ── 批量刷新日志队列到UI ──
    private void FlushLogQueue(object? sender, EventArgs e)
    {
        if (_logFlushRunning) return;
        _logFlushRunning = true;
        
        try
        {
            int count = 0;
            const int maxPerFlush = 50;
            
            while (count < maxPerFlush && _logQueue.TryDequeue(out var item))
            {
                AppendLog(item.timeStr, item.msgStr, item.msgColor);
                count++;
            }
        }
        finally
        {
            _logFlushRunning = false;
        }
    }

    // 兼容旧的 Color 重载
    private void Log(string message, Color color)
    {
        var time = DateTime.Now.ToString("HH:mm:ss.fff");
        var tStr = $"[{time}]";
        var mStr = $"  {message}\n";
        _logQueue.Enqueue((tStr, mStr, color));

        EnsureLogFlushTimerStarted();
    }

    private void AppendLog(string timeStr, string msgStr, Color msgColor)
    {
        var dimGray = Color.FromArgb(75, 75, 75);
        _logBox.SelectionStart  = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor  = dimGray;
        _logBox.AppendText(timeStr);
        _logBox.SelectionColor  = msgColor;
        _logBox.AppendText(msgStr);
        TrimLogIfNeeded();
        _logBox.ScrollToCaret();
    }

    private void TrimLogIfNeeded()
    {
        int lineCount = _logBox.GetLineFromCharIndex(_logBox.TextLength) + 1;
        if (lineCount <= MaxLogLines) return;

        int removeLineCount = lineCount - LogTrimToLines;
        int removeChars = _logBox.GetFirstCharIndexFromLine(removeLineCount);
        if (removeChars <= 0) return;

        _logBox.Select(0, removeChars);
        _logBox.SelectedText = string.Empty;
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
    }

    // ── 初始化WebView2实例池 ──
    private async Task InitWebView2PoolAsync()
    {
        await UpdateInstancePoolSize(AppConfig.InstanceCount); // 从配置读取实例数
    }

    // ── 更新WebView2实例池大小 ──
    private async Task UpdateInstancePoolSize(int targetSize)
    {
        Log($"WebView2 实例池调整中  →  目标数: {targetSize}", LogLevel.Warning);

        // ── 修复2+3：先等待所有正在使用的实例归还，再销毁旧实例 ──
        // 获取当前 semaphore 的实际容量（即上一次设定的实例数）
        // 通过连续 WaitAsync 把所有槽都占满，意味着此时没有任何请求在用实例
        int oldCapacity;
        lock (_instancesLock)
        {
            oldCapacity = _allInstances.Count;
        }

        // 用一个新的 semaphore 替换旧的，旧的正在等待的 WaitAsync 会收到 ObjectDisposedException
        // 为了安全替换：先创建新 semaphore（初始=0，暂时阻止新请求），等旧请求全部完成再开放
        var oldSemaphore = _poolSemaphore;
        var newSemaphore = new SemaphoreSlim(0, targetSize); // 初始0：暂时阻止新请求
        _poolSemaphore = newSemaphore;

        // 等旧 semaphore 的所有槽都被 Release 回来（即所有正在用实例的请求都已归还）
        // 最多等 120 秒
        if (oldCapacity > 0)
        {
            Log($"等待 {oldCapacity} 个实例槽归还中...", LogLevel.Warning);
            var drainTasks = Enumerable.Range(0, oldCapacity)
                .Select(_ => oldSemaphore.WaitAsync(TimeSpan.FromSeconds(120)));
            var results = await Task.WhenAll(drainTasks);
            if (results.Any(r => !r))
            {
                Log("部分实例槽等待超时，强制继续", LogLevel.Warning);
            }
        }
        oldSemaphore.Dispose();

        // 清空队列（此时已确保没有请求在使用实例）
        while (_webViewQueue.TryDequeue(out _)) { }

        // 取消所有 FIFO 等待者（池重建，旧实例即将销毁）
        foreach (var kv in _instanceWaiters)
        {
            lock (_instanceWaitersLock.GetOrAdd(kv.Key, _ => new object()))
            {
                foreach (var tcs in kv.Value) tcs.TrySetCanceled();
                kv.Value.Clear();
            }
        }
        _instanceWaiters.Clear();

        // 销毁旧实例
        List<(WebView2, int)> existingInstances;
        lock (_instancesLock)
        {
            existingInstances = _allInstances.ToList();
            _allInstances.Clear();
        }

        foreach (var (webView, instanceId) in existingInstances)
        {
            try
            {
                if (!webView.IsDisposed && webView.InvokeRequired)
                {
                    webView.Invoke((Action)(() =>
                    {
                        RemoveTabForInstanceWebView(webView, instanceId);
                        webView.Dispose();
                    }));
                }
                else
                {
                    RemoveTabForInstanceWebView(webView, instanceId);
                    if (!webView.IsDisposed)
                        webView.Dispose();
                }
                Log($"实例 #{instanceId} 已销毁", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Log($"实例 #{instanceId} 销毁失败: {ex.Message}", LogLevel.Warning);
            }
        }

        // 重新创建实例
        _nextInstanceId = 1;
        List<(WebView2, int)> newInstances = new();
        for (int i = 0; i < targetSize; i++)
        {
            try
            {
                var webView = new WebView2();
                var instanceId = _nextInstanceId++;
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebInterceptor", $"WebView2Profile_{instanceId}");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // 配置WebView2
                ConfigureWebView2(webView);

                RegisterWebResourceEvent(webView, instanceId);

                var instance = (webView, instanceId);
                newInstances.Add(instance);
                _instanceStatus[instanceId] = "idle";
                Log($"实例 #{instanceId} 初始化完成", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log($"实例初始化失败: {ex.Message}", LogLevel.Error);
            }
        }

        // 将新实例添加到列表和队列，然后开放 semaphore
        lock (_instancesLock)
        {
            foreach (var instance in newInstances)
            {
                _allInstances.Add(instance);
                _webViewQueue.Enqueue(instance);
            }
        }

        // 在 UI 线程一次性建好所有标签页（init 阶段，不在请求热路径上）
        Invoke(() =>
        {
            foreach (var (webView, instanceId) in newInstances)
            {
                CreateTabForInstance(webView, instanceId);
            }
        });

        // 修复2：新 semaphore 初始为0，现在开放实际创建成功的槽数
        if (newInstances.Count > 0)
        {
            newSemaphore.Release(newInstances.Count);
            lock (_poolInitLock)
            {
                _poolInitialized = true;
            }
            Log($"WebView2 实例池就绪  共 {_allInstances.Count} 个实例", LogLevel.Success);
        }
        else
        {
            lock (_poolInitLock)
            {
                _poolInitialized = false;
            }
            Log("WebView2 实例池不可用：没有成功创建任何实例", LogLevel.Error);
        }
    }

    // ── 注册WebView2资源响应事件（方案一：旁听模式，不拦截不转发）──
    // WebView2 完全用真实 Chromium 内核自己发请求，绕过 CF Bot 检测
    // 我们只在响应已经到达后，旁读响应体，不干扰任何请求
    private void RegisterWebResourceEvent(WebView2 webView, int instanceId)
    {
        // ── 功能A：监听渲染进程崩溃，自动重建实例 ──
        webView.CoreWebView2.ProcessFailed += async (sender, e) =>
        {
            Log($"实例 #{instanceId}  进程崩溃  类型={e.ProcessFailedKind}", LogLevel.Error);
            _instanceStatus[instanceId] = "crashed";
            RefreshStatsUI();

            // 取消所有正在等待该实例的 FIFO 请求
            var waiterLock = _instanceWaitersLock.GetOrAdd(instanceId, _ => new object());
            lock (waiterLock)
            {
                if (_instanceWaiters.TryRemove(instanceId, out var waiters))
                    foreach (var tcs in waiters) tcs.TrySetCanceled();
            }

            // 只对可恢复的崩溃自动重建（RendererProcessExited 等），进程彻底退出则重建
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Log($"实例 #{instanceId}  浏览器进程退出，无法恢复", LogLevel.Error);
                return;
            }

            Log($"实例 #{instanceId}  尝试自动重建...", LogLevel.Warning);
            await RebuildInstanceAsync(instanceId, webView);
        };

        webView.CoreWebView2.WebResourceResponseReceived += async (sender, e) =>
        {
            try
            {
                var uri = e.Request.Uri;

                // 检查是否有会话等待这个请求（立即返回模式）
                System.Threading.Channels.Channel<string>? channel = null;
                string matchedFilter = string.Empty;
                bool isCompleteLoadMode = false;
                bool includeBody = true;
                bool includeHeaders = false;

                foreach (var kv in _sessions)
                {
                    var (instance, filter) = kv.Key;
                    if (instance == instanceId && uri.Contains(filter))
                    {
                        channel = kv.Value.Channel;
                        matchedFilter = filter;
                        includeBody = kv.Value.IncludeBody;
                        includeHeaders = kv.Value.IncludeHeaders;
                        break;
                    }
                }

                // 检查是否是完整加载模式的请求
                if (channel == null)
                {
                    foreach (var kv in _collectedData)
                    {
                        var (instance, filter) = kv.Key;
                        if (instance == instanceId && uri.Contains(filter))
                        {
                            matchedFilter = filter;
                            isCompleteLoadMode = true;
                            includeBody = kv.Value.IncludeBody;
                            includeHeaders = kv.Value.IncludeHeaders;
                            break;
                        }
                    }
                }

                // 如果既不是立即返回模式也不是完整加载模式，直接返回
                if (channel == null && !isCompleteLoadMode) return;

                Log($"实例 #{instanceId}  命中  {uri[..Math.Min(120, uri.Length)]}", LogLevel.Warning);

                try
                {
                    // 旁读响应体：WebView2 已经用真实 Chromium 发出并收到了响应
                    // GetContentAsync() 读取的是 WebView2 自己拿到的响应流，不会重新发请求
                    string body = string.Empty;
                    Dictionary<string, string>? headers = null;
                    Dictionary<string, string>? cookies = null;

                    if (includeBody)
                    {
                        try
                        {
                            using var contentStream = await e.Response.GetContentAsync();
                            using var reader = new System.IO.StreamReader(contentStream, Encoding.UTF8);
                            body = await reader.ReadToEndAsync();
                            Log($"实例 #{instanceId}  响应体 {body.Length} 字符", LogLevel.Success);
                            Log($"实例 #{instanceId}  预览  {body[..Math.Min(200, body.Length)]}", LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            Log($"实例 #{instanceId}  读取响应体失败: {ex.Message}", LogLevel.Warning);
                            // 读取失败不影响流程，body 保持空字符串
                        }
                    }

                    if (includeHeaders)
                    {
                        try
                        {
                            // 获取响应头
                            headers = new Dictionary<string, string>();
                            foreach (var header in e.Response.Headers)
                            {
                                headers[header.Key] = header.Value;
                            }

                            // 获取 Cookie
                            cookies = new Dictionary<string, string>();
                            if (webView.CoreWebView2 != null)
                            {
                                var cookieManager = webView.CoreWebView2.CookieManager;
                                var cookieList = await cookieManager.GetCookiesAsync(uri);
                                foreach (var cookie in cookieList)
                                {
                                    cookies[cookie.Name] = cookie.Value;
                                }
                            }

                            Log($"实例 #{instanceId}  响应头 {headers.Count} 个，Cookie {cookies.Count} 个", LogLevel.Success);
                        }
                        catch (Exception ex)
                        {
                            Log($"实例 #{instanceId}  读取响应头/Cookie失败: {ex.Message}", LogLevel.Warning);
                        }
                    }

                    var interceptedChunk = new InterceptedChunk(uri, includeBody ? body : null, headers, cookies);

                    if (channel != null)
                    {
                        // 立即返回模式：推送给客户端
                        var json = JsonSerializer.Serialize(interceptedChunk) + "\n";
                        try
                        {
                            await channel.Writer.WriteAsync(json);
                            Log($"实例 #{instanceId}  已推送数据", LogLevel.Success);
                        }
                        catch { /* 忽略写入失败 */ }
                    }
                    else if (isCompleteLoadMode)
                    {
                        // 完整加载模式：收集到列表中
                        var dataKey = (instanceId, matchedFilter);
                        if (_collectedData.TryGetValue(dataKey, out var collectionData))
                        {
                            var items = collectionData.Items;
                            int itemCount;
                            lock (items)
                            {
                                items.Add(interceptedChunk);
                                itemCount = items.Count;
                            }
                            Log($"实例 #{instanceId}  已收集数据  当前数量: {itemCount}", LogLevel.Success);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"实例 #{instanceId}  响应处理异常: {ex.Message}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                Log($"实例 #{instanceId}  响应事件异常: {ex.Message}", LogLevel.Error);
            }
        };
    }

    // ── 功能A：自动重建崩溃实例 ──
    private async Task RebuildInstanceAsync(int instanceId, WebView2 oldWebView)
    {
        try
        {
            // 从公共队列移除旧实例（如果还在）
            var tempQ = new ConcurrentQueue<(WebView2, int)>();
            while (_webViewQueue.TryDequeue(out var item))
                if (!(item.Item2 == instanceId && ReferenceEquals(item.Item1, oldWebView))) tempQ.Enqueue(item);
            while (tempQ.TryDequeue(out var item)) _webViewQueue.Enqueue(item);

            // 从 UI 上移除旧标签页
            BeginInvoke(() =>
            {
                RemoveTabForInstanceWebView(oldWebView, instanceId);
            });

            // 销毁旧实例
            try { oldWebView.Dispose(); }
            catch (Exception ex)
            {
                Log($"实例 #{instanceId}  旧实例销毁失败: {ex.Message}", LogLevel.Warning);
            }
            RemoveExactInstance(oldWebView, instanceId);

            // 创建新 WebView2（复用相同 instanceId 和 Profile）
            var newWebView = new WebView2();
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebInterceptor", $"WebView2Profile_{instanceId}");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await newWebView.EnsureCoreWebView2Async(env);

            ConfigureWebView2(newWebView);
            RegisterWebResourceEvent(newWebView, instanceId);

            // 加入池
            lock (_instancesLock) { _allInstances.Add((newWebView, instanceId)); }
            _webViewQueue.Enqueue((newWebView, instanceId));
            _instanceStatus[instanceId] = "idle";

            Log($"实例 #{instanceId}  自动重建完成", LogLevel.Success);
            RefreshStatsUI();
        }
        catch (Exception ex)
        {
            Log($"实例 #{instanceId}  自动重建失败: {ex.Message}", LogLevel.Error);
        }
    }

    // ── 从池中获取WebView2实例 ──
    private async Task<(WebView2 WebView, int InstanceId, SemaphoreSlim LeaseSemaphore)> GetWebViewFromPoolAsync(InterceptRequest? req = null)
    {
        async Task<SemaphoreSlim> WaitPoolSlotAsync()
        {
            while (true)
            {
                var semaphore = _poolSemaphore;
                try
                {
                    await semaphore.WaitAsync();
                    return semaphore;
                }
                catch (ObjectDisposedException)
                {
                    // Pool resize can swap/dispose semaphore while a waiter is pending.
                    await Task.Delay(10);
                }
            }
        }

        SemaphoreSlim leaseSemaphore = _poolSemaphore;
        bool slotHeld = false;
        var excludedInstanceSet = new HashSet<int>();
        try
        {
            // 确保实例池已初始化
            bool poolReady;
            lock (_poolInitLock)
            {
                poolReady = _poolInitialized;
            }

            if (!poolReady)
            {
                await InitWebView2PoolAsync();
                lock (_poolInitLock)
                {
                    poolReady = _poolInitialized;
                }
            }

            if (!poolReady)
            {
                throw new InvalidOperationException("WebView2 实例池不可用，请稍后重试");
            }

            leaseSemaphore = await WaitPoolSlotAsync();
            slotHeld = true;
            
            // 尝试根据请求参数指定实例
            if (req?.instances != null)
            {
                try
                {
                    Log($"接收到实例参数: {req.instances}", LogLevel.Request);
                    // 解析实例ID列表
                    var parts = req.instances.Split(',')
                        .Select(id => id.Trim())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToList();
                    
                    var specifiedInstances = new List<int>();
                    var excludedInstances = new List<int>();
                    
                    foreach (var part in parts)
                    {
                        if (part.StartsWith("!"))
                        {
                            var idStr = part.Substring(1).Trim();
                            if (int.TryParse(idStr, out int id))
                            {
                                excludedInstances.Add(id);
                                excludedInstanceSet.Add(id);
                            }
                        }
                        else
                        {
                            if (int.TryParse(part, out int id))
                            {
                                specifiedInstances.Add(id);
                            }
                        }
                    }
                    
                    Log($"指定实例: {(specifiedInstances.Count > 0 ? string.Join(',', specifiedInstances) : "无")}, 排除实例: {(excludedInstances.Count > 0 ? string.Join(',', excludedInstances) : "无")}", LogLevel.Request);
                    
                    // 获取所有可用实例
                    List<(WebView2, int)> allAvailableInstances;
                    lock (_instancesLock)
                    {
                        Log($"当前实例池中共有 {_allInstances.Count} 个实例", LogLevel.Debug);
                        foreach (var inst in _allInstances)
                            Log($"实例池中存在实例 #{inst.Item2}", LogLevel.Debug);
                        allAvailableInstances = _allInstances.ToList();
                    }
                    
                    List<(WebView2, int)> candidateInstances;
                    
                    if (specifiedInstances.Count > 0)
                    {
                        // 有指定实例，只使用这些实例（排除其中的排除项）
                        candidateInstances = allAvailableInstances
                            .Where(i => specifiedInstances.Contains(i.Item2) && !excludedInstances.Contains(i.Item2))
                            .ToList();
                        Log($"从指定实例中选择，候选实例数: {candidateInstances.Count}", LogLevel.Request);
                        
                        if (candidateInstances.Count > 0)
                        {
                            // 尝试按顺序使用候选实例
                            foreach (var candidateInstance in candidateInstances)
                            {
                                int instanceId = candidateInstance.Item2;
                                Log($"尝试使用实例 #{instanceId}", LogLevel.Request);
                                
                                bool isCoreWebView2Ready = false;
                                bool foundInQueue = false;
                                
                                // UI 线程检查 CoreWebView2
                                Invoke(() => { isCoreWebView2Ready = candidateInstance.Item1.CoreWebView2 != null; });
                                
                                if (isCoreWebView2Ready)
                                {
                                    Log($"使用指定实例 #{instanceId}", LogLevel.Request);
                                    
                                    // 从队列中移除该实例，记录是否在队列中
                                    var tempQueue = new ConcurrentQueue<(WebView2, int)>();
                                    while (_webViewQueue.TryDequeue(out var item))
                                    {
                                        if (item.Item2 != instanceId) tempQueue.Enqueue(item);
                                        else { foundInQueue = true; Log($"从队列中移除实例 #{instanceId}", LogLevel.Debug); }
                                    }
                                    while (tempQueue.TryDequeue(out var item)) _webViewQueue.Enqueue(item);
                                    
                                    if (!foundInQueue)
                                    {
                                        // 实例正被其他请求占用：
                                        // 1. 释放刚才多占的 semaphore 槽（本请求不走正常队列路径）
                                        // 2. 把自己的 TCS 按 FIFO 入队，等待实例归还时被唤醒
                                        leaseSemaphore.Release();
                                        slotHeld = false;
                                        Log($"指定实例 #{instanceId} 正忙，FIFO 排队等待...", LogLevel.Warning);
                                        
                                        var tcs = new TaskCompletionSource<(WebView2, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        var waiterLock = _instanceWaitersLock.GetOrAdd(instanceId, _ => new object());
                                        lock (waiterLock)
                                        {
                                            var queue = _instanceWaiters.GetOrAdd(instanceId, _ => new Queue<TaskCompletionSource<(WebView2, int)>>());
                                            queue.Enqueue(tcs);
                                        }
                                        
                                        // 使用请求超时时间等待被唤醒（最长 120 秒）
                                        int timeoutMs = (req?.timeout_seconds ?? 120) * 1000;
                                        using var cts = new CancellationTokenSource(timeoutMs);
                                        cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
                                        
                                        (WebView2, int) granted;
                                        try
                                        {
                                            granted = await tcs.Task;
                                            // 修复问题4：等待者被唤醒时没有持有 semaphore 槽
                                            // 必须重新 WaitAsync 占槽，否则 ReturnWebViewToPool 的 finally Release() 会 double-release
                                            leaseSemaphore = await WaitPoolSlotAsync();
                                            slotHeld = true;
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            // 超时：从等待队列中移除自己
                                            lock (waiterLock)
                                            {
                                                if (_instanceWaiters.TryGetValue(instanceId, out var q))
                                                {
                                                    // 重建队列，跳过已取消的 tcs
                                                    var tmp = new Queue<TaskCompletionSource<(WebView2, int)>>(q.Where(t => t != tcs));
                                                    _instanceWaiters[instanceId] = tmp;
                                                }
                                            }
                                            Log($"等待指定实例 #{instanceId} 超时，使用默认分配", LogLevel.Warning);
                                            // 超时后走默认队列分配，需要重新 WaitAsync 占槽
                                            leaseSemaphore = await WaitPoolSlotAsync();
                                            slotHeld = true;
                                            goto defaultAllocation;
                                        }
                                        
                                        // 被唤醒时 granted 就是已从队列取走的实例，直接走 UI 创建标签页
                                        // 轻量：标签页在 init 时已建好，这里只激活
                                        BeginInvoke(() => ActivateTabForInstance(granted.Item2));
                                        Log($"使用指定实例 #{granted.Item2} 成功", LogLevel.Success);
                                        return (granted.Item1, granted.Item2, leaseSemaphore);
                                    }
                                    
                                    // 轻量：标签页在 init 时已建好，这里只激活
                                    BeginInvoke(() => ActivateTabForInstance(instanceId));
                                    
                                    Log($"使用指定实例 #{instanceId} 成功", LogLevel.Success);
                                    return (candidateInstance.Item1, candidateInstance.Item2, leaseSemaphore);
                                }
                                else
                                {
                                    Log($"实例 #{instanceId} 的CoreWebView2不可用，尝试下一个", LogLevel.Warning);
                                }
                            }
                            Log("所有候选实例都不可用，使用默认分配", LogLevel.Warning);
                        }
                        else
                        {
                            Log("没有可用的候选实例，使用默认分配", LogLevel.Warning);
                        }
                    }
                    else if (excludedInstances.Count > 0)
                    {
                        // 没有指定实例，但有排除实例，走默认分配但确保不使用排除实例
                        Log($"没有指定实例但有排除实例，使用默认分配策略（排除实例 {string.Join(',', excludedInstances)}）", LogLevel.Request);
                        goto defaultAllocation;
                    }
                }
                catch (Exception ex)
                {
                    Log($"解析实例参数失败: {ex.Message}", LogLevel.Error);
                }
            }

            // 修复4：goto 跳转目标，指定实例不可用时走这里
            defaultAllocation:
            // 先尝试从队列中获取实例（公平分配）
            int attempts = 0;
            while (attempts < 20) // 增加尝试次数
            {
                if (_webViewQueue.TryDequeue(out var webViewWithId))
                {
                    var instanceId = webViewWithId.Item2;
                    
                    // 检查是否需要排除此实例
                    bool shouldExclude = false;
                    if (excludedInstanceSet.Count > 0)
                    {
                        shouldExclude = excludedInstanceSet.Contains(instanceId);
                    }
                    
                    if (shouldExclude)
                    {
                        Log($"实例 #{instanceId} 在排除列表中，放回队列", LogLevel.Debug);
                        _webViewQueue.Enqueue(webViewWithId);
                        attempts++;
                        await Task.Delay(10); // 短暂等待让其他实例有机会被取出
                    }
                    else
                    {
                        Log($"获取实例 #{instanceId}", LogLevel.Request);
                        // 轻量：标签页已在 init 时建好，仅激活
                        BeginInvoke(() => ActivateTabForInstance(instanceId));
                        return (webViewWithId.Item1, webViewWithId.Item2, leaseSemaphore);
                    }
                }
                else
                {
                    await Task.Delay(50); // 减少延迟时间
                    attempts++;
                }
            }
            
            // 修复5：临时实例用完后直接销毁而不归还队列，避免队列实例数超过 semaphore maxCount
            // 临时实例由当前 WaitAsync 占用的槽对应，Release() 归还槽即可
            Log("实例池为空，创建临时实例", LogLevel.Warning);
            var tempWebView = new WebView2();
            var tempInstanceId = Interlocked.Increment(ref _nextInstanceId);
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebInterceptor", $"WebView2Temp_{Guid.NewGuid()}");
            
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await tempWebView.EnsureCoreWebView2Async(env);
            
            // 配置WebView2
            ConfigureWebView2(tempWebView);
            
            // 注册事件
            RegisterWebResourceEvent(tempWebView, tempInstanceId);
            
            // 临时实例：instanceId 为负数标记（用负值区分），归还时走销毁路径
            // 实际用正 tempInstanceId，但添加到 _tempInstanceIds 集合做标记
            _tempInstanceIds.TryAdd(tempInstanceId, true);
            var newInstance = (tempWebView, tempInstanceId);
            Log($"临时实例 #{tempInstanceId} 已创建", LogLevel.Success);
            
            // 将临时实例添加到所有实例列表中（用于事件处理器查找）
            lock (_instancesLock)
            {
                _allInstances.Add(newInstance);
            }
            
            // 为临时实例创建标签页（复用统一方法）
            Invoke(() => CreateTabForInstance(tempWebView, tempInstanceId));
            
            return (newInstance.Item1, newInstance.Item2, leaseSemaphore);
        }
        catch (Exception ex)
        {
            if (slotHeld)
            {
                try { leaseSemaphore.Release(); }
                catch (ObjectDisposedException) { }
                catch (SemaphoreFullException) { }
            }
            Log($"获取实例失败: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    // ── 归还WebView2实例到池 ──
    private void ReturnWebViewToPool(WebView2 webView, int instanceId, SemaphoreSlim leaseSemaphore, bool navigateToBlank = true)
    {
        // 修复5：判断是否为临时实例，临时实例直接销毁，不重新入队
        bool isTempInstance = _tempInstanceIds.TryRemove(instanceId, out _);
        
        try
        {
            if (isTempInstance)
            {
                // 临时实例：直接销毁，不归还队列（避免超出 semaphore maxCount）
                Log($"临时实例 #{instanceId} 使用完毕，销毁", LogLevel.Debug);
                try
                {
                    BeginInvoke(() =>
                    {
                        RemoveTabForInstanceWebView(webView, instanceId);
                    });
                }
                catch (Exception ex)
                {
                    Log($"临时实例 #{instanceId} 移除标签页失败: {ex.Message}", LogLevel.Debug);
                }
                RemoveExactInstance(webView, instanceId);
                try { webView?.Dispose(); }
                catch (Exception ex)
                {
                    Log($"临时实例 #{instanceId} 销毁失败: {ex.Message}", LogLevel.Warning);
                }
                return; // finally 里会 Release()
            }

            // 导航到空白页以释放内存资源（在UI线程中执行）
            if (navigateToBlank)
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            webView.CoreWebView2.NavigateToString("<html><head><meta charset=utf-8><style>body{margin:0;background:#1e1e1e;display:flex;align-items:center;justify-content:center;height:100vh;font-family:'微软雅黑',sans-serif;color:#555}</style></head><body><div style=text-align:center><div style=font-size:32px;margin-bottom:8px>◎</div><div style=font-size:13px;letter-spacing:2px>READY</div></div></body></html>");
                            Log($"实例 #{instanceId} 已重置", LogLevel.Debug);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"实例 #{instanceId} 重置失败: {ex.Message}", LogLevel.Warning);
                    }
                });
            }
            else
            {
                Log($"实例 #{instanceId} 跳过重置", LogLevel.Debug);
            }
            
            // 确保WebView2实例有效
            if (webView != null && !webView.IsDisposed)
            {
                // 检查是否有 FIFO 等待者（指定了该实例的请求按到达顺序排队）
                bool handedToWaiter = false;
                var waiterLock = _instanceWaitersLock.GetOrAdd(instanceId, _ => new object());
                lock (waiterLock)
                {
                    if (_instanceWaiters.TryGetValue(instanceId, out var waiters) && waiters.Count > 0)
                    {
                        // 取出队头（最早等待的请求），直接把实例交给它
                        var tcs = waiters.Dequeue();
                        tcs.TrySetResult((webView, instanceId));
                        handedToWaiter = true;
                        Log($"实例 #{instanceId} 直接移交给下一个 FIFO 等待者", LogLevel.Debug);
                    }
                }

                if (!handedToWaiter)
                {
                    // 无等待者，归还到公共队列
                    _webViewQueue.Enqueue((webView, instanceId));
                    Log($"实例 #{instanceId} 已归还到公共队列", LogLevel.Debug);
                }
                // handedToWaiter=true 时：实例已移交，当前请求的 semaphore 槽由 finally Release() 正常归还
                // 等待者在 tcs.Task 返回后会重新 WaitAsync 占槽，保证槽计数始终平衡
            }
            else
            {
                Log($"实例 #{instanceId} 已释放，跳过归还", LogLevel.Warning);
                
                // 实例已销毁，取消所有等待者
                var waiterLock = _instanceWaitersLock.GetOrAdd(instanceId, _ => new object());
                lock (waiterLock)
                {
                    if (_instanceWaiters.TryRemove(instanceId, out var waiters))
                    {
                        foreach (var tcs in waiters)
                            tcs.TrySetCanceled();
                    }
                }

                // 从所有实例列表中移除已释放的实例
                RemoveExactInstance(webView, instanceId);
            }
        }
        catch (Exception ex)
        {
            Log($"实例 #{instanceId} 归还失败: {ex.Message}", LogLevel.Warning);
            // 失败时销毁实例
            try
            {
                // 从标签页中移除
                BeginInvoke(() =>
                {
                    RemoveTabForInstanceWebView(webView, instanceId);
                });
            }
            catch (Exception removeTabEx)
            {
                Log($"实例 #{instanceId} 移除标签页失败: {removeTabEx.Message}", LogLevel.Debug);
            }
            
            try
            {
                webView?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log($"实例 #{instanceId} 销毁失败: {disposeEx.Message}", LogLevel.Warning);
            }
            
            // 从所有实例列表中移除已销毁的实例
            RemoveExactInstance(webView, instanceId);
        }
        finally
        {
            try
            {
                leaseSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                Log($"实例 #{instanceId} 对应 semaphore 已释放，跳过归还槽位", LogLevel.Debug);
            }
            catch (SemaphoreFullException ex)
            {
                Log($"实例 #{instanceId} 槽位归还异常: {ex.Message}", LogLevel.Warning);
            }
        }
    }



    private static bool IsValidPort(int port) => port >= 1 && port <= 65535;

    private bool TryCreateHttpServer(
        int port,
        out HttpListener? listener,
        out CancellationTokenSource? cts,
        out Task? loopTask,
        out string error)
    {
        listener = null;
        cts = null;
        loopTask = null;
        error = string.Empty;

        if (!IsValidPort(port))
        {
            error = "端口必须是 1-65535 的整数";
            return false;
        }

        HttpListener? localListener = null;
        CancellationTokenSource? localCts = null;

        try
        {
            localListener = new HttpListener();
            localListener.Prefixes.Add($"http://+:{port}/");  // + 同时支持 IPv4 和 IPv6
            localListener.Start();

            localCts = new CancellationTokenSource();
            var localLoopTask = Task.Run(() => AcceptLoopAsync(localListener, localCts.Token));

            listener = localListener;
            cts = localCts;
            loopTask = localLoopTask;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { localListener?.Close(); } catch { }
            try
            {
                if (localCts != null)
                {
                    localCts.Cancel();
                    localCts.Dispose();
                }
            }
            catch { }
            listener = null;
            cts = null;
            loopTask = null;
            return false;
        }
    }

    private static void StopHttpServerResources(HttpListener? listener, CancellationTokenSource? cts, Task? loopTask)
    {
        try { cts?.Cancel(); } catch { }
        try { listener?.Close(); } catch { }
        try { loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private bool StartHttpServer(int port, out string error)
    {
        if (!TryCreateHttpServer(port, out var listener, out var cts, out var loopTask, out error))
        {
            return false;
        }

        HttpListener? oldListener;
        CancellationTokenSource? oldCts;
        Task? oldLoopTask;
        lock (_httpServerLock)
        {
            oldListener = _httpListener;
            oldCts = _httpServerCts;
            oldLoopTask = _httpServerLoopTask;

            _httpListener = listener;
            _httpServerCts = cts;
            _httpServerLoopTask = loopTask;
        }

        StopHttpServerResources(oldListener, oldCts, oldLoopTask);
        Log($"HTTP 服务启动  →  http://0.0.0.0:{port}/intercept (同时支持 IPv4 和 IPv6)", LogLevel.Success);
        return true;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = HandleHttpRequestAsync(ctx).ContinueWith(
                    t => Log($"请求处理异常: {t.Exception?.GetBaseException().Message ?? "未知错误"}", LogLevel.Error),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"HTTP 接收循环异常: {ex.Message}", LogLevel.Warning);
                try
                {
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void StopHttpServer()
    {
        HttpListener? listener = null;
        CancellationTokenSource? cts = null;
        Task? loopTask = null;

        lock (_httpServerLock)
        {
            listener = _httpListener;
            _httpListener = null;

            cts = _httpServerCts;
            _httpServerCts = null;

            loopTask = _httpServerLoopTask;
            _httpServerLoopTask = null;
        }

        StopHttpServerResources(listener, cts, loopTask);
    }

    private bool RestartHttpServer(int newPort, out string error)
    {
        if (!TryCreateHttpServer(newPort, out var newListener, out var newCts, out var newLoopTask, out error))
        {
            return false;
        }

        HttpListener? oldListener;
        CancellationTokenSource? oldCts;
        Task? oldLoopTask;
        lock (_httpServerLock)
        {
            oldListener = _httpListener;
            oldCts = _httpServerCts;
            oldLoopTask = _httpServerLoopTask;

            _httpListener = newListener;
            _httpServerCts = newCts;
            _httpServerLoopTask = newLoopTask;
        }

        StopHttpServerResources(oldListener, oldCts, oldLoopTask);
        error = string.Empty;
        return true;
    }

    // 存储完整加载模式下的收集数据
    private readonly ConcurrentDictionary<(int, string), CollectionData> _collectedData = new();

    private async Task HandleHttpRequestAsync(HttpListenerContext ctx)
    {
        // 添加CORS头信息
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
        ctx.Response.AddHeader("Access-Control-Max-Age", "86400");
        
        // 处理OPTIONS请求
        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }
        
        if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url?.AbsolutePath != "/intercept")
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        // 请求限流
        if (!await _requestSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var errorMsg = JsonSerializer.Serialize(new { error = "请求过多，请稍后再试" });
            var bytes = Encoding.UTF8.GetBytes(errorMsg);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            Log("请求限流：并发超限", LogLevel.Warning);
            return;
        }

        // 增加等待任务计数
        Interlocked.Increment(ref _waitingTasks);
        bool waitingCounted = true;
        bool runningCounted = false;
        Interlocked.Increment(ref _statTotalRequests);
        var _reqStartTime = System.Diagnostics.Stopwatch.StartNew();
        UpdateWindowTitle();

        (WebView2 WebView, int InstanceId, SemaphoreSlim LeaseSemaphore)? webViewWithId = null;
        InterceptRequest? req = null;
        try
        {
            try
            {
                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var rawBody = await sr.ReadToEndAsync();
                Log($"收到请求  {rawBody}", LogLevel.Request);
                
                req = TryDeserializeRequest(rawBody);
                if (req == null)
                {
                    var cleanedBody = SanitizeRequestBody(rawBody);
                    if (!string.Equals(cleanedBody, rawBody, StringComparison.Ordinal))
                    {
                        Log("原始请求解析失败，尝试兼容清理后重试", LogLevel.Warning);
                        Log($"清理后请求  {cleanedBody}", LogLevel.Debug);
                        req = TryDeserializeRequest(cleanedBody);
                    }
                }

                if (req == null)
                    throw new JsonException("请求 JSON 格式无效");
            }
            catch (Exception ex)
            {
                Log($"请求解析失败: {ex.Message}", LogLevel.Error);
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            if (req == null || string.IsNullOrWhiteSpace(req.Url))
            {
                Log("参数错误：url 为空", LogLevel.Error);
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            // 从池中获取WebView2实例
            try
            {
                webViewWithId = await GetWebViewFromPoolAsync(req);
            }
            catch (Exception ex)
            {
                Log($"获取实例失败: {ex.Message}", LogLevel.Error);
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var errorMsg = JsonSerializer.Serialize(new { error = "服务不可用", message = "WebView2实例池不可用，请稍后重试" });
                var bytes = Encoding.UTF8.GetBytes(errorMsg);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                Interlocked.Increment(ref _statFailed);
                return;
            }
            var webView = webViewWithId.Value.WebView;
            var instanceId = webViewWithId.Value.InstanceId;
            Log($"获取实例 #{instanceId}", LogLevel.Request);
            _instanceStatus[instanceId] = "busy";
            
            // 减少等待任务计数，增加运行任务计数
            if (waitingCounted)
            {
                Interlocked.Decrement(ref _waitingTasks);
                waitingCounted = false;
            }
            Interlocked.Increment(ref _runningTasks);
            runningCounted = true;
            UpdateWindowTitle();

            // Filter 为空时，直接导航并返回完整网页 HTML 文本
            if (string.IsNullOrWhiteSpace(req.Filter))
            {
                Log($"实例 #{instanceId}  Filter 为空，直接返回网页 HTML", LogLevel.Request);
                await HandleNoFilterMode(ctx, webView, instanceId, req);
            }
            else
            {
                bool waitForComplete = req.wait_for_complete ?? false;
                Log($"实例 #{instanceId}  模式: {(waitForComplete ? "完整加载" : "立即返回")}", LogLevel.Request);

                if (waitForComplete)
                {
                    // 完整加载模式：收集所有匹配内容
                    await HandleCompleteLoadMode(ctx, webView, instanceId, req);
                }
                else
                {
                    // 立即返回模式：保持原有逻辑
                    await HandleImmediateReturnMode(ctx, webView, instanceId, req);
                }
            }
        }
        finally
        {
            bool counterChanged = false;

            // 归还WebView2实例到池
            if (webViewWithId != null)
            {
                _instanceStatus[webViewWithId.Value.InstanceId] = "idle";
                // 使用配置的是否导航到空白页，默认为true
                // keep_page=true => keep current page; default false => navigate to blank
                bool navigateToBlank = !(req?.keep_page ?? false);
                ReturnWebViewToPool(
                    webViewWithId.Value.WebView,
                    webViewWithId.Value.InstanceId,
                    webViewWithId.Value.LeaseSemaphore,
                    navigateToBlank);
            }

            // 请求在等待阶段提前失败时，确保等待计数被回收
            if (waitingCounted)
            {
                Interlocked.Decrement(ref _waitingTasks);
                waitingCounted = false;
                counterChanged = true;
            }
            
            // 记录耗时
            _reqStartTime.Stop();
            Interlocked.Add(ref _statTotalMs, _reqStartTime.ElapsedMilliseconds);

            // 减少运行任务计数
            if (runningCounted)
            {
                Interlocked.Decrement(ref _runningTasks);
                runningCounted = false;
                counterChanged = true;
            }
            
            if (counterChanged)
            {
                UpdateWindowTitle();
            }
            
            _requestSemaphore.Release();
        }
    }

    // 无Filter模式：导航到目标URL，等待加载完成后返回完整网页HTML
    private async Task HandleNoFilterMode(HttpListenerContext ctx, WebView2 webView, int instanceId, InterceptRequest req)
    {
        int timeoutSeconds = req.timeout_seconds ?? 60;
        bool includeBodyRequested = req.include_body == true;
        bool includeHeadersRequested = req.include_headers == true;
        bool returnJson = includeBodyRequested || includeHeadersRequested;
        string? requestHost = null;
        try
        {
            if (Uri.TryCreate(req.Url, UriKind.Absolute, out var reqUri))
                requestHost = reqUri.Host;
        }
        catch { }

        Log($"实例 #{instanceId}  等待页面加载完成  timeout={timeoutSeconds}s", LogLevel.Warning);

        EventHandler<CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? responseReceivedHandler = null;
        var documentHeaderLock = new object();
        Dictionary<string, string>? latestDocumentHeaders = null;
        string? latestDocumentUri = null;

        try
        {
            var navigationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            webView.Invoke(() =>
            {
                navigationCompletedHandler = (sender, e) =>
                {
                    try
                    {
                        Log($"实例 #{instanceId}  页面加载完成", LogLevel.Success);
                        navigationTcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        navigationTcs.TrySetException(ex);
                    }
                    finally
                    {
                        SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
                    }
                };

                if (includeHeadersRequested)
                {
                    responseReceivedHandler = (sender, e) =>
                    {
                        try
                        {
                            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var header in e.Response.Headers)
                                headers[header.Key] = header.Value;
                            // Compatible with WebView2 SDK versions that do not expose ResourceContext.
                            // Keep only document-like HTML responses.
                            if (!headers.TryGetValue("Content-Type", out var contentType) ||
                                (contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 contentType.IndexOf("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) < 0))
                            {
                                return;
                            }

                            if (!string.IsNullOrWhiteSpace(requestHost))
                            {
                                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var candidateUri) ||
                                    !string.Equals(candidateUri.Host, requestHost, StringComparison.OrdinalIgnoreCase))
                                {
                                    return;
                                }
                            }
                            lock (documentHeaderLock)
                            {
                                if (latestDocumentHeaders == null)
                                {
                                    latestDocumentHeaders = headers;
                                    latestDocumentUri = e.Request.Uri;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"instance #{instanceId} failed to capture document headers: {ex.Message}", LogLevel.Warning);
                        }
                    };

                    webView.CoreWebView2.WebResourceResponseReceived += responseReceivedHandler;
                }

                webView.CoreWebView2.NavigationCompleted += navigationCompletedHandler;
                Log($"实例 #{instanceId}  -> {req.Url}", LogLevel.Request);
                webView.CoreWebView2.Navigate(req.Url);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            var completedTask = await Task.WhenAny(navigationTcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
                ctx.Response.StatusCode = 504;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var errorMsg = JsonSerializer.Serialize(new { error = "超时", message = $"{timeoutSeconds}秒内网页未加载完成" });
                var bytes = Encoding.UTF8.GetBytes(errorMsg);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                Log($"实例 #{instanceId}  超时：{timeoutSeconds}s 内页面未加载完成", LogLevel.Error);
                return;
            }

            await navigationTcs.Task;

            var htmlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            webView.Invoke(() =>
            {
                _ = FetchHtmlAsync();

                async Task FetchHtmlAsync()
                {
                    try
                    {
                        var result = await webView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                        var unquoted = JsonSerializer.Deserialize<string>(result) ?? string.Empty;
                        htmlTcs.TrySetResult(unquoted);
                    }
                    catch (Exception ex)
                    {
                        htmlTcs.TrySetException(ex);
                    }
                }
            });

            var htmlContent = await htmlTcs.Task;

            if (returnJson)
            {
                Dictionary<string, string>? headers = null;
                Dictionary<string, string>? cookies = null;
                string responseUri = req.Url;

                if (includeHeadersRequested)
                {
                    Dictionary<string, string>? capturedHeaders = null;
                    string? capturedUri = null;
                    lock (documentHeaderLock)
                    {
                        if (latestDocumentHeaders != null)
                            capturedHeaders = new Dictionary<string, string>(latestDocumentHeaders, StringComparer.OrdinalIgnoreCase);
                        capturedUri = latestDocumentUri;
                    }

                    headers = capturedHeaders ?? new Dictionary<string, string>();
                    if (!string.IsNullOrWhiteSpace(capturedUri))
                        responseUri = capturedUri!;
                    else
                    {
                        Log($"instance #{instanceId} no document headers captured in no-filter mode", LogLevel.Warning);
                    }

                    cookies = new Dictionary<string, string>();
                    try
                    {
                        var cookiesTcs = new TaskCompletionSource<Dictionary<string, string>>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        webView.Invoke(() =>
                        {
                            _ = FetchCookiesAsync();

                            async Task FetchCookiesAsync()
                            {
                                try
                                {
                                    var dict = new Dictionary<string, string>();
                                    if (webView.CoreWebView2 != null)
                                    {
                                        var cookieManager = webView.CoreWebView2.CookieManager;
                                        var cookieList = await cookieManager.GetCookiesAsync(responseUri);
                                        foreach (var cookie in cookieList)
                                            dict[cookie.Name] = cookie.Value;
                                    }
                                    cookiesTcs.TrySetResult(dict);
                                }
                                catch (Exception ex)
                                {
                                    cookiesTcs.TrySetException(ex);
                                }
                            }
                        });

                        cookies = await cookiesTcs.Task;
                    }
                    catch (Exception ex)
                    {
                        Log($"instance #{instanceId} failed to read cookies in no-filter mode: {ex.Message}", LogLevel.Warning);
                    }
                }

                var payload = new InterceptedChunk(
                    responseUri,
                    includeBodyRequested ? htmlContent : null,
                    headers,
                    cookies);

                var json = JsonSerializer.Serialize(payload);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = jsonBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(jsonBytes);
                Log($"instance #{instanceId} no-filter response returned as JSON, bytes={jsonBytes.Length}", LogLevel.Success);
                Interlocked.Increment(ref _statSuccess);
                return;
            }

            var htmlBytes = Encoding.UTF8.GetBytes(htmlContent);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = htmlBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(htmlBytes);
            Log($"instance #{instanceId} HTML returned to client, bytes={htmlBytes.Length}", LogLevel.Success);
            Interlocked.Increment(ref _statSuccess);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var errorMsg = JsonSerializer.Serialize(new { error = "失败", message = ex.Message });
            var bytes = Encoding.UTF8.GetBytes(errorMsg);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            Log($"实例 #{instanceId}  输出异常: {ex.Message}", LogLevel.Error);
            Interlocked.Increment(ref _statFailed);
        }
        finally
        {
            SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
            if (responseReceivedHandler != null)
            {
                try
                {
                    if (!webView.IsDisposed)
                    {
                        if (webView.InvokeRequired)
                        {
                            webView.Invoke((Action)(() =>
                            {
                                if (webView.CoreWebView2 != null)
                                    webView.CoreWebView2.WebResourceResponseReceived -= responseReceivedHandler;
                            }));
                        }
                        else
                        {
                            if (webView.CoreWebView2 != null)
                                webView.CoreWebView2.WebResourceResponseReceived -= responseReceivedHandler;
                        }
                    }
                }
                catch { }
            }

            ctx.Response.Close();
            Log($"实例 #{instanceId}  会话结束", LogLevel.Debug);
        }
    }
    private async Task HandleImmediateReturnMode(HttpListenerContext ctx, WebView2 webView, int instanceId, InterceptRequest req)
    {
        // 用 TaskCompletionSource 等待第一条拦截数据，拿到后立刻关闭连接
        var tcs = new TaskCompletionSource<string>();

        // 使用配置的是否包含body，默认为true
        bool includeBody = req.include_body ?? true;
        bool includeHeaders = req.include_headers ?? false;
        Log($"实例 #{instanceId}  包含Body: {includeBody}, 包含Headers: {includeHeaders}", LogLevel.Request);

        // 使用 ConcurrentDictionary 管理会话，使用(instanceId, filter)作为key
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var sessionData = new SessionData(channel, includeBody, includeHeaders);
        if (!_sessions.TryAdd((instanceId, req.Filter), sessionData))
        {
            // 同实例+同filter已有会话在运行（理论上不应发生，因实例在排队中），记录警告
            Log($"实例 #{instanceId}  会话冲突 filter={req.Filter}，当前请求将无法收到数据", LogLevel.Warning);
        }

        // 后台任务：等 channel 第一条数据，写入 tcs
        _ = Task.Run(async () =>
        {
            try
            {
                // 使用配置的超时时间，默认60秒
                int timeoutSeconds = req.timeout_seconds ?? 60;
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var result = await channel.Reader.ReadAsync(cts2.Token);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                // 清理 channel
                _sessions.TryRemove((instanceId, req.Filter), out _);
                channel.Writer.TryComplete();
            }
        });

        // 在WebView2实例上直接导航，无需注册过滤器
        // WebResourceResponseReceived 会旁听所有响应，由事件处理器内部匹配 filter
        webView.Invoke(() =>
        {
            Log($"实例 #{instanceId}  →  {req.Url}", LogLevel.Request);
            webView.CoreWebView2.Navigate(req.Url);
        });

        // 使用配置的超时时间，默认60秒
        int timeoutSeconds = req.timeout_seconds ?? 60;
        Log($"实例 #{instanceId}  等待拦截  filter={req.Filter}  timeout={timeoutSeconds}s", LogLevel.Warning);

        try
        {
            // 等第一条数据，拿到后立刻返回给客户端并关闭连接
            var result = await tcs.Task;

            var bytes = Encoding.UTF8.GetBytes(result);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            Log($"实例 #{instanceId}  数据已返回客户端", LogLevel.Success);
            Interlocked.Increment(ref _statSuccess);
        }
        catch (OperationCanceledException)
        {
            ctx.Response.StatusCode = 504;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var errorMsg = JsonSerializer.Serialize(new { error = "拦截失败", message = $"{timeoutSeconds}秒内未拦截到数据" });
            var bytes = Encoding.UTF8.GetBytes(errorMsg);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            Log($"实例 #{instanceId}  超时：{timeoutSeconds}s 内未拦截到数据", LogLevel.Error);
            Interlocked.Increment(ref _statTimeout);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var errorMsg = JsonSerializer.Serialize(new { error = "拦截失败", message = ex.Message });
            var bytes = Encoding.UTF8.GetBytes(errorMsg);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            Log($"实例 #{instanceId}  输出异常: {ex.Message}", LogLevel.Error);
            Interlocked.Increment(ref _statFailed);
        }
        finally
        {
            ctx.Response.Close();
            Log($"实例 #{instanceId}  会话结束", LogLevel.Debug);
        }
    }

    // 完整加载模式处理
    private async Task HandleCompleteLoadMode(HttpListenerContext ctx, WebView2 webView, int instanceId, InterceptRequest req)
    {
        // 使用配置的超时时间，默认60秒
        int timeoutSeconds = req.timeout_seconds ?? 60;
        // 使用配置的是否包含body，默认为true
        bool includeBody = req.include_body ?? true;
        bool includeHeaders = req.include_headers ?? false;
        Log($"实例 #{instanceId}  等待完整加载  filter={req.Filter}  timeout={timeoutSeconds}s  包含Body: {includeBody}  包含Headers: {includeHeaders}", LogLevel.Warning);

        // 为完整加载模式创建数据收集列表
        var dataKey = (instanceId, req.Filter);
        var collectedItems = new List<InterceptedChunk>();
        var collectionData = new CollectionData(collectedItems, includeBody, includeHeaders);
        _collectedData.TryAdd(dataKey, collectionData);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? navigationCompletedHandler = null;

        // 使用配置的是否导航到空白页，默认为true
        // keep_page=true => keep current page; default false => navigate to blank
        bool navigateToBlank = !(req.keep_page ?? false);

        try
        {
            // 创建TaskCompletionSource来等待网页加载完成
            var navigationTcs = new TaskCompletionSource<bool>();

            // 在WebView2实例上直接导航，无需注册过滤器
            // WebResourceResponseReceived 会旁听所有响应，由事件处理器内部匹配 filter
            webView.Invoke(() =>
            {
                // 注册导航完成事件
                navigationCompletedHandler = (sender, e) =>
                {
                    try
                    {
                        Log($"实例 #{instanceId}  网页加载完成", LogLevel.Success);
                        navigationTcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Log($"实例 #{instanceId}  导航完成事件异常: {ex.Message}", LogLevel.Error);
                        navigationTcs.TrySetException(ex);
                    }
                    finally
                    {
                        // 移除事件处理器，避免内存泄漏
                        SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
                    }
                };

                webView.CoreWebView2.NavigationCompleted += navigationCompletedHandler;
                
                Log($"实例 #{instanceId}  →  {req.Url}", LogLevel.Request);
                webView.CoreWebView2.Navigate(req.Url);
            });

            // 等待网页加载完成或超时
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var navigationTask = navigationTcs.Task;
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);

            var completedTask = await Task.WhenAny(navigationTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
                // 超时
                ctx.Response.StatusCode = 504;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var errorMsg = JsonSerializer.Serialize(new { error = "拦截失败", message = $"{timeoutSeconds}秒内网页未加载完成" });
                var bytes = Encoding.UTF8.GetBytes(errorMsg);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                Log($"实例 #{instanceId}  超时：{timeoutSeconds}s 内网页未加载完成", LogLevel.Error);
                Interlocked.Increment(ref _statTimeout);
                return;
            }

            // 确保导航任务成功完成
            await navigationTask;

            // 给一点额外时间收集可能的延迟加载内容（默认2秒，可通过请求参数覆盖）
            int collectDelaySeconds = req.collect_delay_seconds ?? 2;
            collectDelaySeconds = Math.Clamp(collectDelaySeconds, 0, 30);
            if (collectDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(collectDelaySeconds));

            // 检查是否收集到数据
            if (_collectedData.TryGetValue(dataKey, out var collectedData))
            {
                var items = collectedData.Items;
                List<InterceptedChunk> snapshot;
                lock (items)
                {
                    snapshot = items.ToList();
                }
                Log($"实例 #{instanceId}  收集到 {snapshot.Count} 个匹配项", LogLevel.Success);
                
                if (snapshot.Count > 0)
                {
                    // 返回所有收集到的内容
                    var response = new { items = snapshot };
                    var json = JsonSerializer.Serialize(response);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    Log($"实例 #{instanceId}  数据已返回客户端", LogLevel.Success);
                    Interlocked.Increment(ref _statSuccess);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    var errorMsg = JsonSerializer.Serialize(new { error = "未拦截到数据", message = "页面加载完成但未找到匹配内容" });
                    var bytes = Encoding.UTF8.GetBytes(errorMsg);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    Log($"实例 #{instanceId}  未找到匹配内容", LogLevel.Warning);
                    Interlocked.Increment(ref _statFailed);
                }
            }
            else
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var errorMsg = JsonSerializer.Serialize(new { error = "拦截失败", message = "数据收集失败" });
                var bytes = Encoding.UTF8.GetBytes(errorMsg);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                Log($"实例 #{instanceId}  数据收集失败", LogLevel.Error);
                Interlocked.Increment(ref _statFailed);
            }
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var errorMsg = JsonSerializer.Serialize(new { error = "拦截失败", message = ex.Message });
            var bytes = Encoding.UTF8.GetBytes(errorMsg);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            Log($"实例 #{instanceId}  输出异常: {ex.Message}", LogLevel.Error);
            Interlocked.Increment(ref _statFailed);
        }
        finally
        {
            SafeUnsubscribeNavigationCompleted(webView, navigationCompletedHandler);
            // 清理数据收集
            _collectedData.TryRemove(dataKey, out _);
            ctx.Response.Close();
            Log($"实例 #{instanceId}  会话结束", LogLevel.Debug);
        }
    }
}
