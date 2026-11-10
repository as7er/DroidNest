using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace DroidNest
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool firstInstance;
            using (Mutex instance = new Mutex(true, @"Local\DroidNest.AndroidPhone.v1", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("安卓小窝已经在运行。请切换到已打开的控制面板；同一时间只能运行一个实例。",
                        "DroidNest / 安卓小窝", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }

                try
                {
                    string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                    Application app = new Application();
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(new MainWindow(root));
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("安卓小窝无法启动。\n\n" + ex.Message,
                        "DroidNest / 安卓小窝", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }
    }
}
