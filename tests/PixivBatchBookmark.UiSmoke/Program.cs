using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using PixivBatchBookmark.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var window = new MainWindow();
        window.Show();
        var code = 0;
        window.Dispatcher.Invoke(() =>
        {
            try
            {
                void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
                T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var input = Control<TextBox>("InputBox");
                Check(window.IsVisible, "main window loaded");
                Check(!Control<Button>("BookmarkButton").IsEnabled && !Control<Button>("RemoveButton").IsEnabled, "writes disabled before login");
                Check(Control<RadioButton>("PublicMode").IsChecked == true && Control<RadioButton>("PrivateMode").IsChecked == false, "public is the only initial mode");
                Control<RadioButton>("PrivateMode").IsChecked = true;
                Check(Control<RadioButton>("PublicMode").IsChecked == false, "private selection clears public");
                Control<RadioButton>("PublicMode").IsChecked = true;
                Check(Control<RadioButton>("PrivateMode").IsChecked == false, "public selection clears private");
                input.Text = "97003966\n今天冷…-pixiv id：103816477\nhttps://www.pixiv.net/artworks/97720788\n97003966\nhttps://www.pixiv.net/users/123456";
                Click("ParseButton");
                Check(Control<ListBox>("IdList").Items.Count == 3, "parse populates three artwork IDs");
                Check(Control<TextBlock>("ParseStatus").Text.Contains("去重 1") && Control<TextBlock>("ParseStatus").Text.Contains("忽略 1"), "duplicates and ignored lines shown");
                Check(Control<TextBox>("LogBox").Text.Contains("解析得到 3"), "task log displays parse result");
                // Viewing the wallpaper must preserve the user's unfinished work.
                Check(window.FindName("WallpaperButton") is Button, "wallpaper toggle is available in the main window");
                var savedInput = input.Text;
                var savedRows = Control<ListBox>("IdList").Items.Cast<string>().ToArray();
                var savedLog = Control<TextBox>("LogBox").Text;
                Control<RadioButton>("PrivateMode").IsChecked = true;
                Control<ProgressBar>("TaskProgress").Maximum = 3;
                Control<ProgressBar>("TaskProgress").Value = 1;
                Click("WallpaperButton");
                Check(Control<Grid>("InterfacePanel").Visibility == Visibility.Collapsed && Control<System.Windows.Shapes.Rectangle>("WallpaperShade").Visibility == Visibility.Collapsed && Panel.GetZIndex(Control<Image>("WallpaperImage")) > Panel.GetZIndex(Control<Grid>("InterfacePanel")) && Control<Image>("WallpaperImage").HorizontalAlignment == HorizontalAlignment.Center, "wallpaper mode hides the interface and displays the foreground wallpaper");
                Check(Control<Button>("WallpaperButton").IsVisible && Control<Button>("WallpaperButton").IsEnabled && (string)Control<Button>("WallpaperButton").Content == "再见兽娘麻麻⊙﹏⊙", "visible restore button uses the requested label");
                if (args.Length > 0)
                {
                    window.UpdateLayout();
                    var wallpaperRoot = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                    var wallpaperBitmap = new RenderTargetBitmap((int)wallpaperRoot.ActualWidth, (int)wallpaperRoot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    wallpaperBitmap.Render(window); var wallpaperEncoder = new PngBitmapEncoder(); wallpaperEncoder.Frames.Add(BitmapFrame.Create(wallpaperBitmap));
                    var wallpaperPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, Path.GetFileNameWithoutExtension(args[0]) + "_wallpaper.png");
                    using var wallpaperOutput = File.Create(wallpaperPath); wallpaperEncoder.Save(wallpaperOutput);
                    Console.WriteLine("Wallpaper render: " + wallpaperPath);
                }
                Click("WallpaperButton");
                Check(Control<Grid>("InterfacePanel").Visibility == Visibility.Visible && Control<System.Windows.Shapes.Rectangle>("WallpaperShade").Visibility == Visibility.Visible && Panel.GetZIndex(Control<Image>("WallpaperImage")) < Panel.GetZIndex(Control<Grid>("InterfacePanel")) && Control<Image>("WallpaperImage").HorizontalAlignment == HorizontalAlignment.Right && (string)Control<Button>("WallpaperButton").Content == "看看兽娘麻麻˃ 𖥦 ˂ ", "leaving wallpaper mode restores the interface and background wallpaper");
                Check(input.Text == savedInput && Control<ListBox>("IdList").Items.Cast<string>().SequenceEqual(savedRows) && Control<TextBox>("LogBox").Text == savedLog && Control<RadioButton>("PrivateMode").IsChecked == true && Control<ProgressBar>("TaskProgress").Value == 1, "wallpaper round trip preserves input results logs mode and progress");
                Control<RadioButton>("PublicMode").IsChecked = true;
                Control<ProgressBar>("TaskProgress").Value = 0;
                Control<ProgressBar>("TaskProgress").Maximum = 1;
                if (args.Length > 0)
                {
                    window.UpdateLayout();
                    var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.GetFullPath(args[0])); encoder.Save(output);
                    Console.WriteLine("UI render: " + Path.GetFullPath(args[0]));
                }
                Click("ClearButton");
                Check(input.Text.Length == 0 && Control<ListBox>("IdList").Items.Count == 0, "clear resets input and preview");
                if (args.Contains("--webview"))
                {
                    var login = new LoginWindow { Owner = window };
                    login.Show();
                    var webview = (WebView2)login.FindName("Browser");
                    var frame = new DispatcherFrame();
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    timer.Tick += (_, _) => { if (webview.CoreWebView2 is not null || DateTime.UtcNow >= deadline) frame.Continue = false; };
                    timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                    Check(webview.CoreWebView2 is not null, "actual WebView2 runtime initializes official login window");
                    login.Close();
                    Check(!login.IsVisible, "login window closes after initialization");
                }
                window.Close();
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); code = 1; window.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        app.Shutdown();
        return code;
    }
}
