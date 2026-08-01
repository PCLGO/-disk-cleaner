using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DiskCleanupAssistant.Cleanup;

namespace DiskCleanupAssistant
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            base.OnStartup(e);
            if (e.Args.Length >= 3 && e.Args[0] == "--elevated-executor")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Task.Run(async () =>
                {
                    var code = await ElevatedPipeProtocol.RunElevatedClientAsync(e.Args[1], e.Args[2]).ConfigureAwait(false);
                    Dispatcher.Invoke(() => Shutdown(code));
                });
                return;
            }

            var requestedPage = 0;
            if (e.Args.Length >= 2 && e.Args[0] == "--page") int.TryParse(e.Args[1], out requestedPage);
            var window = new MainWindow();
            MainWindow = window;
            window.NavigateToPage(requestedPage);
            window.Show();
        }
    }
}
