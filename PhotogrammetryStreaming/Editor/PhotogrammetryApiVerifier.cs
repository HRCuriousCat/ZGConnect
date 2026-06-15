using System;
using System.Net;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ZGConnect.PhotogrammetryStreaming.Editor
{
    public static class PhotogrammetryApiVerifier
    {
        const string PrefsKey = "ZGConnect.Photogrammetry.ApiKey";

        [MenuItem("ZG Connect/Photogrammetry/Verify Google 3D Tiles API")]
        public static void OpenVerifierWindow()
        {
            PhotogrammetryStreamingWindow.Open(0);
        }

        public static void RunVerification(string apiKey, Action<VerificationResult> onComplete)
        {
            apiKey = NormalizeApiKey(apiKey);
            EditorCoroutineRunner.Run(VerifyCoroutine(apiKey, onComplete));
        }

        public static string GetSavedApiKey() => EditorPrefs.GetString(PrefsKey, "");

        public static void SaveApiKey(string key) => EditorPrefs.SetString(PrefsKey, NormalizeApiKey(key));

        static string NormalizeApiKey(string key) =>
            string.IsNullOrWhiteSpace(key) ? "" : key.Trim();

        public sealed class VerificationResult
        {
            public bool Success;
            public long HttpCode;
            public string Summary;
            public string BodyPreview;
            public string RequestUrlMasked;
            public string Transport;
            public bool LikelyEeaBlock;
        }

        static System.Collections.IEnumerator VerifyCoroutine(string apiKey, Action<VerificationResult> onComplete)
        {
            var result = new VerificationResult();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.Summary = "API key is empty. Paste your Google Cloud Map Tiles API key (no spaces).";
                onComplete?.Invoke(result);
                yield break;
            }

            string url = BuildRequestUrl(apiKey);
            result.RequestUrlMasked = MaskKeyInUrl(url);

            // UnityWebRequest first (same stack as Play mode).
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();

                result.HttpCode = req.responseCode;
                result.BodyPreview = Truncate(req.downloadHandler?.text, 800);
                result.Transport = $"UnityWebRequest ({req.result})";

                if (req.result == UnityWebRequest.Result.Success)
                {
                    FillSuccessResult(result);
                    onComplete?.Invoke(result);
                    yield break;
                }

                if (req.responseCode == 403)
                {
                    Fill403Result(result);
                    onComplete?.Invoke(result);
                    yield break;
                }

                if (req.responseCode is 400 or 401)
                {
                    result.Summary = BuildHttpErrorSummary(req.responseCode, result.BodyPreview, req.error);
                    onComplete?.Invoke(result);
                    yield break;
                }
            }

            // HTTP 0 / connection errors — retry with HttpClient (often works when UWR fails in Editor).
            VerificationResult fallback = TryHttpClient(url);
            fallback.RequestUrlMasked = result.RequestUrlMasked;
            if (fallback.HttpCode > 0 || !string.IsNullOrEmpty(fallback.Summary))
            {
                Debug.Log($"[Photogrammetry] API verify fallback: {fallback.Transport}\n{fallback.Summary}");
                onComplete?.Invoke(fallback);
                yield break;
            }

            result.Summary = BuildConnectionFailureSummary(result);
            onComplete?.Invoke(result);
        }

        static VerificationResult TryHttpClient(string url)
        {
            var result = new VerificationResult { Transport = "HttpClient" };
            try
            {
                using var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
                result.HttpCode = (long)response.StatusCode;
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                result.BodyPreview = Truncate(body, 800);

                if (response.IsSuccessStatusCode)
                {
                    FillSuccessResult(result);
                    return result;
                }

                if (result.HttpCode == 403)
                {
                    Fill403Result(result);
                    return result;
                }

                result.Summary = BuildHttpErrorSummary(result.HttpCode, result.BodyPreview, response.ReasonPhrase);
            }
            catch (Exception ex)
            {
                result.HttpCode = 0;
                result.Summary = "HttpClient also failed.\n\n" + ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null)
                    result.Summary += "\nInner: " + ex.InnerException.Message;
            }

            return result;
        }

        static void FillSuccessResult(VerificationResult result)
        {
            result.Success = !string.IsNullOrEmpty(result.BodyPreview) &&
                (result.BodyPreview.Contains("\"root\"") || result.BodyPreview.Contains("geometricError"));
            result.Summary = result.Success
                ? "SUCCESS — root.json returned valid tileset. Live Google streaming can proceed."
                : $"HTTP {result.HttpCode} but body does not look like a 3D Tiles root tileset.";
        }

        static void Fill403Result(VerificationResult result)
        {
            result.LikelyEeaBlock = ContainsIgnoreCase(result.BodyPreview, "EEA")
                || ContainsIgnoreCase(result.BodyPreview, "European");
            var sb = new StringBuilder();
            sb.AppendLine("HTTP 403 — Access denied.");
            if (result.LikelyEeaBlock)
            {
                sb.AppendLine("Likely EEA restriction (Croatia): Photorealistic 3D Tiles unavailable for this GCP project.");
                sb.AppendLine("Use LocalBaked tileset (dev/test only) or a non-Google fallback.");
            }
            else
            {
                sb.AppendLine("Check:");
                sb.AppendLine("• Map Tiles API enabled on this GCP project");
                sb.AppendLine("• Billing account linked and active");
                sb.AppendLine("• API key restrictions allow Map Tiles API");
                sb.AppendLine("• Key not restricted to wrong IP / HTTP referrer");
            }
            result.Summary = sb.ToString();
        }

        static string BuildHttpErrorSummary(long code, string body, string error)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP {code}" + (string.IsNullOrEmpty(error) ? "" : $" — {error}"));
            if (code == 400)
                sb.AppendLine("Bad request — check API key format (no extra spaces or line breaks).");
            if (code == 401)
                sb.AppendLine("Unauthorized — invalid API key or key not allowed for Map Tiles API.");
            if (!string.IsNullOrWhiteSpace(body))
                sb.AppendLine("Server message: " + Truncate(body, 300));
            return sb.ToString();
        }

        static string BuildConnectionFailureSummary(VerificationResult uwrResult)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Connection failed — HTTP 0 (no response from server).");
            sb.AppendLine();
            sb.AppendLine($"Transport: {uwrResult.Transport}");
            sb.AppendLine($"URL: {uwrResult.RequestUrlMasked}");
            sb.AppendLine();
            sb.AppendLine("Common causes:");
            sb.AppendLine("• No internet / firewall / VPN blocking tile.googleapis.com");
            sb.AppendLine("• Corporate proxy (Unity Editor may not use system proxy)");
            sb.AppendLine("• Antivirus SSL inspection");
            sb.AppendLine("• API key pasted with hidden spaces — re-paste and trim");
            sb.AppendLine();
            sb.AppendLine("Quick manual test — open in browser:");
            sb.AppendLine(uwrResult.RequestUrlMasked);
            sb.AppendLine();
            sb.AppendLine("If browser works but Unity fails, it is an Editor networking issue.");
            sb.AppendLine("If browser also fails, fix GCP key / billing / API enablement first.");
            return sb.ToString();
        }

        static string BuildRequestUrl(string apiKey) =>
            $"{PhotogrammetryApiSettings.DefaultRootUrl}?key={Uri.EscapeDataString(apiKey)}";

        static string MaskKeyInUrl(string url)
        {
            int idx = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return url;
            return url.Substring(0, idx + 4) + "***";
        }

        static bool ContainsIgnoreCase(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    /// <summary>Minimal editor coroutine helper without external deps.</summary>
    static class EditorCoroutineRunner
    {
        public static void Run(System.Collections.IEnumerator routine)
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                try
                {
                    if (!routine.MoveNext())
                        EditorApplication.update -= tick;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    EditorApplication.update -= tick;
                }
            };
            EditorApplication.update += tick;
        }
    }
}
