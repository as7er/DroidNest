using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DroidNest
{
    public sealed class MainWindow : Window
    {
        private enum PhoneState { Idle, Checking, Booting, Running, Stopping, Error }

        private readonly AndroidRuntime runtime;
        private readonly Brush background = ColorBrush("#151B20");
        private readonly Brush surface = ColorBrush("#20292F");
        private readonly Brush foreground = ColorBrush("#E5ECEF");
        private readonly Brush secondary = ColorBrush("#A8B8C0");
        private readonly Brush accent = ColorBrush("#7DC7B5");
        private readonly List<Button> phoneButtons = new List<Button>();
        private readonly Queue<string> pendingLogs = new Queue<string>();
        private readonly object logLock = new object();
        private readonly DispatcherTimer logTimer;
        private readonly TextBlock status;
        private readonly TextBlock detail;
        private readonly TextBox logBox;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Button installButton;
        private readonly Button checkButton;
        private readonly Button dataButton;
        private readonly PhoneDisplayHost phoneDisplay;
        private readonly TextBlock phonePlaceholder;
        private readonly Button fullscreenButton;
        private bool fullscreen;
        private Rect normalBounds;
        private WindowState previousWindowState;
        private WindowStyle previousWindowStyle;
        private ResizeMode previousResizeMode;
        private PhoneState state = PhoneState.Idle;
        private bool ready;
        private bool commandBusy;
        private bool installing;
        private bool closeRequested;
        private bool closeApproved;
        private bool closingInProgress;
        private bool runtimeDisposed;
        private Task activeCommand = Task.FromResult(0);

        public MainWindow(string root)
        {
            runtime = new AndroidRuntime(root);
            Title = "DroidNest / 安卓小窝";
            Width = 1200;
            Height = 820;
            MinWidth = 960;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = background;
            Foreground = foreground;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 14;
            AllowDrop = true;

            Grid layout = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = layout;

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel identity = new StackPanel();
            identity.Children.Add(Label("DroidNest / 安卓小窝", 25, foreground, FontWeights.SemiBold));
            identity.Children.Add(Label("本地 Android 手机 · 无广告 · 无登录 · 无遥测", 12, secondary, FontWeights.Normal));
            header.Children.Add(identity);
            fullscreenButton = MakeButton("全屏 F11", "fullscreen", false);
            fullscreenButton.VerticalAlignment = VerticalAlignment.Center;
            fullscreenButton.Margin = new Thickness(12, 0, 0, 0);
            fullscreenButton.Click += delegate { ToggleFullscreen(); };
            Grid.SetColumn(fullscreenButton, 1);
            header.Children.Add(fullscreenButton);
            layout.Children.Add(header);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(332) });
            Grid.SetRow(body, 1);
            layout.Children.Add(body);

            Border phoneCard = Card();
            phoneCard.Padding = new Thickness(1);
            phoneCard.Background = Brushes.Black;
            phoneCard.Margin = new Thickness(0, 0, 16, 0);
            Grid phoneArea = new Grid();
            phonePlaceholder = Label("手机未启动\n\n点击右侧「启动手机」\n真实 Android 画面将在这里显示", 17, secondary, FontWeights.Normal);
            phonePlaceholder.TextAlignment = TextAlignment.Center;
            phonePlaceholder.TextWrapping = TextWrapping.Wrap;
            phonePlaceholder.Margin = new Thickness(24);
            phonePlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
            phonePlaceholder.VerticalAlignment = VerticalAlignment.Center;
            phoneArea.Children.Add(phonePlaceholder);
            phoneDisplay = new PhoneDisplayHost { Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(phoneDisplay, "phone");
            AutomationProperties.SetName(phoneDisplay, "Android 手机画面");
            phoneArea.Children.Add(phoneDisplay);
            phoneCard.Child = phoneArea;
            body.Children.Add(phoneCard);

            StackPanel sidebar = new StackPanel();
            ScrollViewer sidebarScroll = new ScrollViewer
            {
                Content = sidebar,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 0)
            };
            Grid.SetColumn(sidebarScroll, 1);
            body.Children.Add(sidebarScroll);

            Border stateCard = Card();
            stateCard.Margin = new Thickness(0, 0, 0, 12);
            StackPanel stateContent = new StackPanel();
            stateContent.Children.Add(Label("手机状态", 12, secondary, FontWeights.Normal));
            status = Label("等待检查", 22, accent, FontWeights.SemiBold);
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 5, 0, 5);
            AutomationProperties.SetAutomationId(status, "status");
            AutomationProperties.SetName(status, status.Text);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            stateContent.Children.Add(status);
            detail = Label("正在准备本地 Android 运行环境…", 13, secondary, FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            stateContent.Children.Add(detail);
            stateCard.Child = stateContent;
            sidebar.Children.Add(stateCard);

            Border controls = Card();
            controls.Margin = new Thickness(0, 0, 0, 12);
            StackPanel controlContent = new StackPanel();
            WrapPanel lifecycle = new WrapPanel();
            startButton = MakeButton("启动手机", "start", true);
            stopButton = MakeButton("停止手机", "stop", false);
            installButton = MakeButton("安装 APK…", "install", false);
            checkButton = MakeButton("检查环境", "check", false);
            dataButton = MakeButton("打开数据目录", "data", false);
            lifecycle.Children.Add(startButton);
            lifecycle.Children.Add(stopButton);
            lifecycle.Children.Add(installButton);
            lifecycle.Children.Add(checkButton);
            controlContent.Children.Add(lifecycle);
            TextBlock navigationTitle = Label("手机操作", 12, secondary, FontWeights.Normal);
            navigationTitle.Margin = new Thickness(0, 9, 0, 6);
            controlContent.Children.Add(navigationTitle);
            WrapPanel navigation = new WrapPanel();
            AddPhoneButton(navigation, "主屏幕", "home", delegate { return runtime.KeyAsync(3); });
            AddPhoneButton(navigation, "返回", "back", delegate { return runtime.KeyAsync(4); });
            AddPhoneButton(navigation, "最近任务", "recents", delegate { return runtime.KeyAsync(187); });
            AddPhoneButton(navigation, "旋转屏幕", "rotate", delegate { return runtime.RotateAsync(); });
            AddPhoneButton(navigation, "系统设置", "settings", delegate { return runtime.SettingsAsync(); });
            navigation.Children.Add(dataButton);
            controlContent.Children.Add(navigation);
            TextBlock dropHint = Label("直接点击手机画面使用鼠标和键盘。安装应用可选择 APK，或将一个 .apk 文件拖到右侧面板。", 12, secondary, FontWeights.Normal);
            dropHint.TextWrapping = TextWrapping.Wrap;
            dropHint.Margin = new Thickness(0, 7, 0, 0);
            controlContent.Children.Add(dropHint);
            controls.Child = controlContent;
            sidebar.Children.Add(controls);

            logBox = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                Background = ColorBrush("#10161A"),
                Foreground = secondary,
                BorderBrush = ColorBrush("#34424B"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                Height = 180,
                Margin = new Thickness(0, 8, 0, 0),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetAutomationId(logBox, "diagnostics");
            Expander diagnostics = new Expander
            {
                Header = "运行记录 / 诊断",
                Foreground = secondary,
                IsExpanded = false,
                Content = logBox
            };
            sidebar.Children.Add(diagnostics);
            TextBlock footer = Label("应用与手机数据保存在本机。关闭此窗口会安全停止手机。\n全屏：F11；退出全屏：Esc。", 12, secondary, FontWeights.Normal);
            footer.Margin = new Thickness(0, 12, 0, 0);
            footer.TextWrapping = TextWrapping.Wrap;
            sidebar.Children.Add(footer);

            runtime.Log += OnRuntimeLog;
            phoneDisplay.DisplayError += OnDisplayError;
            phoneDisplay.FullscreenRequested += ToggleFullscreen;
            logTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            logTimer.Interval = TimeSpan.FromMilliseconds(150);
            logTimer.Tick += delegate
            {
                FlushLogs();
                if (!commandBusy && !closeRequested && !runtimeDisposed &&
                    (state == PhoneState.Running || phoneDisplay.IsAttached) && !runtime.IsRunning)
                {
                    DetachPhoneDisplay();
                    state = PhoneState.Idle;
                    SetStatus(StateText(state), "Android 运行环境已退出。可以重新启动；应用与数据仍保存在本机。");
                    UpdateControls();
                }
            };
            logTimer.Start();
            startButton.Click += async delegate { await RunCommandAsync("启动手机", PhoneState.Booting, StartPhoneAsync); };
            stopButton.Click += async delegate { await RunCommandAsync("停止手机", PhoneState.Stopping, StopPhoneAsync); };
            checkButton.Click += async delegate { await RunCommandAsync("检查环境", PhoneState.Checking, CheckRuntimeAsync); };
            installButton.Click += async delegate { await ChooseApkAsync(); };
            dataButton.Click += delegate { OpenDataDirectory(); };
            PreviewDragOver += OnDragOver;
            PreviewDrop += OnDrop;
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            Loaded += async delegate { await RunCommandAsync("启动手机", PhoneState.Booting, StartPhoneAsync); };
            UpdateControls();
        }

        private void ToggleFullscreen()
        {
            if (!fullscreen)
            {
                previousWindowState = WindowState;
                previousWindowStyle = WindowStyle;
                previousResizeMode = ResizeMode;
                normalBounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                fullscreen = true;
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = previousWindowStyle;
                ResizeMode = previousResizeMode;
                Left = normalBounds.Left;
                Top = normalBounds.Top;
                Width = normalBounds.Width;
                Height = normalBounds.Height;
                WindowState = previousWindowState;
                fullscreen = false;
            }
            phoneDisplay.IsFullscreen = fullscreen;
            string label = fullscreen ? "退出全屏 F11" : "全屏 F11";
            fullscreenButton.Content = label;
            AutomationProperties.SetName(fullscreenButton, label);
            phoneDisplay.ResizeDisplay();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11 || (e.Key == Key.Escape && fullscreen))
            {
                if (!e.IsRepeat)
                    ToggleFullscreen();
                e.Handled = true;
            }
        }

        private Exception DetachPhoneDisplay()
        {
            Exception error = null;
            try
            {
                phoneDisplay.Detach();
            }
            catch (Exception ex)
            {
                error = ex;
                ReportError("手机画面分离失败", ex);
            }
            phoneDisplay.Visibility = Visibility.Collapsed;
            return error;
        }

        private void OnDisplayError(string message)
        {
            ReportError("手机画面错误", new InvalidOperationException(message));
        }

        private static Brush ColorBrush(string hex)
        {
            SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }

        private static TextBlock Label(string text, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = weight };
        }

        private Border Card()
        {
            return new Border
            {
                Background = surface,
                BorderBrush = ColorBrush("#34424B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(18, 14, 18, 14)
            };
        }

        private Button MakeButton(string text, string id, bool primary)
        {
            Button button = new Button
            {
                Content = text,
                MinWidth = 100,
                MinHeight = 38,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 7),
                Background = primary ? accent : ColorBrush("#2C3941"),
                Foreground = primary ? ColorBrush("#102B25") : foreground,
                BorderBrush = primary ? accent : ColorBrush("#53656E"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
            };
            // Keep native keyboard/focus behavior while giving all button states legible colors.
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, foreground, "ButtonBorder"));
            template.Triggers.Add(focus);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            template.Triggers.Add(disabled);
            button.Template = template;
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, text);
            return button;
        }

        private void AddPhoneButton(Panel panel, string label, string id, Func<Task> action)
        {
            Button button = MakeButton(label, id, false);
            button.Click += async delegate { await RunCommandAsync(label, PhoneState.Running, action); };
            panel.Children.Add(button);
            phoneButtons.Add(button);
        }

        private Task RunCommandAsync(string name, PhoneState busyState, Func<Task> action)
        {
            if (commandBusy || closeRequested || runtimeDisposed)
                return Task.FromResult(0);
            commandBusy = true;
            state = busyState;
            SetStatus(StateText(state), name + "，请稍候…");
            UpdateControls();
            activeCommand = ExecuteCommandAsync(name, action);
            return activeCommand;
        }

        private async Task ExecuteCommandAsync(string name, Func<Task> action)
        {
            OnRuntimeLog(name);
            try
            {
                await action();
                state = runtime.IsRunning ? PhoneState.Running : PhoneState.Idle;
                if (!closeRequested)
                    SetStatus(StateText(state), name == "检查环境" ? detail.Text : name + "已完成。");
            }
            catch (Exception ex)
            {
                state = PhoneState.Error;
                if (name == "检查环境" || name == "启动手机")
                    ready = false;
                ReportError(name + "失败", ex);
            }
            finally
            {
                commandBusy = false;
                UpdateControls();
            }
        }

        private async Task CheckRuntimeAsync()
        {
            ready = false;
            string report = await runtime.CheckAsync();
            ready = true;
            detail.Text = "运行环境就绪：Android 11 x86_64 / WHPX 硬件加速。可以启动手机。";
            detail.ToolTip = detail.Text;
            OnRuntimeLog(report);
        }

        private async Task StartPhoneAsync()
        {
            await runtime.StartAsync();
            if (closeRequested)
                return;
            Exception attachError = null;
            try
            {
                phoneDisplay.Visibility = Visibility.Visible;
                phoneDisplay.UpdateLayout();
                phoneDisplay.Attach(runtime.DisplayWindowHandle);
            }
            catch (Exception ex)
            {
                attachError = ex;
            }
            if (attachError != null)
            {
                DetachPhoneDisplay();
                try
                {
                    await runtime.StopAsync();
                }
                catch (Exception stopError)
                {
                    throw new AggregateException("手机画面接入失败，且停止运行环境失败。", attachError, stopError);
                }
                throw new InvalidOperationException("无法显示 Android 手机画面：" + attachError.Message, attachError);
            }
            ready = true;
        }

        private async Task StopPhoneAsync()
        {
            Exception detachError = DetachPhoneDisplay();
            await runtime.StopAsync();
            if (detachError != null)
                throw new InvalidOperationException("手机已停止，但画面分离失败：" + detachError.Message, detachError);
        }

        private async Task ChooseApkAsync()
        {
            if (!CanInstall())
                return;
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "选择要安装的 Android 应用",
                Filter = "Android 应用 (*.apk)|*.apk",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true)
                await InstallApkAsync(dialog.FileName);
        }

        private bool CanInstall()
        {
            return !commandBusy && !closeRequested && !runtimeDisposed && runtime.IsRunning;
        }

        private async Task InstallApkAsync(string path)
        {
            if (!CanInstall())
                return;
            if (!String.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                SetStatus("无法安装", "请选择一个存在的 .apk 文件；不支持 APK 压缩包或文件夹。");
                return;
            }
            installing = true;
            try
            {
                await RunCommandAsync("安装 " + Path.GetFileName(path), PhoneState.Running,
                    delegate { return runtime.InstallAsync(path); });
            }
            finally
            {
                installing = false;
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            bool apk = files != null && files.Length == 1 && String.Equals(Path.GetExtension(files[0]), ".apk", StringComparison.OrdinalIgnoreCase);
            e.Effects = CanInstall() && apk ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!CanInstall())
            {
                if (!commandBusy && !closeRequested)
                    SetStatus(StateText(state), "请先启动手机，再拖入 APK 安装。");
                return;
            }
            string[] files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            if (files == null || files.Length != 1)
            {
                SetStatus(StateText(state), "每次请拖入一个 .apk 文件。");
                return;
            }
            await InstallApkAsync(files[0]);
        }

        private void OpenDataDirectory()
        {
            try
            {
                Directory.CreateDirectory(runtime.DataDirectory);
                Process.Start(new ProcessStartInfo { FileName = runtime.DataDirectory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ReportError("无法打开数据目录", ex);
            }
        }

        private void UpdateControls()
        {
            bool available = !commandBusy && !closeRequested && !runtimeDisposed;
            bool running = !runtimeDisposed && runtime.IsRunning;
            if (!running && phoneDisplay.IsAttached)
                DetachPhoneDisplay();
            phonePlaceholder.Visibility = !running && state != PhoneState.Booting && state != PhoneState.Stopping
                ? Visibility.Visible : Visibility.Collapsed;
            startButton.IsEnabled = available && ready && !running;
            stopButton.IsEnabled = available && running;
            installButton.IsEnabled = available && running;
            checkButton.IsEnabled = available && !running;
            dataButton.IsEnabled = !closeRequested && !runtimeDisposed;
            foreach (Button button in phoneButtons)
                button.IsEnabled = available && running;
        }

        private static string StateText(PhoneState value)
        {
            switch (value)
            {
                case PhoneState.Checking: return "正在检查环境";
                case PhoneState.Booting: return "手机正在启动";
                case PhoneState.Running: return "手机运行中";
                case PhoneState.Stopping: return "正在停止手机";
                case PhoneState.Error: return "操作未完成";
                default: return "手机未启动";
            }
        }

        private void SetStatus(string title, string message)
        {
            status.Text = title;
            AutomationProperties.SetName(status, title);
            detail.Text = message;
            detail.ToolTip = message;
        }

        private void ReportError(string title, Exception error)
        {
            string message = error.Message;
            SetStatus(title, message + "  请查看运行记录；修复后重试或重新检查环境。");
            OnRuntimeLog(title + ": " + error.ToString());
        }

        private void OnRuntimeLog(string message)
        {
            if (String.IsNullOrEmpty(message))
                return;
            // Process output may arrive off-thread or faster than the UI can paint.
            lock (logLock)
            {
                while (pendingLogs.Count >= 400)
                    pendingLogs.Dequeue();
                string bounded = message.Length > 4000 ? message.Substring(0, 4000) + " [记录已截断]" : message;
                pendingLogs.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + bounded + Environment.NewLine);
            }
        }

        private void FlushLogs()
        {
            StringBuilder batch = new StringBuilder();
            lock (logLock)
            {
                while (pendingLogs.Count > 0)
                    batch.Append(pendingLogs.Dequeue());
            }
            if (batch.Length == 0)
                return;
            const int maxCharacters = 64000;
            string next = logBox.Text + batch.ToString();
            if (next.Length > maxCharacters)
            {
                int cut = next.IndexOf('\n', next.Length - maxCharacters);
                next = cut >= 0 && cut + 1 < next.Length ? next.Substring(cut + 1) : next.Substring(next.Length - maxCharacters);
            }
            bool atBottom = logBox.VerticalOffset + logBox.ViewportHeight >= logBox.ExtentHeight - 2;
            logBox.Text = next;
            if (atBottom)
                logBox.ScrollToEnd();
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (closeApproved)
                return;
            e.Cancel = true;
            if (closingInProgress)
                return;
            if (installing || state == PhoneState.Booting)
            {
                string operation = installing ? "应用正在安装" : "手机正在启动";
                MessageBoxResult answer = MessageBox.Show(this,
                    operation + "。关闭时会等待当前操作完成，再停止手机。是否继续关闭？",
                    "关闭安卓小窝", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                    return;
            }
            closingInProgress = true;
            closeRequested = true;
            UpdateControls();
            SetStatus("正在安全关闭", "等待当前操作结束，然后停止此实例的手机；请勿强制结束进程。");
            try
            {
                await activeCommand;
                state = PhoneState.Stopping;
                SetStatus("正在安全关闭", "正在停止此实例的手机并释放运行环境…");
                DetachPhoneDisplay();
                await runtime.StopAsync();
                await Task.Run(delegate { runtime.Dispose(); });
                runtime.Log -= OnRuntimeLog;
                runtimeDisposed = true;
                logTimer.Stop();
                FlushLogs();
                closeApproved = true;
                Close();
            }
            catch (Exception ex)
            {
                // Keep the window available if shutdown fails, rather than abandoning its phone.
                closeRequested = false;
                closingInProgress = false;
                state = PhoneState.Error;
                ReportError("关闭未完成", ex);
                UpdateControls();
            }
        }
    }
}
