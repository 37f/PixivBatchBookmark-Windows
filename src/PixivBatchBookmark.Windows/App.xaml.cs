using System.Threading;
using System.Windows;

namespace PixivBatchBookmark.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\PixivBatchBookmark.Windows.v1", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("程序已经运行，请使用已打开的窗口。", "Pixiv 批量收藏");
            Shutdown(); return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose(); base.OnExit(e);
    }
}
