using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DroidNest
{
    internal sealed class SetupWindow : Window
    {
        private readonly string root, appRoot;
        private readonly RuntimeInstaller installer;
        private readonly bool repair;
        private readonly TextBox information;
        private readonly TextBlock status;
        private readonly CheckBox consent;
        private readonly Button action, cancel;
        private readonly ProgressBar progress;
        private CancellationTokenSource operation;
        private InstallationPlan plan;
        private bool busy, closing, accepted, migration;
        internal bool Completed { get; private set; }

        internal SetupWindow(string root, string appRoot, bool repair = false)
        {
            this.root = root; this.appRoot = appRoot;
            this.repair = repair;
            installer = new RuntimeInstaller(root, appRoot);
            Title = "DroidNest / 安卓小窝 · " + (repair ? "修复运行环境" : "首次安装");
            Width = 820; Height = 720; MinWidth = 640; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
            Background = new SolidColorBrush(Color.FromRgb(21, 27, 32));
            Foreground = new SolidColorBrush(Color.FromRgb(229, 236, 239));
            Grid layout = new Grid { Margin = new Thickness(24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;
            TextBlock heading = new TextBlock { Text = "准备你的 Android 手机", FontSize = 26, Margin = new Thickness(0, 0, 0, 12) };
            layout.Children.Add(heading);
            status = new TextBlock { Text = "检查系统条件…", TextWrapping = TextWrapping.Wrap, MaxHeight = 110, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetAutomationId(status, "setup-status");
            Grid.SetRow(status, 1); layout.Children.Add(status);
            information = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(32, 41, 47)), Foreground = Foreground,
                Text = "组件和手机数据保存在：\n" + root + "\n\n后续解压新版本会复用此目录，不需要重复安装。" };
            AutomationProperties.SetAutomationId(information, "setup-details");
            Grid.SetRow(information, 2); layout.Children.Add(information);
            consent = new CheckBox { Content = "我已阅读以上许可和安全说明，同意下载并在本机配置这些组件。", Foreground = Foreground,
                Margin = new Thickness(0, 14, 0, 12), IsEnabled = false };
            AutomationProperties.SetAutomationId(consent, "setup-consent");
            consent.Checked += delegate { UpdateAction(); }; consent.Unchecked += delegate { UpdateAction(); };
            Grid.SetRow(consent, 3); layout.Children.Add(consent);
            progress = new ProgressBar { Height = 5, Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(progress, 4); layout.Children.Add(progress);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button help = new Button { Content = "Windows 虚拟化设置", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 10, 0) };
            help.Click += delegate {
                try { Process.Start(new ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true }); }
                catch (Exception ex) { status.Text = "无法打开 Windows 功能：" + ex.Message; }
            };
            buttons.Children.Add(help);
            action = new Button { Content = "检查中…", Padding = new Thickness(18, 8, 18, 8), IsEnabled = false };
            AutomationProperties.SetAutomationId(action, "setup-action");
            action.Click += async delegate { await ExecuteAsync(); }; buttons.Children.Add(action);
            cancel = new Button { Content = "退出", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(10, 0, 0, 0) };
            AutomationProperties.SetAutomationId(cancel, "setup-cancel");
            cancel.Click += delegate { Close(); }; buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 5); layout.Children.Add(buttons);
            Loaded += async delegate { await ExecuteAsync(); };
            Closing += OnClosing;
        }

        private void UpdateAction()
        {
            action.IsEnabled = !busy && (plan == null && !migration || consent.IsChecked == true || accepted);
        }

        private async Task ExecuteAsync()
        {
            if (busy) return;
            busy = true; operation = new CancellationTokenSource();
            action.IsEnabled = false; consent.IsEnabled = false; progress.IsIndeterminate = true;
            cancel.Content = "取消并退出";
            Progress<string> updates = new Progress<string>(delegate(string text) { status.Text = text; });
            try
            {
                if (migration && consent.IsChecked == true)
                {
                    await AppPaths.ImportLegacyAsync(appRoot, updates, operation.Token);
                    migration = false; consent.IsChecked = false;
                }
                using (AndroidRuntime host = new AndroidRuntime(root))
                    await host.CheckInstallationHostAsync(operation.Token);
                if (AppPaths.HasLegacy(appRoot))
                {
                    migration = true;
                    status.Text = "发现旧版本的本地运行环境或手机数据";
                    information.Text = "来源：" + appRoot + "\n目标：" + root +
                        "\n\n将复制旧目录中尚未导入的 runtime 和 data，保留原文件，不覆盖目标中的已有数据。请先关闭旧版手机。复制期间需要足够的磁盘空间。\n\n以后更新客户端不再移动手机数据。";
                    consent.Content = "同意复制旧运行环境及手机数据到固定用户目录（不删除原文件）。";
                    action.Content = "保留数据并继续";
                }
                else if (!repair && RuntimeInstaller.IsInstalled(root))
                {
                    status.Text = "检查已安装组件与模拟器加速…";
                    using (AndroidRuntime runtime = new AndroidRuntime(root)) await runtime.CheckAsync();
                    Completed = true;
                }
                else if (plan == null)
                {
                    status.Text = "正在获取官方组件清单和许可，不会安装或运行下载内容…";
                    plan = await installer.GetPlanAsync(operation.Token);
                    information.Text = plan.Summary + "\n\n保存位置：" + root + "\n\n" + plan.Licenses;
                    consent.Content = "我已阅读以上许可和安全说明，同意下载并在本机配置这些组件。";
                    consent.IsChecked = false;
                    status.Text = "请阅读组件来源、许可与安全限制，再确认安装。";
                    action.Content = "同意并安装";
                }
                else
                {
                    if (!accepted && consent.IsChecked != true) throw new InvalidOperationException("请先确认组件许可。");
                    accepted = true;
                    await installer.InstallAsync(plan, updates, operation.Token);
                    status.Text = "组件准备完成，检查模拟器加速…";
                    using (AndroidRuntime runtime = new AndroidRuntime(root)) await runtime.CheckAsync();
                    Completed = true;
                }
            }
            catch (OperationCanceledException) { status.Text = "安装已取消。手机数据保持不变；重新打开后可继续。"; }
            catch (Exception ex)
            {
                status.Text = "未完成：" + ex.Message;
                information.Text += "\n\n最近错误：\n" + ex.Message + "\n\n修正网络或系统条件后点击重试；不会删除手机数据。";
                action.Content = "重试";
            }
            finally
            {
                busy = false; operation.Dispose(); operation = null;
                progress.IsIndeterminate = false; cancel.Content = "退出";
                consent.IsEnabled = (plan != null || migration) && !accepted;
                UpdateAction();
            }
            if (Completed || closing) Close();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!busy) return;
            e.Cancel = true; closing = true;
            status.Text = "正在停止安装并释放文件，请稍候…";
            cancel.IsEnabled = false; operation.Cancel();
        }
    }
}
