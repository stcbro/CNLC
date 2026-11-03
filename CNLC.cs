// ==================================================================
//  CILC-CE - 校园内网登录器
//  Campus Intranet Login Client (Civil Engineering Custom Edition)
//  简称：CILC-CE（工程代号 CNLC）
//  针对高校校园网（华为 AC Portal 1.1.1.3 认证）环境定制
//  功能：登录窗口 / 托盘守护 / 断网自动重连 / 记住密码(跨电脑) /
//        开机自登录(自启动+静默) / 雨课堂网页版快捷入口 / 注销 / 状态窗口
//  说明：不生成日志与状态文件，保持程序目录干净（状态显示用内存记录）
//  版本：1.3.4
//  作者：Bro.Cui
//  编译：由 Build-Exe.ps1 调用 Windows 自带的 csc.exe 完成
// ==================================================================

using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CNLC
{
    internal static class App
    {
        // ---------- 配置常量 ----------
        internal const string AppVersion = "1.3.4";   // 版本号（仅代码内部使用，不显示在界面）
        internal const string AppAuthor = "Bro.Cui";  // 作者
        private const string PortalHost = "http://1.1.1.3";
        private const string PortalLoginPage = PortalHost + "/ac_portal/20240318141412/pc.html";
        private const string PortalLoginApi = PortalHost + "/ac_portal/login.php";
        private const string ProbeUrl = "https://www.baidu.com";
        private const int CheckIntervalSec = 30;
        private const int MaxLoginAttempts = 10;
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunName = "CNLC";
        private const string YuketangUrl = "https://sycu.yuketang.cn/pro/portal/home/";   // 雨课堂网页版入口
        // 更新检查走 Gitee Release 发布制：只有作者手动创建的 Release 才对用户可见，
        // main 分支的日常提交不影响用户端
        private const string ReleaseApiUrl = "https://gitee.com/api/v5/repos/stcbro/CNLC/releases/latest";
        private const string ReleaseUrl = "https://gitee.com/stcbro/CNLC/releases";   // 下载页

        // 软件简介（"关于"弹出）；中英文之间不加空格，紧凑排版
        private const string AboutText =
            "校园内网登录器（CNLC）是一款面向校园网Portal认证环境的Windows托盘工具，" +
            "解决学校教室希沃大屏“每次联网都要手动打开网页输账号密码、打开雨课堂网页版需要手工输入网址”的痛点。\n\n" +
            "程序启动后常驻系统托盘：一键完成Portal认证；支持断网后每30秒自动检测并重新认证；" +
            "支持记住密码、开机自登录——开机即联网，全程无感；点击“雨课堂网页版”按钮，一键直达学校雨课堂门户，无需手工输入网址。\n\n" +
            "整个程序为单个exe文件，绿色免安装，在本地配置好账号密码后，拷贝程序所在文件夹到U盘即可在任意实验室/办公室电脑使用。";
        // 通知总开关：false = 关闭所有弹动提示（浮条/托盘气泡），完全静默，仅写日志
        private static readonly bool ShowNotify = false;

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string CredFile = Path.Combine(BaseDir, "CNLC.dat");
        private static readonly string StateFile = Path.Combine(BaseDir, "auto_login_status.txt");
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");

        // 日志开关：
        //   测试版（编译时 /define:DIAG，文件名 CNLC-Test.exe）：始终写日志与状态文件，作者手持排查用
        //   正式版（默认构建）：零残留；仅在 exe 旁新建空文件 debug.on 时临时写日志
#if DIAG
        private static readonly bool DebugMode = true;
#else
        private static readonly bool DebugMode = File.Exists(Path.Combine(BaseDir, "debug.on"));
#endif

        // ---------- 运行状态 ----------
        private static Mutex mutex;
        private static bool exiting = false;
        private static NotifyIcon tray;
        private static ToolStripMenuItem miGuard;
        private static System.Windows.Forms.Timer timer;
        private static bool GuardOn = false;
        private static bool ExtendedRetry = false;
        private static string portalUser = "";
        private static string portalPass = "";
        private static Form loginForm;
        private static Form statusForm;
        private static Form aboutForm;
        private static readonly CookieContainer cookies = new CookieContainer();

        // 登录窗口控件（事件处理器需要访问）
        private static TextBox txtUser;
        private static TextBox txtPass;
        private static CheckBox chkRemember;
        private static CheckBox chkGuard;
        private static CheckBox chkAutoLogin;
        private static Button btnYuketang;
        private static Button btnLogin;
        private static Button btnLogout;
        private static bool suppressGuard = false;
        private static bool suppressAutoLogin = false;

        // 已保存的凭据
        private static string savedUser = "";
        private static string savedPass = "";
        private static bool savedAutoLogin = false;

        // 高分屏（125%/150% 缩放）下让文字原生渲染，清晰不发虚
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // 查询物理按键全局按下状态（用于状态窗"屏幕任意点击/按键即关闭"）
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // ---------- 后台线程 / UI 调度 ----------
        // 网络请求（认证/探测/注销，最坏 10~20 秒超时）必须放后台线程，
        // 否则外网等不可达环境下 UI 线程被阻塞，表现为"程序卡死"（已实测）
        private static System.Threading.SynchronizationContext _uiCtx;

        private static void RunBg(ThreadStart work)
        {
            System.Threading.Thread t = new System.Threading.Thread(work);
            t.IsBackground = true;
            t.Start();
        }

        // 切回 UI 线程执行（NotifyIcon/控件/浮条等只能在 UI 线程操作）
        private static void Ui(MethodInvoker mi)
        {
            try
            {
                if (_uiCtx != null) { _uiCtx.Post(delegate { try { mi(); } catch { } }, null); return; }
            }
            catch { }
            try { mi(); } catch { }
        }

        // 统一弹窗入口：指定主界面为父窗口。
        // 关键修复：主界面 TopMost=true，无父窗口的 MessageBox 会被压在主界面下层，
        // 同时主界面又被模态框禁用——表现为"程序卡死"（托盘注销确认框曾触发此 bug）
        private static DialogResult MsgOn(Form owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (owner != null && !owner.IsDisposed) return MessageBox.Show(owner, text, caption, buttons, icon);
            return MessageBox.Show(text, caption, buttons, icon);
        }

        private static DialogResult MsgOn(Form owner, string text, string caption)
        {
            return MsgOn(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        private static DialogResult Msg(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MsgOn(loginForm, text, caption, buttons, icon);
        }

        private static void Msg(string text, string caption)
        {
            Msg(text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { SetProcessDPIAware(); } catch { }
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls; } catch { }
            WebRequest.DefaultWebProxy = new WebProxy();   // 强制直连，绕过本地 VPN/代理
            _uiCtx = System.Threading.SynchronizationContext.Current;   // UI 线程调度器（后台线程回调用）

            bool autoStart = false;
            foreach (string a in args)
                if (string.Equals(a, "-AutoStart", StringComparison.OrdinalIgnoreCase)) autoStart = true;

            bool createdNew;
            try
            {
                mutex = new Mutex(true, "CNLC.SingleInstance", out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // 上个实例被强制结束导致互斥体遗留（无持有者），本进程安全接管，视为正常启动
                createdNew = true;
                try { mutex = new Mutex(false, "CNLC.SingleInstance", out createdNew); } catch { }
            }
            if (!createdNew)
            {
                Msg("校园内网登录器已在运行（右下角托盘图标）。\n\n如需切换账号，请先右键托盘图标退出，再重新启动。", "提示");
                return;
            }

            LoadCreds();
            BuildLoginForm();

            if (autoStart && savedUser.Length > 0 && savedPass.Length > 0 && savedAutoLogin)
            {
                // 开机自启动拉起 + 已记住密码 + 已开启开机自登录 → 静默登录，直接进托盘守护
                portalUser = savedUser;
                portalPass = savedPass;
                Log("开机自启动：静默登录 " + portalUser);
                suppressGuard = true; chkGuard.Checked = true; suppressGuard = false;
                GuardStart();
            }
            else
            {
                loginForm.Show();
            }

            try { Application.Run(); }
            finally
            {
                CleanupTray();
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        // ================= 登录窗口 =================

        private static Label NewLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            return l;
        }

        // ---------- 自绘标题条状态 ----------
        private static Panel titlePanel;
        private static Label titleLabel;
        private static Label minBtn;
        private static Label closeBtn;

        // 标题条底色：与白色主界面同系但稍深，形成微妙的分界
        private static readonly Color TitleBarBack = Color.FromArgb(245, 245, 245);

        private static void ApplyTitleStyle()
        {
            titlePanel.BackColor = TitleBarBack;
            titleLabel.ForeColor = Color.FromArgb(25, 25, 25);
            minBtn.ForeColor = Color.FromArgb(60, 60, 60);
            minBtn.BackColor = TitleBarBack;
            closeBtn.ForeColor = Color.FromArgb(60, 60, 60);
            closeBtn.BackColor = TitleBarBack;
            titlePanel.Invalidate();
        }

        private static void BuildLoginForm()
        {
            // 无边框 + 自绘标题条（只有 － 和 ×，无系统 □；最小化悬停提示可自定义）
            loginForm = new Form();
            loginForm.Text = "校园内网登录器（CNLC）";
            loginForm.ClientSize = new Size(320, 212);
            loginForm.FormBorderStyle = FormBorderStyle.None;
            loginForm.StartPosition = FormStartPosition.CenterScreen;
            loginForm.TopMost = true;
            loginForm.ShowInTaskbar = false;   // 纯托盘程序，任务栏不留图标
            loginForm.AutoScaleMode = AutoScaleMode.Dpi;
            loginForm.AutoScaleDimensions = new SizeF(96F, 96F);
            try { loginForm.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---------- 自绘标题条（仿 Windows 10 原生样式）----------
            titlePanel = new Panel();
            titlePanel.Dock = DockStyle.Top;
            titlePanel.Height = 32;
            titlePanel.BackColor = TitleBarBack;
            Bitmap titleBmp = null;
            try
            {
                // app.ico 内部是 PNG 封装条目，.NET Icon 类解析不了（会抛异常），
                // 必须走 Shell 的 ExtractAssociatedIcon（托盘图标一直正常就是证明）
                using (Icon ti = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    titleBmp = ti.ToBitmap();
            }
            catch { }
            Bitmap iconForPaint = titleBmp;
            titlePanel.Paint += delegate(object s, PaintEventArgs e)
            {
                // 程序图标：按当前 DPI 缩放绘制（125% 屏画 20×20 物理像素，避免非整数拉伸发糊）
                if (iconForPaint != null)
                {
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    int iconSize = Math.Max(16, (int)Math.Round(16 * e.Graphics.DpiX / 96F));
                    e.Graphics.DrawImage(iconForPaint, 10, (titlePanel.Height - iconSize) / 2, iconSize, iconSize);
                }
            };

            titleLabel = NewLabel("校园内网登录器（CNLC）", 32, 8);
            // 标题文字与正文同字体但加粗加深，低分屏/缩放屏上不发虚
            titleLabel.Font = new Font(Control.DefaultFont, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(25, 25, 25);
            titlePanel.Controls.Add(titleLabel);

            // 最小化按钮：46×32 贴右上，图标为画的水平细线（仿系统）
            minBtn = new Label();
            minBtn.Size = new Size(46, 32);
            minBtn.Location = new Point(320 - 92, 0);
            minBtn.ForeColor = Color.FromArgb(60, 60, 60);
            minBtn.BackColor = TitleBarBack;
            minBtn.Cursor = Cursors.Hand;
            minBtn.Click += delegate { EnsureTray(); loginForm.Hide(); };
            minBtn.MouseEnter += delegate { minBtn.BackColor = Color.FromArgb(226, 226, 226); };
            minBtn.MouseLeave += delegate { ApplyTitleStyle(); };
            minBtn.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(minBtn.ForeColor, 1F))
                {
                    int cx = minBtn.Width / 2, cy = minBtn.Height / 2;
                    e.Graphics.DrawLine(p, cx - 5, cy, cx + 4, cy);
                }
            };
            titlePanel.Controls.Add(minBtn);

            // 关闭按钮：hover 红底白线（仿系统）
            closeBtn = new Label();
            closeBtn.Size = new Size(46, 32);
            closeBtn.Location = new Point(320 - 46, 0);
            closeBtn.ForeColor = Color.FromArgb(60, 60, 60);
            closeBtn.BackColor = TitleBarBack;
            closeBtn.Cursor = Cursors.Hand;
            closeBtn.Click += delegate { loginForm.Close(); };
            closeBtn.MouseEnter += delegate { closeBtn.BackColor = Color.FromArgb(232, 17, 35); closeBtn.ForeColor = Color.White; };
            closeBtn.MouseLeave += delegate { ApplyTitleStyle(); };
            closeBtn.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(closeBtn.ForeColor, 1F))
                {
                    int cx = closeBtn.Width / 2, cy = closeBtn.Height / 2;
                    e.Graphics.DrawLine(p, cx - 5, cy - 5, cx + 4, cy + 4);
                    e.Graphics.DrawLine(p, cx + 4, cy - 5, cx - 5, cy + 4);
                }
            };
            titlePanel.Controls.Add(closeBtn);
            loginForm.Controls.Add(titlePanel);

            // 标题条固定强调色，不随激活/失焦变化
            ApplyTitleStyle();

            // 窗体外圈细边框（无边框窗体的"系统边框"替代）
            loginForm.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawRectangle(new Pen(Color.FromArgb(204, 204, 204)),
                    0, 0, loginForm.ClientSize.Width - 1, loginForm.ClientSize.Height - 1);
            };

            // 悬停提示
            ToolTip tip = new ToolTip();
            tip.SetToolTip(minBtn, "最小化到托盘");
            tip.SetToolTip(closeBtn, "退出程序");
            tip.SetToolTip(titleLabel, "按住可拖动窗口");

            // 按住标题条拖动窗口
            bool dragging = false;
            Point dragCursor = Point.Empty, dragForm = Point.Empty;
            titlePanel.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { dragging = true; dragCursor = Cursor.Position; dragForm = loginForm.Location; }
            };
            titlePanel.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging)
                {
                    Point d = Point.Subtract(Cursor.Position, (Size)dragCursor);
                    loginForm.Location = Point.Add(dragForm, (Size)d);
                }
            };
            titlePanel.MouseUp += delegate(object s, MouseEventArgs e) { dragging = false; };
            titleLabel.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { dragging = true; dragCursor = Cursor.Position; dragForm = loginForm.Location; }
            };
            titleLabel.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging)
                {
                    Point d = Point.Subtract(Cursor.Position, (Size)dragCursor);
                    loginForm.Location = Point.Add(dragForm, (Size)d);
                }
            };
            titleLabel.MouseUp += delegate(object s, MouseEventArgs e) { dragging = false; };

            // ---------- 左右分栏：左栏 = 标签+输入框（同一行），右栏 = 登录/注销 ----------
            Label lblUser = NewLabel("工号:", 20, 66);
            txtUser = new TextBox();
            txtUser.Location = new Point(72, 62);
            txtUser.Size = new Size(135, 23);

            btnLogin = new Button();
            btnLogin.Text = "登 录";
            btnLogin.Location = new Point(225, 62);
            btnLogin.Size = new Size(72, 26);

            Label lblPass = NewLabel("密码:", 20, 104);
            txtPass = new TextBox();
            txtPass.Location = new Point(72, 100);
            txtPass.Size = new Size(135, 23);
            txtPass.PasswordChar = '*';

            btnLogout = new Button();
            btnLogout.Text = "注 销";
            btnLogout.Location = new Point(225, 100);
            btnLogout.Size = new Size(72, 26);

            chkRemember = new CheckBox();
            chkRemember.Text = "记住密码";
            chkRemember.Location = new Point(25, 152);
            chkRemember.AutoSize = true;

            chkGuard = new CheckBox();
            chkGuard.Text = "断网自动重连";
            chkGuard.Location = new Point(165, 152);
            chkGuard.AutoSize = true;

            // "开机自登录"（合并版）：勾选 = 开机自启动 + 开机静默登录
            chkAutoLogin = new CheckBox();
            chkAutoLogin.Text = "开机自登录";
            chkAutoLogin.Location = new Point(25, 178);
            chkAutoLogin.AutoSize = true;

            // "雨课堂网页版"按钮：点击打开雨课堂门户网页，同时最小化主界面到托盘
            btnYuketang = new Button();
            btnYuketang.Text = "雨课堂网页版";
            btnYuketang.Location = new Point(165, 178);
            btnYuketang.Size = new Size(115, 26);

            loginForm.Controls.Add(lblUser);
            loginForm.Controls.Add(txtUser);
            loginForm.Controls.Add(lblPass);
            loginForm.Controls.Add(txtPass);
            loginForm.Controls.Add(btnLogin);
            loginForm.Controls.Add(btnLogout);
            loginForm.Controls.Add(chkRemember);
            loginForm.Controls.Add(chkGuard);
            loginForm.Controls.Add(chkAutoLogin);
            loginForm.Controls.Add(btnYuketang);
            loginForm.AcceptButton = btnLogin;

            // 已记住密码：自动填入并勾选
            if (savedUser.Length > 0)
            {
                txtUser.Text = savedUser;
                txtPass.Text = savedPass;
                chkRemember.Checked = true;
            }
            // 开机自登录状态以注册表 Run 项为准（勾选时注册表与凭据文件同步写入，不会失步）
            suppressAutoLogin = true; chkAutoLogin.Checked = AutoStartEnabled(); suppressAutoLogin = false;
            chkGuard.Checked = true;

            btnLogin.Click += delegate { BtnLoginClick(); };
            btnLogout.Click += delegate { DoLogout(); };

            chkGuard.CheckedChanged += delegate
            {
                if (suppressGuard) return;
                if (portalUser.Length == 0) return;   // 未登录：仅记住勾选偏好，登录时自动按此生效
                if (chkGuard.Checked) { loginForm.Hide(); GuardStart(); }
                else StopGuard();
            };

            // 开机自登录（合并版）：勾选 = 写注册表 Run 项（自启动）+ 凭据文件 autoLogin 标记（静默登录）
            chkAutoLogin.CheckedChanged += delegate
            {
                if (suppressAutoLogin) return;
                if (chkAutoLogin.Checked)
                {
                    if (!File.Exists(CredFile))
                    {
                        suppressAutoLogin = true; chkAutoLogin.Checked = false; suppressAutoLogin = false;
                        Msg("开机自登录需要先勾选\"记住密码\"并登录一次，保存账号。", "提示");
                        return;
                    }
                    try
                    {
                        SetAutoStart(true);
                        SetAutoLoginFlag(true);
                        Log("已开启开机自登录（开机自启动并静默登录）");
                    }
                    catch (Exception ex)
                    {
                        Msg("设置开机自登录失败：" + ex.Message, "错误");
                        suppressAutoLogin = true; chkAutoLogin.Checked = false; suppressAutoLogin = false;
                    }
                }
                else
                {
                    try
                    {
                        SetAutoStart(false);
                        SetAutoLoginFlag(false);
                    }
                    catch { }
                    Log("已关闭开机自登录");
                }
            };

            // 雨课堂网页版：打开门户网页，同时把主界面最小化到托盘
            btnYuketang.Click += delegate
            {
                try
                {
                    System.Diagnostics.Process.Start(YuketangUrl);
                    Log("已打开雨课堂网页版：" + YuketangUrl);
                }
                catch (Exception ex)
                {
                    Msg("打开浏览器失败：" + ex.Message, "错误");
                }
                EnsureTray();
                loginForm.Hide();
            };

            // 底行对齐：让"开机自登录"复选框与"雨课堂网页版"按钮中心线一致
            // （DPI 缩放对两者舍入不同会产生 1~3px 错位，显示时按实际高度动态居中修正）
            // 输入框动态定位：不同屏幕/字体的标签渲染宽度不同（希沃大屏会贴住输入框），
            // 显示时按标签实际右边界重排，保证 6px 间隙，宽度延伸到距登录/注销按钮 8px
            loginForm.Shown += delegate
            {
                chkAutoLogin.Top = btnYuketang.Top + (btnYuketang.Height - chkAutoLogin.Height) / 2;
                // 右列上下对齐：按钮与"断网自动重连"复选框同左边缘、同宽度，行距与左列一致
                btnYuketang.Left = chkGuard.Left;
                btnYuketang.Width = chkGuard.Width;
                txtUser.Left = lblUser.Right + 6;
                txtUser.Width = btnLogin.Left - txtUser.Left - 8;
                txtPass.Left = lblPass.Right + 6;
                txtPass.Width = btnLogout.Left - txtPass.Left - 8;
            };

            loginForm.FormClosed += delegate { ExitApp(); };
        }

        private static void BtnLoginClick()
        {
            string u = txtUser.Text.Trim();
            string p = txtPass.Text;
            if (u.Length == 0 || p.Length == 0)
            {
                Msg("请填写账号和密码。", "提示");
                return;
            }
            portalUser = u;
            portalPass = p;
            SaveCreds();

            if (tray != null)
            {
                tray.Text = "校园内网登录器 - " + u + "（每 " + CheckIntervalSec + "s 检测）";
                Log("账号已切换为 " + u);
            }

            // 认证放后台线程：外网等不可达环境下请求超时可达 10~20 秒，
            // 同步执行会冻结 UI（实测"卡死"）；期间按钮禁用并显示认证中
            btnLogin.Enabled = false;
            btnLogin.Text = "认证中";
            btnLogout.Enabled = false;
            RunBg(delegate
            {
                bool ok = PortalLogin();
                System.Threading.Thread.Sleep(800);
                bool online = !TestOffline();
                Ui(delegate
                {
                    btnLogin.Enabled = true;
                    btnLogin.Text = "登 录";
                    btnLogout.Enabled = true;

                    if (chkGuard.Checked)
                    {
                        // 守护开关已勾选：认证已在上面完成，这里只启动守护循环（失败时下个周期自动重试）
                        EnsureTray();
                        GuardOn = true;
                        if (miGuard != null) miGuard.Checked = true;
                        if (timer != null) timer.Start();
                    }

                    if (ok && online)
                    {
                        ExtendedRetry = false;
                        SaveState(true);
                        Log("登录成功，网络已恢复 !");
                        ShowLoginResult("登录成功，网络已恢复", false);
                    }
                    else if (ok && !online)
                    {
                        ShowLoginResult("认证已提交，但网络暂未连通，请稍候重试", true);
                    }
                    else
                    {
                        string reason = lastLoginMsg.Length > 0 ? lastLoginMsg
                            : "无法连接认证服务器，请确认已接入校园网";
                        Log("登录失败：" + reason);
                        ShowLoginResult("登录失败：" + reason, true);
                    }
                });
            });
        }

        // ================= 托盘守护 =================

        private static void EnsureTray()
        {
            if (tray != null) return;

            Log("校园内网登录器已启动（托盘守护）");
            Log("版本: " + AppVersion + "   作者: " + AppAuthor);
            Log("账号: " + portalUser + "   检测间隔: " + CheckIntervalSec + "s");
            Log("目标服务器: " + PortalHost + "   登录接口: " + PortalLoginApi);

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Shield; }
            tray.Text = "校园内网登录器" + (portalUser.Length > 0 ? " - " + portalUser : "") + "（每 " + CheckIntervalSec + "s 检测）";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主界面");
            miShow.Font = new Font(miShow.Font, FontStyle.Bold);
            miShow.Click += delegate
            {
                loginForm.Show();
                loginForm.Activate();
            };
            miGuard = new ToolStripMenuItem("断网自动重连");
            miGuard.Checked = true;
            miGuard.Click += delegate { SetGuard(!GuardOn); };
            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于");
            miAbout.Click += delegate { ShowAbout(); };
            ToolStripMenuItem miLogout = new ToolStripMenuItem("注销");
            miLogout.Click += delegate { DoLogout(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };

            menu.Items.Add(miShow);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miGuard);
            menu.Items.Add(miAbout);
            menu.Items.Add(miLogout);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            tray.ContextMenuStrip = menu;

            // 只有左键点击才显示状态窗（右键会同时触发 MouseUp，需过滤）
            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowStatus();
            };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = CheckIntervalSec * 1000;
            timer.Tick += delegate { Tick(); };

            Log("已最小化到系统托盘（右下角图标）。");
            Balloon("守护已开始（" + portalUser + "）：每 " + CheckIntervalSec + "s 检测一次，断网自动重连", false);
        }

        private static void GuardStart()
        {
            EnsureTray();
            GuardOn = true;
            if (miGuard != null) miGuard.Checked = true;
            if (timer != null) timer.Start();

            // 立即认证放后台线程（不可达环境下超时可达 10~20 秒，不能冻结 UI）
            RunBg(delegate
            {
                // 点登录后：跳过联网检测等待，直接立即发认证请求，一步到位上网
                Log("正在立即登录认证（跳过检测）...");
                PortalLogin();
                System.Threading.Thread.Sleep(1000);
                if (TestOffline())
                {
                    Log("立即认证未恢复，转入自动重试 ...");
                    ReloginLoop();
                }
                else
                {
                    Log("登录成功，网络已恢复 !");
                    ExtendedRetry = false;
                    SaveState(true);
                    Ui(delegate { Balloon("登录成功，网络已恢复！", false); });
                }
            });
        }

        private static void StopGuard()
        {
            if (timer != null) timer.Stop();
            GuardOn = false;
            if (miGuard != null) miGuard.Checked = false;
            Log("守护已关闭（不再自动重连）");
            if (tray != null) tray.Text = "校园内网登录器 - 守护已关闭";
        }

        private static void SetGuard(bool on)
        {
            if (on) { GuardStart(); GuardOn = true; if (miGuard != null) miGuard.Checked = true; }
            else { StopGuard(); }
        }

        private static int tickBusy = 0;   // 防止上轮后台检测未完成时重复进入

        private static void Tick()
        {
            // 检测与重连循环放后台线程（断网时 ReloginLoop 可达数分钟，不能冻结 UI）
            if (System.Threading.Interlocked.CompareExchange(ref tickBusy, 1, 0) != 0) return;
            RunBg(delegate
            {
                try
                {
                    bool wasOffline = TestOffline();
                    if (wasOffline)
                    {
                        Ui(delegate { Balloon("检测到断网，正在自动重新认证 ...", true); });
                        ReloginLoop();
                        if (!TestOffline()) Ui(delegate { Balloon("登录成功，网络已恢复！", false); });
                    }
                    else
                    {
                        Log("网络正常，无需操作 ...");
                        SaveState(false);
                    }
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref tickBusy, 0);
                }
            });
        }

        private static void ReloginLoop()
        {
            int max = ExtendedRetry ? 40 : MaxLoginAttempts;
            string hint = ExtendedRetry ? "（注销冷却期，服务器放行需几分钟，请耐心等待）" : "";
            for (int att = 1; att <= max; att++)
            {
                if (PortalLogin())
                {
                    System.Threading.Thread.Sleep(2000);
                    if (!TestOffline())
                    {
                        Log("登录成功，网络已恢复 !");
                        ExtendedRetry = false;
                        SaveState(true);
                        return;
                    }
                }
                Log("第 " + att + " 次认证失败，5 秒后重试" + hint + " ...");
                System.Threading.Thread.Sleep(5000);
            }
            Log("已连续 " + max + " 次认证失败，将在下一个检测周期重试" + hint);
        }

        private static void DoLogout()
        {
            // 注销目标以输入框当前账号为准（用户可能已改输新账号），输入框为空才用已登录账号
            string target = txtUser.Text.Trim();
            if (target.Length == 0) target = portalUser;
            if (target.Length == 0)
            {
                Msg("尚未登录，无需注销。", "提示");
                return;
            }
            DialogResult r = Msg(
                "确定注销账号 " + target + " 吗？\n注销后网络将断开，守护暂停。", "确认注销",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            portalUser = target;
            StopGuard();
            btnLogout.Enabled = false;
            RunBg(delegate
            {
                bool ok = PortalLogout();
                Ui(delegate
                {
                    btnLogout.Enabled = true;
                    if (ok)
                    {
                        // portal 服务器注销后有短暂冷却期 → 开启加密重试模式
                        ExtendedRetry = true;
                        Log("注销成功（" + target + "），网络已断开。");
                        Balloon("账号 " + target + " 已注销，网络已断开", false);
                        if (tray != null) tray.Text = "校园内网登录器 - 已注销";
                    }
                    else
                    {
                        Log("注销请求未成功（可能已离线或被服务器拒绝）");
                        Balloon("注销请求未成功（可能已离线）", true);
                    }
                });
            });
            // 保留输入框中用户正在输入的账号密码，不回填旧值
        }

        private static void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            Log("用户退出");
            CleanupTray();
            Application.Exit();
        }

        private static void CleanupTray()
        {
            if (tray != null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                tray = null;
            }
        }

        // ================= 状态窗口 / Toast =================

        private static void ShowStatus()
        {
            if (tray == null)
            {
                Msg("尚未登录，守护未启动。", "状态");
                return;
            }
            // 单实例：状态窗已打开时不重复弹
            if (statusForm != null && !statusForm.IsDisposed)
            {
                statusForm.Activate();
                return;
            }
            bool offline = TestOffline();
#if DIAG
            // 测试版：状态文件里的最近检测/登录时间优先（跨重启保留）
            try
            {
                if (File.Exists(StateFile))
                {
                    string c = File.ReadAllText(StateFile);
                    Match m = Regex.Match(c, "\"last_check\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success && m.Groups[1].Value.Length > 0) lastCheckTime = m.Groups[1].Value;
                    m = Regex.Match(c, "\"last_login_at\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success && m.Groups[1].Value.Length > 0) lastLoginTime = m.Groups[1].Value;
                }
            }
            catch { }
#endif

            statusForm = new Form();
            statusForm.Text = "运行状态";
            statusForm.ClientSize = new Size(300, 158);
            statusForm.FormBorderStyle = FormBorderStyle.None;   // 无边框：无 - 口 X，也不放确定按钮
            statusForm.StartPosition = FormStartPosition.CenterScreen;
            statusForm.TopMost = true;
            statusForm.BackColor = Color.White;
            statusForm.AutoScaleMode = AutoScaleMode.Dpi;
            statusForm.AutoScaleDimensions = new SizeF(96F, 96F);

            // 无边框窗体手动绘制标题区
            Label title = NewLabel("运行状态", 20, 12);
            title.Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold);
            statusForm.Controls.Add(title);

            string[] lines = new string[] {
                "当前账号：" + portalUser,
                "网络状态：" + (offline ? "已断网" : "在线"),
                "检测间隔：" + CheckIntervalSec + " 秒",
                "最近检测：" + (lastCheckTime.Length > 0 ? lastCheckTime : "本次运行尚未检测"),
                "最近登录：" + (lastLoginTime.Length > 0 ? lastLoginTime : "本次运行尚未登录")
            };
            int y = 42;
            foreach (string t in lines)
            {
                Label lb = NewLabel(t, 20, y);
                statusForm.Controls.Add(lb);
                y += 22;
            }

            // 屏幕上任意位置点击鼠标或按任意键即自动关闭：
            // 用 GetAsyncKeyState 轮询物理按键状态（全局生效，不限本窗口内）
            System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
            closeTimer.Interval = 100;
            closeTimer.Tick += delegate
            {
                if (statusForm == null || statusForm.IsDisposed) { closeTimer.Stop(); return; }
                bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0    // 鼠标左键
                            || (GetAsyncKeyState(0x02) & 0x8000) != 0    // 鼠标右键
                            || (GetAsyncKeyState(0x04) & 0x8000) != 0;   // 鼠标中键
                if (!pressed)
                {
                    for (int vk = 0x08; vk <= 0xFE; vk++)   // Backspace 起的全部键盘键
                    {
                        if ((GetAsyncKeyState(vk) & 0x8000) != 0) { pressed = true; break; }
                    }
                }
                if (pressed) statusForm.Close();
            };
            closeTimer.Start();

            // 无边框窗体手动画一圈细边框，避免和白背景融在一起
            statusForm.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawRectangle(new Pen(Color.FromArgb(120, 120, 120)),
                    0, 0, statusForm.ClientSize.Width - 1, statusForm.ClientSize.Height - 1);
            };

            // 关键：ShowDialog 的窗体 Close 后不会自动 Dispose，IsDisposed 仍为 false，
            // 若不清理引用，下次左键会被单实例守卫误判为"已打开"而只 Activate（无反应）
            statusForm.FormClosed += delegate
            {
                closeTimer.Stop();
                try { closeTimer.Dispose(); } catch { }
                try { statusForm.Dispose(); } catch { }
                statusForm = null;
            };
            statusForm.ShowDialog();
        }

        private static void ShowLoginResult(string message, bool isError)
        {
            // 登录结果反馈：显示在登录窗底部内层，宽度=文字+4个字符，1.5 秒后自动消失
            Form tf = new Form();
            tf.FormBorderStyle = FormBorderStyle.None;
            tf.StartPosition = FormStartPosition.Manual;
            tf.AutoSize = true;                    // 必须开启，否则 AutoSizeMode 无效
            tf.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tf.TopMost = true;
            tf.ShowInTaskbar = false;
            tf.BackColor = isError ? Color.FromArgb(198, 40, 40) : Color.FromArgb(46, 125, 50);
            Label lb = new Label();
            lb.Text = message;
            lb.AutoSize = true;
            lb.ForeColor = Color.White;
            lb.Padding = new Padding(26, 8, 26, 8);   // 左右各多出约 2 个字符，共 4 字符余量
            tf.Controls.Add(lb);
            // 窗口显示后（尺寸已定）定位到登录窗底部内层、水平居中，浮于主界面上层
            tf.Shown += delegate
            {
                tf.Location = new Point(
                    loginForm.Left + (loginForm.Width - tf.Width) / 2,
                    loginForm.Bottom - tf.Height - 12);
            };
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1500;   // 1.5 秒自动消失，不打扰后续操作
            t.Tick += delegate { tf.Close(); };
            t.Start();
            tf.ShowDialog();
            t.Stop();
            t.Dispose();
        }

        // ================= 软件简介窗 =================

        private static void ShowAbout()
        {
            if (aboutForm != null && !aboutForm.IsDisposed) { aboutForm.Activate(); return; }
            aboutForm = new Form();
            aboutForm.Text = "关于";
            aboutForm.ClientSize = new Size(368, 296);
            aboutForm.FormBorderStyle = FormBorderStyle.None;
            aboutForm.StartPosition = FormStartPosition.CenterScreen;
            aboutForm.TopMost = true;
            aboutForm.BackColor = Color.White;
            aboutForm.AutoScaleMode = AutoScaleMode.Dpi;
            aboutForm.AutoScaleDimensions = new SizeF(96F, 96F);

            Label title = NewLabel("关于校园内网登录器", 20, 12);
            title.Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold);
            aboutForm.Controls.Add(title);

            Label body = new Label();
            body.Text = AboutText;
            body.Location = new Point(18, 44);
            body.Size = new Size(332, 214);
            aboutForm.Controls.Add(body);

            Label foot = NewLabel("v" + AppVersion + "  © " + DateTime.Now.Year + " Bro.Cui", 18, 266);

            // 右下角"检查更新"按钮：联网比对 Gitee 上的 version.txt
            Button btnUpdate = new Button();
            btnUpdate.Text = "检查更新";
            btnUpdate.Location = new Point(268, 258);
            btnUpdate.Size = new Size(84, 26);
            btnUpdate.Click += delegate { btnUpdate.Enabled = false; CheckUpdate(); };
            aboutForm.Controls.Add(btnUpdate);
            foot.ForeColor = Color.FromArgb(110, 110, 110);
            aboutForm.Controls.Add(foot);

            // 关键：窗体尺寸与正文高度按 DPI 缩放后的实际字体动态计算——
            // 固定尺寸在不同分辨率/缩放下文字行数不同，会溢出裁切
            aboutForm.Shown += delegate
            {
                try
                {
                    int x = body.Left;
                    int inner = aboutForm.ClientSize.Width - x * 2;
                    Size ts = TextRenderer.MeasureText(AboutText, body.Font,
                        new Size(inner, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    body.Size = new Size(inner, ts.Height + 6);
                }
                catch { }
                // 底部行：版权贴左无缩进，检查更新按钮贴右不动
                try
                {
                    int rowY = body.Bottom + 12;
                    foot.Location = new Point(body.Left, rowY + (btnUpdate.Height - foot.Height) / 2);
                    btnUpdate.Location = new Point(aboutForm.ClientSize.Width - btnUpdate.Width - body.Left, rowY);
                    aboutForm.ClientSize = new Size(aboutForm.ClientSize.Width, rowY + btnUpdate.Height + 12);
                }
                catch { }
            };

            // 点击窗口内任意处或按任意键关闭（"检查更新"按钮除外——点了它要等结果）
            aboutForm.KeyPreview = true;
            aboutForm.KeyDown += delegate(object s, KeyEventArgs e) { aboutForm.Close(); };
            aboutForm.MouseClick += delegate(object s, MouseEventArgs e) { aboutForm.Close(); };
            foreach (Control c in aboutForm.Controls)
                if (c != btnUpdate)
                    c.MouseClick += delegate(object s, MouseEventArgs e) { aboutForm.Close(); };

            aboutForm.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawRectangle(new Pen(Color.FromArgb(120, 120, 120)),
                    0, 0, aboutForm.ClientSize.Width - 1, aboutForm.ClientSize.Height - 1);
            };
            aboutForm.FormClosed += delegate
            {
                try { aboutForm.Dispose(); } catch { }
                aboutForm = null;
            };
            aboutForm.ShowDialog();
        }

        // ================= 检查更新 =================

        // 获取 Gitee 最新 Release 的 tag（如 "v1.3.5"）。
        // 返回 "" 表示尚无任何发布（视为已是最新）；null 表示网络失败
        private static string GetLatestReleaseTag()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ReleaseApiUrl);
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "Mozilla/5.0";
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        Match m = Regex.Match(sr.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"([^\"]*)\"");
                        return m.Success ? m.Groups[1].Value : "";
                    }
                }
                catch (WebException we)
                {
                    HttpWebResponse err = we.Response as HttpWebResponse;
                    if (err != null && err.StatusCode == HttpStatusCode.NotFound) return "";   // 仓库还没有任何 Release
                    return null;
                }
            }
            catch { return null; }
        }

        // 后台线程查询最新 Release（不卡 UI），结果回传 UI 线程弹窗
        private static void CheckUpdate()
        {
            Form owner = (aboutForm != null && !aboutForm.IsDisposed) ? aboutForm : loginForm;
            if (owner == null || owner.IsDisposed) return;
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                string tag = GetLatestReleaseTag();
                owner.BeginInvoke(new MethodInvoker(delegate()
                {
                    Version rv = null;
                    bool ok = tag != null && tag.Length > 0 &&
                              Version.TryParse(tag.TrimStart('v', 'V'), out rv);
                    if (tag == null)
                    {
                        MsgOn(owner, "检查更新失败：无法连接更新源，请稍后再试。", "检查更新");
                        return;
                    }
                    if (ok && rv > new Version(AppVersion))
                    {
                        if (MsgOn(owner, "发现新版本 v" + rv + "（当前 v" + AppVersion + "）。\n\n是否打开下载页面？",
                                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            try { System.Diagnostics.Process.Start(ReleaseUrl); } catch { }
                        }
                    }
                    else
                    {
                        MsgOn(owner, "当前已是最新版本 v" + AppVersion + "。", "检查更新");
                    }
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        // ================= 认证核心 =================

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static void Log(string msg)
        {
            // 仅诊断模式（exe 旁存在 debug.on 文件）才写日志，平时零残留
            if (!DebugMode) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                string line = "[" + Now() + "] " + msg;
                File.AppendAllText(
                    Path.Combine(LogDir, "auto_login_" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static void Balloon(string message, bool warning)
        {
            if (!ShowNotify) return;
            if (tray == null) return;
            try
            {
                tray.ShowBalloonTip(4000, "校园内网登录器", message,
                    warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch { }
        }

        // RC4（与 portal 前端一致：UTF-8 字节，输出小写十六进制）
        private static string Rc4Hex(string input, string key)
        {
            byte[] data = Encoding.UTF8.GetBytes(input);
            byte[] kb = Encoding.UTF8.GetBytes(key);
            int[] s = new int[256];
            for (int i = 0; i < 256; i++) s[i] = i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + kb[i % kb.Length]) & 0xFF;
                int t = s[i]; s[i] = s[j]; s[j] = t;
            }
            StringBuilder sb = new StringBuilder();
            int ii = 0; j = 0;
            foreach (byte b in data)
            {
                ii = (ii + 1) & 0xFF;
                j = (j + s[ii]) & 0xFF;
                int t = s[ii]; s[ii] = s[j]; s[j] = t;
                int k = s[(s[ii] + s[j]) & 0xFF];
                sb.Append(((b ^ k) & 0xFF).ToString("x2"));
            }
            return sb.ToString();
        }

        private static long UnixMillis()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static string HttpGet(string url, int timeoutSec)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutSec * 1000;
                req.ReadWriteTimeout = timeoutSec * 1000;
                req.CookieContainer = cookies;
                req.UserAgent = "Mozilla/5.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log("GET " + url + " 异常: " + ex.Message);
                return null;
            }
        }

        private static string HttpPost(string url, string body, int timeoutSec, string referer)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.Timeout = timeoutSec * 1000;
                req.ReadWriteTimeout = timeoutSec * 1000;
                req.CookieContainer = cookies;
                req.UserAgent = "Mozilla/5.0";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Referer = referer;
                req.Headers.Add("X-Requested-With: XMLHttpRequest");
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log("POST " + url + " 异常: " + ex.Message);
                return null;
            }
        }

        private static string lastLoginMsg = "";   // 最近一次登录失败时 portal 返回的原因

        private static bool PortalLogin()
        {
            lastLoginMsg = "";
            string rckey = UnixMillis().ToString();
            string enc = Rc4Hex(portalPass, rckey);
            string body = "opr=pwdLogin&userName=" + Uri.EscapeDataString(portalUser) +
                          "&pwd=" + enc + "&auth_tag=" + rckey + "&rememberPwd=0";
            HttpGet(PortalLoginPage, 4);   // 先取会话 cookie（不可达环境快速失败）
            string resp = HttpPost(PortalLoginApi, body, 8, PortalLoginPage);
            if (resp != null && Regex.IsMatch(resp, "\"success\"\\s*:\\s*true")) return true;
            if (resp != null)
            {
                Log("登录返回: " + resp);
                Match m = Regex.Match(resp, "\"message\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success) lastLoginMsg = m.Groups[1].Value;
            }
            return false;
        }

        private static bool PortalLogout()
        {
            string resp = HttpPost(PortalLoginApi, "opr=logout", 8, PortalLoginPage);
            Log("注销返回: " + (resp == null ? "无响应" : resp));
            return resp != null && Regex.IsMatch(resp, "\"success\"\\s*:\\s*true");
        }

        private static bool TestOffline()
        {
            try
            {
                string s = HttpGet(ProbeUrl, 6);
                bool ok = s != null && s.Contains("baidu");
                if (!ok)
                    Log("联网探测失败" + (s == null ? "（无响应）" :
                        "（响应前 120 字: " + s.Substring(0, Math.Min(120, s.Length)).Replace("\n", " ").Replace("\r", "") + "）"));
                return !ok;
            }
            catch { return true; }
        }

        // ================= 状态 / 凭据 / 自启动 =================

        // 运行状态（内存记录，供状态窗显示；程序重启后清零）
        private static string lastCheckTime = "";
        private static string lastLoginTime = "";

        private static void SaveState(bool loginSuccess)
        {
            lastCheckTime = Now();
            if (loginSuccess) lastLoginTime = Now();
#if DIAG
            // 测试版：额外持久化状态文件（跨重启保留最近检测/登录时间）；正式版零残留不写
            try
            {
                string content = "{\n  \"last_check\": \"" + lastCheckTime + "\",\n  \"online_status\": \"ONLINE\",\n  \"last_login_at\": \"" +
                                 (lastLoginTime.Length > 0 ? lastLoginTime : lastCheckTime) + "\"\n}";
                File.WriteAllText(StateFile, content, Encoding.UTF8);
            }
            catch { }
#endif
        }

        private static void LoadCreds()
        {
            savedUser = ""; savedPass = ""; savedAutoLogin = false;
            // 旧版迁移：credentials.txt → CNLC.dat（隐藏文件，不易误删误看）
            string legacyCred = Path.Combine(BaseDir, "credentials.txt");
            if (!File.Exists(CredFile) && File.Exists(legacyCred))
            {
                try { File.Move(legacyCred, CredFile); } catch { }
            }
            if (File.Exists(CredFile)) NormalizeCredFile();
            if (!File.Exists(CredFile)) return;
            try
            {
                string c = File.ReadAllText(CredFile);
                Match m = Regex.Match(c, "\"user\"\\s*:\\s*\"([^\"]*)\"");
                savedUser = m.Success ? m.Groups[1].Value : "";
                m = Regex.Match(c, "\"pass\"\\s*:\\s*\"([^\"]*)\"");
                string pb = m.Success ? m.Groups[1].Value : "";
                try { savedPass = Encoding.UTF8.GetString(Convert.FromBase64String(pb)); }
                catch { savedPass = ""; }
                savedAutoLogin = Regex.IsMatch(c, "\"autoLogin\"\\s*:\\s*true");
            }
            catch
            {
                try { File.Delete(CredFile); } catch { }
            }
        }

        // 凭据文件保持普通属性。旧版曾设隐藏+系统属性，但"隐藏的凭据文件"是杀毒软件
        // 启发式引擎的强恶意特征（360 因此误报），已取消；旧文件读入时归一化为普通属性
        private static void NormalizeCredFile()
        {
            try { File.SetAttributes(CredFile, FileAttributes.Archive); } catch { }
        }

        private static void WriteCredFile(string content)
        {
            NormalizeCredFile();   // 旧版可能遗留隐藏/系统属性，写前归一化，否则 WriteAllText 会抛异常
            File.WriteAllText(CredFile, content, Encoding.UTF8);
        }

        private static void SaveCreds()
        {
            if (chkRemember.Checked)
            {
                try
                {
                    string content = "{\n  \"user\": \"" + portalUser + "\",\n  \"pass\": \"" +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(portalPass)) +
                        "\",\n  \"autoLogin\": " + (chkAutoLogin.Checked ? "true" : "false") + "\n}";
                    WriteCredFile(content);
                }
                catch (Exception ex)
                {
                    Log("保存记住密码失败（不影响本次使用）: " + ex.Message);
                }
            }
            else
            {
                try { File.Delete(CredFile); } catch { }
                // 取消记住密码：凭据没了，开机自登录同时关闭（注册表 + 勾选状态一起清理）
                try { SetAutoStart(false); } catch { }
                suppressAutoLogin = true; chkAutoLogin.Checked = false; suppressAutoLogin = false;
            }
        }

        private static bool AutoStartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return key != null && key.GetValue(RunName) != null;
            }
        }

        private static void SetAutoStart(bool on)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (on) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\" -AutoStart");
                else key.DeleteValue(RunName, false);
            }
        }

        // 凭据文件中的 autoLogin 标记（开机静默登录开关），与注册表 Run 项配套写入
        private static void SetAutoLoginFlag(bool on)
        {
            if (!File.Exists(CredFile)) return;
            string content = File.ReadAllText(CredFile);
            string val = on ? "true" : "false";
            if (Regex.IsMatch(content, "\"autoLogin\"\\s*:"))
                content = Regex.Replace(content, "\"autoLogin\"\\s*:\\s*(true|false)", "\"autoLogin\": " + val);
            else
                content = content.TrimEnd().TrimEnd('}') + ",\n  \"autoLogin\": " + val + "\n}";
            WriteCredFile(content);
        }
    }
}
