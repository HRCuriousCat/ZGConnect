using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace ZGConnect.PhotogrammetryStreaming
{
    public sealed class Google3DTilesSession
    {
        public const float SessionDurationSeconds = 3f * 3600f - 15f * 60f;
        const int MaxSessionTokenLength = 512;

        public string SessionToken { get; private set; }
        public string ApiKey { get; private set; }
        public string LastRootJson { get; private set; }
        public DateTime SessionStartedUtc { get; private set; }
        public bool IsActive => !string.IsNullOrEmpty(SessionToken);

        // Stop at &, ", whitespace — avoids swallowing the rest of a large JSON body.
        static readonly Regex SessionInUrlRegex = new(@"[?&]session=([^&""\s]+)", RegexOptions.Compiled);

        public IEnumerator StartSessionCoroutine(PhotogrammetryApiSettings settings, Action<string> onError)
        {
            ApiKey = settings.apiKey?.Trim() ?? "";
            string url = settings.BuildRootRequestUrl();

            using var req = UnityWebRequest.Get(url);
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string body = req.downloadHandler?.text ?? "";
                if (req.responseCode == 403)
                    onError?.Invoke($"HTTP 403 — EEA or API access blocked. Body: {Truncate(body, 400)}");
                else
                    onError?.Invoke($"Root tileset failed ({req.responseCode}): {req.error}. {Truncate(body, 200)}");
                yield break;
            }

            LastRootJson = req.downloadHandler.text;
            SessionToken = CoalesceSession(
                ExtractSessionFromUrl(req.url),
                ExtractSessionFromUrl(url),
                ExtractSessionFromTilesetJson(LastRootJson));

            SessionStartedUtc = DateTime.UtcNow;
            Debug.Log($"[Photogrammetry] Session started. Token length: {SessionToken?.Length ?? 0}");
        }

        public bool NeedsRefresh() =>
            IsActive && (DateTime.UtcNow - SessionStartedUtc).TotalSeconds >= SessionDurationSeconds;

        public string AppendSessionAndKey(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return uri;

            string absolute = ToAbsoluteGoogleUrl(uri);
            SplitUrl(absolute, out string path, out var query);

            if (!string.IsNullOrEmpty(SessionToken) && !query.ContainsKey("session"))
                query["session"] = SessionToken;

            if (!string.IsNullOrEmpty(ApiKey) && !query.ContainsKey("key"))
                query["key"] = ApiKey;

            return BuildUrl(path, query);
        }

        static string CoalesceSession(params string[] candidates)
        {
            foreach (string c in candidates)
            {
                if (IsValidSessionToken(c))
                    return c;
            }
            return null;
        }

        static bool IsValidSessionToken(string token) =>
            !string.IsNullOrWhiteSpace(token)
            && token.Length <= MaxSessionTokenLength
            && !token.Contains("{")
            && !token.Contains("\"");

        static string ExtractSessionFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            var m = SessionInUrlRegex.Match(url);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        static string ExtractSessionFromTilesetJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            var m = SessionInUrlRegex.Match(json);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        static string ToAbsoluteGoogleUrl(string uri)
        {
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return uri;
            return "https://tile.googleapis.com" + (uri.StartsWith("/") ? uri : "/" + uri);
        }

        static void SplitUrl(string url, out string path, out Dictionary<string, string> query)
        {
            query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = url.IndexOf('?', StringComparison.Ordinal);
            if (q < 0)
            {
                path = url;
                return;
            }

            path = url.Substring(0, q);
            foreach (string pair in url.Substring(q + 1).Split('&'))
            {
                if (string.IsNullOrEmpty(pair))
                    continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, eq));
                string val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                query[key] = val;
            }
        }

        static string BuildUrl(string path, Dictionary<string, string> query)
        {
            if (query.Count == 0)
                return path;

            var sb = new StringBuilder(path);
            sb.Append('?');
            bool first = true;
            foreach (var kv in query)
            {
                if (!first)
                    sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            return sb.ToString();
        }

        static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
