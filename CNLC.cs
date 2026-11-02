// ==================================================================
//  CNLC.cs - 校园内网登录器 (Campus Network Login Client, CNLC)
//  针对沈阳城市学院校园网（1.1.1.3 portal 认证）定制
//  功能：登录窗口 / 托盘守护 / 断网自动重连 / 记住密码(跨电脑) /
//        开机自启动 / 开机自登录(静默) / 注销 / 状态窗口 / Toast 提示
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
        private const string PortalHost = "http://1.1.1.3";
        private const string PortalLoginPage = PortalHost + "/ac_portal/20240318141412/pc.html";
        private const string PortalLoginApi = PortalHost + "/ac_portal/login.php";
        private const string ProbeUrl = "https://www.baidu.com";
        private const int CheckIntervalSec = 30;
        private const int MaxLoginAttempts = 10;
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunName = "CNLC";
        // 通知总开关：false = 关闭所有弹动提示（浮条/托盘气泡），完全静默，仅写日志
        private static readonly bool ShowNotify = false;

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");
        private static readonly string StateFile = Path.Combine(BaseDir, "auto_login_status.txt");
        private static readonly string CredFile = Path.Combine(BaseDir, "CNLC.dat");

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
        private static readonly CookieContainer cookies = new CookieContainer();

        // 登录窗口控件（事件处理器需要访问）
        private static TextBox txtUser;
        private static TextBox txtPass;
        private static CheckBox chkRemember;
        private static CheckBox chkGuard;
        private static CheckBox chkAutoStart;
        private static CheckBox chkAutoLogin;
        private static bool suppressGuard = false;
        private static bool suppressAutoStart = false;
        private static bool suppressAutoLogin = false;

        // 已保存的凭据
        private static string savedUser = "";
        private static string savedPass = "";
        private static bool savedAutoLogin = false;

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls; } catch { }
            WebRequest.DefaultWebProxy = new WebProxy();   // 强制直连，绕过本地 VPN/代理
            Directory.CreateDirectory(LogDir);

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
                MessageBox.Show("校园内网登录器已在运行（右下角托盘图标）。\n\n如需切换账号，请先右键托盘图标退出，再重新启动。", "提示");
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

        private static void BuildLoginForm()
        {
            loginForm = new Form();
            loginForm.Text = "校园内网登录器";
            loginForm.ClientSize = new Size(320, 215);
            loginForm.FormBorderStyle = FormBorderStyle.FixedSingle;
            loginForm.StartPosition = FormStartPosition.CenterScreen;
            loginForm.MaximizeBox = false;
            loginForm.TopMost = true;
            try { loginForm.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label lblUser = NewLabel("工号:", 25, 15);
            txtUser = new TextBox();
            txtUser.Location = new Point(25, 35);
            txtUser.Size = new Size(270, 23);

            Label lblPass = NewLabel("密码:", 25, 64);
            txtPass = new TextBox();
            txtPass.Location = new Point(25, 84);
            txtPass.Size = new Size(270, 23);
            txtPass.PasswordChar = '*';

            Button btnLogin = new Button();
            btnLogin.Text = "登 录";
            btnLogin.Location = new Point(25, 116);
            btnLogin.Size = new Size(130, 30);

            Button btnLogout = new Button();
            btnLogout.Text = "注 销";
            btnLogout.Location = new Point(165, 116);
            btnLogout.Size = new Size(130, 30);

            chkRemember = new CheckBox();
            chkRemember.Text = "记住密码";
            chkRemember.Location = new Point(25, 156);
            chkRemember.AutoSize = true;

            chkGuard = new CheckBox();
            chkGuard.Text = "断网自动重连";
            chkGuard.Location = new Point(165, 156);
            chkGuard.AutoSize = true;

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "开机自启动";
            chkAutoStart.Location = new Point(25, 182);
            chkAutoStart.AutoSize = true;

            chkAutoLogin = new CheckBox();
            chkAutoLogin.Text = "开机自登录";
            chkAutoLogin.Location = new Point(165, 182);
            chkAutoLogin.AutoSize = true;

            loginForm.Controls.Add(lblUser);
            loginForm.Controls.Add(txtUser);
            loginForm.Controls.Add(lblPass);
            loginForm.Controls.Add(txtPass);
            loginForm.Controls.Add(btnLogin);
            loginForm.Controls.Add(btnLogout);
            loginForm.Controls.Add(chkRemember);
            loginForm.Controls.Add(chkGuard);
            loginForm.Controls.Add(chkAutoStart);
            loginForm.Controls.Add(chkAutoLogin);
            loginForm.AcceptButton = btnLogin;

            // 已记住密码：自动填入并勾选
            if (savedUser.Length > 0)
            {
                txtUser.Text = savedUser;
                txtPass.Text = savedPass;
                chkRemember.Checked = true;
            }
            if (savedAutoLogin) chkAutoLogin.Checked = true;
            suppressAutoStart = true; chkAutoStart.Checked = AutoStartEnabled(); suppressAutoStart = false;
            chkGuard.Checked = true;

            btnLogin.Click += delegate { BtnLoginClick(); };
            btnLogout.Click += delegate { DoLogout(); };

            chkGuard.CheckedChanged += delegate
            {
                if (suppressGuard) return;
                if (chkGuard.Checked)
                {
                    if (portalUser.Length == 0)
                    {
                        suppressGuard = true; chkGuard.Checked = false; suppressGuard = false;
                        MessageBox.Show("请先登录，再开启守护。", "提示");
                    }
                    else { loginForm.Hide(); GuardStart(); }
                }
                else StopGuard();
            };

            chkAutoStart.CheckedChanged += delegate
            {
                if (suppressAutoStart) return;
                try
                {
                    SetAutoStart(chkAutoStart.Checked);
                    Log(chkAutoStart.Checked ? "已开启开机自启动" : "已取消开机自启动");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置开机自启动失败：" + ex.Message, "错误");
                    suppressAutoStart = true; chkAutoStart.Checked = !chkAutoStart.Checked; suppressAutoStart = false;
                }
            };

            chkAutoLogin.CheckedChanged += delegate
            {
                if (suppressAutoLogin) return;
                if (chkAutoLogin.Checked && !File.Exists(CredFile))
                {
                    suppressAutoLogin = true; chkAutoLogin.Checked = false; suppressAutoLogin = false;
                    MessageBox.Show("开机自登录需要先勾选\"记住密码\"保存账号。", "提示");
                    return;
                }
                if (File.Exists(CredFile))
                {
                    try
                    {
                        string content = File.ReadAllText(CredFile);
                        string val = chkAutoLogin.Checked ? "true" : "false";
                        if (Regex.IsMatch(content, "\"autoLogin\"\\s*:"))
                            content = Regex.Replace(content, "\"autoLogin\"\\s*:\\s*(true|false)", "\"autoLogin\": " + val);
                        else
                            content = content.TrimEnd().TrimEnd('}') + ",\n  \"autoLogin\": " + val + "\n}";
                        WriteCredFile(content);
                        Log(chkAutoLogin.Checked ? "已开启开机自登录" : "已关闭开机自登录");}
                    catch { }
                }
            };

            loginForm.FormClosed += delegate { ExitApp(); };
        }

        private static void BtnLoginClick()
        {
            string u = txtUser.Text.Trim();
            string p = txtPass.Text;
            if (u.Length == 0 || p.Length == 0)
            {
                MessageBox.Show("请填写账号和密码。", "提示");
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

            if (chkGuard.Checked)
            {
                // 登录 + 开启守护：隐藏窗口，立即认证并进入守护
                loginForm.Hide();
                GuardStart();
            }
            else
            {
                // 仅登录：只认证上网，不开守护（窗口保持打开，浮条提示自动消失）
                Log("正在登录认证（仅登录，守护未开启）...");
                PortalLogin();
                System.Threading.Thread.Sleep(1000);
                if (TestOffline())
                {
                    Log("登录未成功：服务器可能暂时拒绝，请稍后重试或检查账号密码。");
                    Toast("登录未成功，请稍后重试", true);
                }
                else
                {
                    ExtendedRetry = false;
                    SaveState(true);
                    Log("登录成功，网络已恢复！（守护未开启）");
                    Toast("登录成功，网络已恢复", false);
                    if (tray != null) Balloon("登录成功，网络已恢复！（守护未开启）", false);
                }
            }
        }

        // ================= 托盘守护 =================

        private static void EnsureTray()
        {
            if (tray != null) return;

            Log("校园内网登录器已启动（托盘守护）");
            Log("账号: " + portalUser + "   检测间隔: " + CheckIntervalSec + "s");
            Log("目标服务器: " + PortalHost + "   登录接口: " + PortalLoginApi);

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Shield; }
            tray.Text = "校园内网登录器 - " + portalUser + "（每 " + CheckIntervalSec + "s 检测）";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            miGuard = new ToolStripMenuItem("断网自动重连");
            miGuard.Checked = true;
            miGuard.Click += delegate { SetGuard(!GuardOn); };
            ToolStripMenuItem miSwitch = new ToolStripMenuItem("切换账号");
            miSwitch.Click += delegate
            {
                // 切换账号 = 暂停检测、弹回登录窗；不动"断网自动重连"开关，
                // 重新登录后守护按登录窗里的勾选状态自动恢复
                if (timer != null) timer.Stop();
                GuardOn = false;
                tray.Text = "校园内网登录器 - 待重新登录";
                txtUser.Text = portalUser;
                txtPass.Text = portalPass;
                chkRemember.Checked = File.Exists(CredFile);
                loginForm.Show();
            };
            ToolStripMenuItem miLogout = new ToolStripMenuItem("注销");
            miLogout.Click += delegate { DoLogout(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };

            menu.Items.Add(miGuard);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miSwitch);
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
                Balloon("登录成功，网络已恢复！", false);
            }

            GuardOn = true;
            if (miGuard != null) miGuard.Checked = true;
            timer.Start();
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

        private static void Tick()
        {
            string now = Now();
            bool wasOffline = TestOffline();
            if (wasOffline)
            {
                Balloon("检测到断网，正在自动重新认证 ...", true);
                ReloginLoop();
                if (!TestOffline()) Balloon("登录成功，网络已恢复！", false);
            }
            else
            {
                Log("网络正常，无需操作 ...");
                SaveState(false);
            }
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
            if (portalUser.Length == 0)
            {
                MessageBox.Show("尚未登录，无需注销。", "提示");
                return;
            }
            DialogResult r = MessageBox.Show(
                "确定注销账号 " + portalUser + " 吗？\n注销后网络将断开，守护暂停。", "确认注销",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            StopGuard();
            bool ok = PortalLogout();
            if (ok)
            {
                // portal 服务器注销后有短暂冷却期 → 开启加密重试模式
                ExtendedRetry = true;
                Log("注销成功，网络已断开。");
                Balloon("账号 " + portalUser + " 已注销，网络已断开", false);
                tray.Text = "校园内网登录器 - 已注销";
            }
            else
            {
                Log("注销请求未成功（可能已离线或被服务器拒绝）");
                Balloon("注销请求未成功（可能已离线）", true);
            }
            txtUser.Text = portalUser;
            txtPass.Text = portalPass;
            loginForm.Show();
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
                MessageBox.Show("尚未登录，守护未启动。", "状态");
                return;
            }
            // 单实例：状态窗已打开时不重复弹
            if (statusForm != null && !statusForm.IsDisposed)
            {
                statusForm.Activate();
                return;
            }
            bool offline = TestOffline();
            string lastCheck = "", lastLogin = "";
            try
            {
                if (File.Exists(StateFile))
                {
                    string c = File.ReadAllText(StateFile);
                    Match m = Regex.Match(c, "\"last_check\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success) lastCheck = m.Groups[1].Value;
                    m = Regex.Match(c, "\"last_login_at\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success) lastLogin = m.Groups[1].Value;
                }
            }
            catch { }

            statusForm = new Form();
            statusForm.Text = "运行状态";
            statusForm.ClientSize = new Size(300, 185);
            statusForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            statusForm.StartPosition = FormStartPosition.CenterScreen;
            statusForm.MaximizeBox = false;
            statusForm.TopMost = true;

            string[] lines = new string[] {
                "当前账号：" + portalUser,
                "网络状态：" + (offline ? "已断网" : "在线"),
                "检测间隔：" + CheckIntervalSec + " 秒",
                "最近检测：" + lastCheck,
                "最近登录：" + lastLogin
            };
            int y = 15;
            foreach (string t in lines)
            {
                Label lb = NewLabel(t, 20, y);
                statusForm.Controls.Add(lb);
                y += 24;
            }
            Button ok = new Button();
            ok.Text = "确定";
            ok.Location = new Point(110, 150);
            ok.Size = new Size(80, 26);
            ok.Click += delegate { statusForm.Close(); };
            statusForm.Controls.Add(ok);
            statusForm.AcceptButton = ok;
            // 关键：ShowDialog 的窗体 Close 后不会自动 Dispose，IsDisposed 仍为 false，
            // 若不清理引用，下次左键会被单实例守卫误判为"已打开"而只 Activate（无反应）
            statusForm.FormClosed += delegate
            {
                try { statusForm.Dispose(); } catch { }
                statusForm = null;
            };
            statusForm.ShowDialog();
        }

        private static void Toast(string message, bool isError)
        {
            if (!ShowNotify) return;
            Form tf = new Form();
            tf.FormBorderStyle = FormBorderStyle.None;
            tf.StartPosition = FormStartPosition.CenterScreen;
            tf.Size = new Size(300, 52);
            tf.TopMost = true;
            tf.ShowInTaskbar = false;
            tf.BackColor = isError ? Color.FromArgb(198, 40, 40) : Color.FromArgb(46, 125, 50);
            Label lb = new Label();
            lb.Text = message;
            lb.Dock = DockStyle.Fill;
            lb.ForeColor = Color.White;
            lb.TextAlign = ContentAlignment.MiddleCenter;
            lb.AutoSize = false;
            tf.Controls.Add(lb);
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 2500;
            t.Tick += delegate { tf.Close(); };
            t.Start();
            tf.ShowDialog();
            t.Stop();
            t.Dispose();
        }

        // ================= 认证核心 =================

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static void Log(string msg)
        {
            try
            {
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
                if (url.IndexOf("1.1.1.3", StringComparison.Ordinal) >= 0)
                    Log("请求异常: " + ex.Message);
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
                Log("请求异常: " + ex.Message);
                return null;
            }
        }

        private static bool PortalLogin()
        {
            string rckey = UnixMillis().ToString();
            string enc = Rc4Hex(portalPass, rckey);
            string body = "opr=pwdLogin&userName=" + Uri.EscapeDataString(portalUser) +
                          "&pwd=" + enc + "&auth_tag=" + rckey + "&rememberPwd=0";
            HttpGet(PortalLoginPage, 8);   // 先取会话 cookie
            string resp = HttpPost(PortalLoginApi, body, 15, PortalLoginPage);
            if (resp != null && Regex.IsMatch(resp, "\"success\"\\s*:\\s*true")) return true;
            if (resp != null) Log("登录返回: " + resp);
            return false;
        }

        private static bool PortalLogout()
        {
            string resp = HttpPost(PortalLoginApi, "opr=logout", 15, PortalLoginPage);
            Log("注销返回: " + (resp == null ? "无响应" : resp));
            return resp != null && Regex.IsMatch(resp, "\"success\"\\s*:\\s*true");
        }

        private static bool TestOffline()
        {
            try
            {
                string s = HttpGet(ProbeUrl, 6);
                return !(s != null && s.Contains("baidu"));
            }
            catch { return true; }
        }

        // ================= 状态 / 凭据 / 自启动 =================

        private static void SaveState(bool loginSuccess)
        {
            string prev = "";
            try
            {
                if (File.Exists(StateFile))
                {
                    string c = File.ReadAllText(StateFile);
                    Match m = Regex.Match(c, "\"last_login_at\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success) prev = m.Groups[1].Value;
                }
            }
            catch { }
            string now = Now();
            string content = "{\n  \"last_check\": \"" + now + "\",\n  \"online_status\": \"ONLINE\",\n  \"last_login_at\": \"" +
                             (loginSuccess ? now : prev) + "\"\n}";
            try { File.WriteAllText(StateFile, content, Encoding.UTF8); } catch { }
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
            if (File.Exists(CredFile)) HideCredFile();
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

        private static void HideCredFile()
        {
            // 隐藏 + 系统属性：避免被随手看到/误删
            try { File.SetAttributes(CredFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
        }

        private static void WriteCredFile(string content)
        {
            // 注意：.NET 的 File.WriteAllText 无法覆盖写入隐藏文件（抛 UnauthorizedAccessException），
            // 必须先去掉属性，写完再恢复隐藏
            try { File.SetAttributes(CredFile, FileAttributes.Archive); } catch { }
            File.WriteAllText(CredFile, content, Encoding.UTF8);
            HideCredFile();
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
    }
}
