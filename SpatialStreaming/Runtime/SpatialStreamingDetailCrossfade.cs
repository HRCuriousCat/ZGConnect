using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZGConnect.SpatialStreaming
{
    /// <summary>
    /// Complementary screen-space dither crossfade between a subcell proxy and its detail root.
    /// Added automatically by <see cref="SpatialStreamingController"/> — do not add manually unless needed.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ZG Connect/Spatial Streaming/Detail Crossfade")]
    public sealed class SpatialStreamingDetailCrossfade : MonoBehaviour
    {
        [SerializeField] float _durationSeconds = 0.12f;
        [SerializeField] int _maxConcurrentTransitions = 16;

        public float DurationSeconds
        {
            get => _durationSeconds;
            set => _durationSeconds = Mathf.Max(0.01f, value);
        }

        public int MaxConcurrentTransitions
        {
            get => _maxConcurrentTransitions;
            set => _maxConcurrentTransitions = Mathf.Max(1, value);
        }

        public void Configure(float durationSeconds, int maxConcurrentTransitions)
        {
            DurationSeconds = durationSeconds;
            MaxConcurrentTransitions = maxConcurrentTransitions;
        }

        struct Transition
        {
            public string ProxyKey;
            public GameObject ProxyRoot;
            public GameObject DetailRoot;
            public float StartTime;
            public Action OnComplete;
        }

        readonly List<Transition> _active = new(8);
        readonly HashSet<string> _transitioningProxyKeys = new(StringComparer.Ordinal);

        public bool IsTransitioningProxy(string proxyKey) =>
            !string.IsNullOrEmpty(proxyKey) && _transitioningProxyKeys.Contains(proxyKey);

        public bool HasActiveTransitions => _active.Count > 0;

        public bool TryBegin(
            string proxyKey,
            GameObject proxyRoot,
            GameObject detailRoot,
            Action onComplete)
        {
            if (string.IsNullOrEmpty(proxyKey) ||
                proxyRoot == null ||
                detailRoot == null)
            {
                return false;
            }

            if (_transitioningProxyKeys.Contains(proxyKey))
                return false;

            if (_active.Count >= Mathf.Max(1, _maxConcurrentTransitions))
                return false;

            proxyRoot.SetActive(true);
            detailRoot.SetActive(true);
            SpatialStreamingDitherMaterialUtility.ApplyDitherFadeOut(proxyRoot, 1f);
            SpatialStreamingDitherMaterialUtility.ApplyDitherFadeIn(detailRoot, 0f);

            _transitioningProxyKeys.Add(proxyKey);
            _active.Add(new Transition
            {
                ProxyKey = proxyKey,
                ProxyRoot = proxyRoot,
                DetailRoot = detailRoot,
                StartTime = Time.unscaledTime,
                OnComplete = onComplete,
            });

            return true;
        }

        void LateUpdate()
        {
            if (_active.Count == 0)
                return;

            float now = Time.unscaledTime;
            float duration = Mathf.Max(0.01f, _durationSeconds);

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Transition transition = _active[i];
                float t = Mathf.Clamp01((now - transition.StartTime) / duration);

                if (transition.ProxyRoot != null)
                    SpatialStreamingDitherMaterialUtility.ApplyDitherFadeOut(transition.ProxyRoot, 1f - t);

                if (transition.DetailRoot != null)
                    SpatialStreamingDitherMaterialUtility.ApplyDitherFadeIn(transition.DetailRoot, t);

                if (t < 1f)
                    continue;

                if (transition.DetailRoot != null)
                    SpatialStreamingDitherMaterialUtility.ClearDitherFade(transition.DetailRoot);

                _transitioningProxyKeys.Remove(transition.ProxyKey);
                transition.OnComplete?.Invoke();
                _active.RemoveAt(i);
            }
        }
    }
}
