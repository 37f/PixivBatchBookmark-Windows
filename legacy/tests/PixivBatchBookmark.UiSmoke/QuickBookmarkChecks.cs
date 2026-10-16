using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PixivBatchBookmark.Core;
using PixivBatchBookmark.Windows;

internal static class QuickBookmarkChecks
{
    public static void Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var httpField = typeof(MainWindow).GetField("_http", flags)!;
        var sessionField = typeof(MainWindow).GetField("_session", flags)!;
        var setBusy = typeof(MainWindow).GetMethod("SetBusy", flags)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var publicButton = window.FindName("QuickPublicButton") as Button;
        var privateButton = window.FindName("QuickPrivateButton") as Button;
        check(publicButton is not null && privateButton is not null, "both quick bookmark buttons exist");
        check(!publicButton!.IsEnabled && !privateButton!.IsEnabled, "quick writes disabled before login");
        var originalHttp = httpField.GetValue(window);
        var originalSession = sessionField.GetValue(window);
        try
        {
            foreach (var wanted in new[] { false, true })
            {
                using var fixture = new QuickFixture();
                using var http = new HttpClient(fixture);
                httpField.SetValue(window, http);
                sessionField.SetValue(window, new LoginSession("42", "Fixture", "fixture-csrf"));
                setBusy.Invoke(window, [false]);
                Control<TextBox>("InputBox").Text = "pixiv id：97003966\n97003966";
                Control<RadioButton>(wanted ? "PublicMode" : "PrivateMode").IsChecked = true;
                var button = wanted ? privateButton! : publicButton!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(!publicButton!.IsEnabled && !privateButton!.IsEnabled, "both quick buttons disabled during task");
                // Deliberately raise an event even while disabled to check the handler's guard.
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(fixture.HomeReads == 1, "duplicate activation cannot start a second batch");
                fixture.ReleaseLogin.SetResult(true);
                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (button.IsEnabled || DateTime.UtcNow >= deadline) frame.Continue = false;
                };
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                check(button.IsEnabled, "quick task completes and re-enables its buttons");
                check(fixture.Writes == 1 && fixture.Private == wanted, $"quick {(wanted ? "private" : "public")} sends the explicit target mode");
                check(Control<ListBox>("IdList").Items.Count == 1 && Control<TextBlock>("Summary").Text.Contains("成功 1"), "quick action parses current input and deduplicates IDs");
                check(Control<RadioButton>("PrivateMode").IsChecked == wanted, "quick target is reflected in selected mode");
            }
        }
        finally
        {
            httpField.SetValue(window, originalHttp);
            sessionField.SetValue(window, originalSession);
            setBusy.Invoke(window, [false]);
        }
        check(!publicButton!.IsEnabled && !privateButton!.IsEnabled, "quick buttons return to logged-out state");
    }

    private sealed class QuickFixture : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> ReleaseLogin = new();
        public bool? Private;
        public int Writes;
        public int HomeReads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/")
            {
                HomeReads++;
                await ReleaseLogin.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new StringContent("<meta name='global-data' content='{\"token\":\"fixture-csrf\",\"userData\":{\"id\":\"42\",\"name\":\"Fixture\"}}'>") };
            }
            if (path == "/ajax/illust/97003966")
                return Json(new { error = false, body = new { bookmarkData = Private is null ? null : new { id = "55", @private = Private.Value } } });
            if (path == "/ajax/illusts/bookmarks/add" && request.Method == HttpMethod.Post)
            {
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (data.RootElement.GetProperty("illust_id").GetString() != "97003966")
                    throw new InvalidOperationException("Unexpected artwork ID");
                Private = data.RootElement.GetProperty("restrict").GetInt32() == 1;
                Writes++;
                return Json(new { error = false, body = new { } });
            }
            throw new InvalidOperationException("Unexpected fixture request: " + path);
        }

        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
