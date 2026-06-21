using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    public struct SpatialStreamingPerformanceStats
    {
        public float SmoothedFps;
        public float LastFrameMs;
        public float MinFps1s;
        public int LoadStartsThisFrame;
        public int LoadStartsPerSecond;
        public float SpawnMsThisFrame;
        public float LastSpawnMs;
        public string LastSpawnKey;
        public int SpikeCount;
        public string SpikeCsvPath;
    }

    /// <summary>Frame-rate tracking, spawn timing, and optional CSV spike logging.</summary>
    public sealed class SpatialStreamingPerformanceTracker
    {
        const float FpsSmoothing = 0.12f;
        const float StatsWindowSeconds = 1f;

        readonly StringBuilder _csvLine = new(256);
        bool _csvHeaderWritten;
        string _csvPath;
        int _spikeCount;
        int _loadStartsInWindow;
        int _loadStartsPerSecond;
        float _windowStartUnscaled;
        float _minFrameMsInWindow = float.MaxValue;

        float _smoothedFps = 60f;
        float _lastFrameMs;
        float _minFps1s = 60f;
        int _loadStartsThisFrame;
        float _spawnMsThisFrame;
        float _lastSpawnMs;
        string _lastSpawnKey = string.Empty;

        public SpatialStreamingPerformanceStats Stats => new()
        {
            SmoothedFps = _smoothedFps,
            LastFrameMs = _lastFrameMs,
            MinFps1s = _minFps1s,
            LoadStartsThisFrame = _loadStartsThisFrame,
            LoadStartsPerSecond = _loadStartsPerSecond,
            SpawnMsThisFrame = _spawnMsThisFrame,
            LastSpawnMs = _lastSpawnMs,
            LastSpawnKey = _lastSpawnKey ?? string.Empty,
            SpikeCount = _spikeCount,
            SpikeCsvPath = _csvPath ?? string.Empty,
        };

        public void BeginFrame()
        {
            _loadStartsThisFrame = 0;
            _spawnMsThisFrame = 0f;
        }

        public void RecordLoadStart()
        {
            _loadStartsThisFrame++;
            _loadStartsInWindow++;
        }

        public void RecordSpawn(string key, float milliseconds)
        {
            _lastSpawnKey = key ?? string.Empty;
            _lastSpawnMs = milliseconds;
            _spawnMsThisFrame += milliseconds;
        }

        public void EndFrame(
            float unscaledDeltaTime,
            SpatialStreamingStats streamingStats,
            bool logSpikesToCsv,
            bool logSpikesToConsole,
            float spikeFrameMsThreshold)
        {
            if (unscaledDeltaTime > 1e-6f)
            {
                float instantFps = 1f / unscaledDeltaTime;
                _smoothedFps = Mathf.Lerp(_smoothedFps, instantFps, FpsSmoothing);
                _lastFrameMs = unscaledDeltaTime * 1000f;
            }

            float now = Time.unscaledTime;
            if (_windowStartUnscaled <= 0f)
                _windowStartUnscaled = now;

            _minFrameMsInWindow = Mathf.Min(_minFrameMsInWindow, _lastFrameMs);

            if (now - _windowStartUnscaled >= StatsWindowSeconds)
            {
                _minFps1s = _minFrameMsInWindow > 1e-3f ? 1000f / _minFrameMsInWindow : _smoothedFps;
                _loadStartsPerSecond = _loadStartsInWindow;
                _loadStartsInWindow = 0;
                _minFrameMsInWindow = float.MaxValue;
                _windowStartUnscaled = now;
            }

            bool streamingActive = streamingStats.Pending > 0 ||
                                   _loadStartsThisFrame > 0 ||
                                   _spawnMsThisFrame > 0.01f;

            if (_lastFrameMs < spikeFrameMsThreshold || !streamingActive)
                return;

            _spikeCount++;

            if (logSpikesToConsole)
            {
                Debug.LogWarning(
                    $"[ZGConnect.Spatial] Frame spike {_lastFrameMs:F1} ms " +
                    $"(fps≈{_smoothedFps:F0}) pending={streamingStats.Pending} loading={streamingStats.Loading} " +
                    $"spawnMs={_spawnMsThisFrame:F1} mainThreadMs last='{_lastSpawnKey}'");
            }

            if (!logSpikesToCsv)
                return;

            EnsureCsvPath();
            AppendSpikeCsv(streamingStats);
        }

        void EnsureCsvPath()
        {
            if (!string.IsNullOrEmpty(_csvPath))
                return;

            string folder = Path.Combine(Application.persistentDataPath, "ZGConnect");
            Directory.CreateDirectory(folder);
            _csvPath = Path.Combine(folder, "spatial_streaming_spikes.csv");
        }

        void AppendSpikeCsv(SpatialStreamingStats streamingStats)
        {
            try
            {
                bool writeHeader = !_csvHeaderWritten && !File.Exists(_csvPath);
                using var writer = new StreamWriter(_csvPath, append: true, encoding: Encoding.UTF8);
                if (writeHeader)
                {
                    writer.WriteLine(
                        "utc,frameMs,smoothedFps,minFps1s,pending,loading,loadedTotal," +
                        "loadStartsFrame,spawnMsFrame,lastSpawnKey,visibleBlocks,meshRenderers");
                    _csvHeaderWritten = true;
                }

                _csvLine.Clear();
                IFormatProvider ic = CultureInfo.InvariantCulture;
                _csvLine.Append(System.DateTime.UtcNow.ToString("O", ic)).Append(',');
                _csvLine.Append(_lastFrameMs.ToString("F2", ic)).Append(',');
                _csvLine.Append(_smoothedFps.ToString("F1", ic)).Append(',');
                _csvLine.Append(_minFps1s.ToString("F1", ic)).Append(',');
                _csvLine.Append(streamingStats.Pending).Append(',');
                _csvLine.Append(streamingStats.Loading).Append(',');
                _csvLine.Append(streamingStats.LoadedTotal).Append(',');
                _csvLine.Append(_loadStartsThisFrame).Append(',');
                _csvLine.Append(_spawnMsThisFrame.ToString("F2", ic)).Append(',');
                _csvLine.Append(EscapeCsv(_lastSpawnKey)).Append(',');
                _csvLine.Append(streamingStats.VisibleBlocks).Append(',');
                _csvLine.Append(streamingStats.TotalMeshRenderers);
                writer.WriteLine(_csvLine.ToString());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ZGConnect.Spatial] Spike CSV write failed: {ex.Message}");
            }
        }

        static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
