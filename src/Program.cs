using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VibeBar
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "VibeBar.SingleInstance", out created))
            {
                if (!created)
                {
                    Forms.MessageBox.Show(
                        "VibeBar 已在运行。",
                        "VibeBar",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Information);
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException +=
                    delegate(object sender, UnhandledExceptionEventArgs e)
                    {
                        Log.Exception(
                            "未处理异常（AppDomain）",
                            e.ExceptionObject as Exception);
                    };

                try
                {
                    Application app = new Application();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    AppController controller = new AppController(app);

                    app.DispatcherUnhandledException +=
                        delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                        {
                            controller.NotifyUnhandled(e.Exception);
                            // 托盘工具不弹模态框，记录后继续运行。
                            e.Handled = true;
                        };

                    app.Run();
                    GC.KeepAlive(controller);
                }
                catch (Exception exception)
                {
                    Log.Exception("VibeBar 启动或运行失败", exception);
                }

                GC.KeepAlive(mutex);
            }
        }
    }
}
