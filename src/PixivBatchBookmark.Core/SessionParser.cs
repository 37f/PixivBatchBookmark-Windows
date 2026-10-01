using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixivBatchBookmark.Core;

public static class SessionParser
{
    public static LoginSession Parse(string html)
    {
        foreach (Match meta in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = Attributes(meta.Value);
            if (!attributes.TryGetValue("name", out var name) || name != "global-data" || !attributes.TryGetValue("content", out var content)) continue;
            try
            {
                using var doc = ParseJson(content);
                var root = doc.RootElement;
                if (!root.TryGetProperty("token", out var token) || !root.TryGetProperty("userData", out var user) || user.ValueKind != JsonValueKind.Object) break;
                var id = user.GetProperty("id").ToString();
                var csrf = token.GetString() ?? "";
                if (!long.TryParse(id, out var userId) || userId <= 0 || !Regex.IsMatch(csrf, @"^[A-Za-z0-9_-]{3,256}$")) break;
                return new(id, user.TryGetProperty("name", out var username) ? username.GetString() ?? id : id, csrf);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { break; }
        }
        throw new PixivException("未检测到有效登录身份和收藏安全令牌。请完成网页登录，再打开 Pixiv 首页重试；若持续失败，网站页面结构可能已变化。");
    }

    private static JsonDocument ParseJson(string text)
    {
        // Some page versions wrap global-data in a JSON string as well as HTML escaping.
        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.String) return doc;
        var decoded = doc.RootElement.GetString()!;
        doc.Dispose();
        return JsonDocument.Parse(decoded);
    }

    public static BookmarkDetails ParseBookmarkDetails(string html)
    {
        string? tagText = null;
        var comment = "";
        foreach (Match input in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attr = Attributes(input.Value);
            if (!attr.TryGetValue("name", out var name)) continue;
            if (name == "tag" && attr.TryGetValue("value", out var value)) tagText = value;
            if (name == "comment" && attr.TryGetValue("value", out var note)) comment = note;
        }
        if (tagText is null) throw new PixivException("无法读取原收藏标签，未取消原收藏。请检查登录状态或网站接口变化。");
        return new(tagText.Split(' ', StringSplitOptions.RemoveEmptyEntries), comment);
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(tag, "([a-zA-Z][a-zA-Z0-9_-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))"))
            result[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value);
        return result;
    }
}
