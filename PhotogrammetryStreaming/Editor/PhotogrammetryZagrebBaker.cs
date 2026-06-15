using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ZGConnect.PhotogrammetryStreaming.Editor
{
    public class PhotogrammetryZagrebBaker : EditorWindow
    {
        PhotogrammetryApiSettings _apiSettings;
        PhotogrammetryRegionPreset _regionPreset;
        PhotogrammetryLodProfile _lodProfile;
        string _outputRelative = "ZGConnect/photogrammetry";
        long _maxBytes = 8L * 1024L * 1024L * 1024L;
        bool _baking;
        string _log = "";

        [MenuItem("ZG Connect/Photogrammetry/Zagreb Offline Baker (Dev/Test)")]
        public static void Open()
        {
            GetWindow<PhotogrammetryZagrebBaker>("Photogrammetry Baker");
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "LEGAL: Baked Google tiles are for INTERNAL DEV/TEST ONLY. " +
                "Google Map Tiles API Terms restrict redistribution and permanent re-serving. " +
                "Do not ship baked tiles to end users without legal review.",
                MessageType.Warning);

            _apiSettings = (PhotogrammetryApiSettings)EditorGUILayout.ObjectField("API Settings", _apiSettings, typeof(PhotogrammetryApiSettings), false);
            _regionPreset = (PhotogrammetryRegionPreset)EditorGUILayout.ObjectField("Region", _regionPreset, typeof(PhotogrammetryRegionPreset), false);
            _lodProfile = (PhotogrammetryLodProfile)EditorGUILayout.ObjectField("LOD Profile", _lodProfile, typeof(PhotogrammetryLodProfile), false);
            _outputRelative = EditorGUILayout.TextField("Output (under StreamingAssets)", _outputRelative);
            _maxBytes = EditorGUILayout.LongField("Max bake bytes", _maxBytes);

            EditorGUI.BeginDisabledGroup(_baking);
            if (GUILayout.Button("Bake Zagreb bbox"))
            {
                _log = "Baking...";
                EditorCoroutineRunner.Run(BakeCoroutine());
            }
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_log))
                EditorGUILayout.TextArea(_log, GUILayout.MinHeight(160));
        }

        System.Collections.IEnumerator BakeCoroutine()
        {
            _baking = true;
            var sb = new StringBuilder();

            if (_apiSettings == null || string.IsNullOrWhiteSpace(_apiSettings.apiKey))
            {
                sb.AppendLine("API settings or key missing.");
                _log = sb.ToString();
                _baking = false;
                yield break;
            }

            if (_regionPreset == null)
                _regionPreset = AssetDatabase.LoadAssetAtPath<PhotogrammetryRegionPreset>(
                    "Assets/ZGConnect/PhotogrammetryStreaming/Settings/PhotogrammetryRegion_Zagreb.asset");

            if (_lodProfile == null)
                _lodProfile = AssetDatabase.LoadAssetAtPath<PhotogrammetryLodProfile>(
                    "Assets/ZGConnect/PhotogrammetryStreaming/Settings/PhotogrammetryLodProfile_Default.asset");

            var session = new Google3DTilesSession();
            string err = null;
            yield return session.StartSessionCoroutine(_apiSettings, e => err = e);
            if (!string.IsNullOrEmpty(err))
            {
                sb.AppendLine(err);
                _log = sb.ToString();
                _baking = false;
                yield break;
            }

            string rootUrl = _apiSettings.BuildRootRequestUrl();
            byte[] rootBytes = null;
            yield return Download(session.AppendSessionAndKey(rootUrl), b => rootBytes = b);
            if (rootBytes == null)
            {
                sb.AppendLine("Failed to download root.json");
                _log = sb.ToString();
                _baking = false;
                yield break;
            }

            string rootJson = Encoding.UTF8.GetString(rootBytes);
            var rootNode = Tile3DParser.ParseTileset(rootJson, rootUrl);
            var clipper = new ZagrebGeographicClipper(_regionPreset, true);

            string outDir = Path.Combine(Application.streamingAssetsPath, _outputRelative.Replace('/', Path.DirectorySeparatorChar));
            string tilesDir = Path.Combine(outDir, "tiles");
            Directory.CreateDirectory(tilesDir);

            long totalBytes = 0;
            int tileCount = 0;
            var urlToLocal = new Dictionary<string, string>();

            var stack = new Stack<Tile3DNode>();
            stack.Push(rootNode);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!clipper.IntersectsRegion(node))
                    continue;

                if (node.Depth > (_lodProfile?.maxRefinementDepth ?? 20))
                    continue;

                if (!string.IsNullOrEmpty(node.ContentUri))
                {
                    string fetchUrl = session.AppendSessionAndKey(node.ContentUri);
                    if (!urlToLocal.ContainsKey(fetchUrl))
                    {
                        byte[] data = null;
                        yield return Download(fetchUrl, b => data = b);
                        if (data == null)
                            continue;

                        totalBytes += data.Length;
                        if (totalBytes > _maxBytes)
                        {
                            sb.AppendLine($"Byte cap reached ({_maxBytes} bytes). Stopping.");
                            break;
                        }

                        string ext = node.ContentUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ".json" :
                            node.ContentUri.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? ".glb" : ".bin";
                        string fileName = $"tile_{tileCount:D5}{ext}";
                        string localPath = Path.Combine(tilesDir, fileName);
                        File.WriteAllBytes(localPath, data);
                        urlToLocal[fetchUrl] = $"tiles/{fileName}";
                        tileCount++;
                    }
                }

                for (int i = node.Children.Count - 1; i >= 0; i--)
                    stack.Push(node.Children[i]);

                if (tileCount % 10 == 0)
                {
                    _log = $"Downloaded {tileCount} files, {totalBytes / (1024 * 1024)} MB...";
                    Repaint();
                }
            }

            string bakedRoot = RewriteTilesetJson(rootJson, urlToLocal);
            string tilesetPath = Path.Combine(outDir, "tileset.json");
            File.WriteAllText(tilesetPath, bakedRoot, Encoding.UTF8);

            var manifest = new BakeManifest
            {
                bakeDateUtc = DateTime.UtcNow.ToString("o"),
                tileCount = tileCount,
                totalBytes = totalBytes,
                region = _regionPreset != null ? _regionPreset.kind.ToString() : "unknown",
                legalNotice = "Internal dev/test only. Google Map Tiles API Terms apply.",
            };
            File.WriteAllText(Path.Combine(outDir, "manifest.json"),
                JsonUtility.ToJson(manifest, true), Encoding.UTF8);

            if (_apiSettings != null)
            {
                Undo.RecordObject(_apiSettings, "Set local tileset");
                _apiSettings.localTilesetPath = $"{_outputRelative}/tileset.json";
                _apiSettings.tileSourceMode = TileSourceMode.LocalBaked;
                EditorUtility.SetDirty(_apiSettings);
            }

            AssetDatabase.Refresh();
            sb.AppendLine($"Bake complete: {tileCount} files, {totalBytes / (1024.0 * 1024.0):F1} MB");
            sb.AppendLine($"Output: {tilesetPath}");
            _log = sb.ToString();
            _baking = false;
        }

        static string RewriteTilesetJson(string json, Dictionary<string, string> urlToLocal)
        {
            string result = json;
            foreach (var kv in urlToLocal)
            {
                string uriOnly = kv.Key;
                int q = uriOnly.IndexOf('?');
                if (q >= 0)
                    uriOnly = uriOnly.Substring(0, q);
                result = result.Replace(uriOnly, kv.Value);
                int slash = kv.Key.LastIndexOf('/');
                if (slash >= 0)
                {
                    string shortPath = kv.Key.Substring(slash);
                    int qq = shortPath.IndexOf('?');
                    if (qq >= 0)
                        shortPath = shortPath.Substring(0, qq);
                    result = result.Replace(shortPath, kv.Value);
                }
            }
            return result;
        }

        static System.Collections.IEnumerator Download(string url, Action<byte[]> onDone)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 120;
            yield return req.SendWebRequest();
            onDone?.Invoke(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.data : null);
        }

        [Serializable]
        class BakeManifest
        {
            public string bakeDateUtc;
            public int tileCount;
            public long totalBytes;
            public string region;
            public string legalNotice;
        }
    }
}
