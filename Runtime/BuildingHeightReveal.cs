using UnityEngine;

namespace ZGConnect
{
    /// <summary>
    /// Lerps localScale.z from 0 to the captured target while a building is revealed.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingHeightReveal : MonoBehaviour
    {
        private Vector3 _targetLocalScale;
        private bool    _hasTarget;
        private float   _t;
        private float   _duration;
        private bool    _animating;

        public void CaptureTargetScale()
        {
            Vector3 s = transform.localScale;
            if (!_hasTarget || s.z > 0.001f)
            {
                _targetLocalScale = s;
                if (_targetLocalScale.z < 0.001f)
                    _targetLocalScale.z = 1f;
                _hasTarget = true;
            }
        }

        public void PrepareHidden()
        {
            CaptureTargetScale();
            ApplyHeight(0f);
        }

        public void BeginReveal(float durationSeconds)
        {
            CaptureTargetScale();
            _duration  = Mathf.Max(0.01f, durationSeconds);
            _t         = 0f;
            _animating = true;
            gameObject.SetActive(true);
            ApplyHeight(0f);
        }

        public void SnapFullHeight()
        {
            CaptureTargetScale();
            _animating = false;
            ApplyHeight(1f);
        }

        private void Update()
        {
            if (!_animating) return;

            _t += Time.deltaTime / _duration;
            if (_t >= 1f)
            {
                _t         = 1f;
                _animating = false;
            }

            ApplyHeight(_t);
        }

        private void ApplyHeight(float linearT)
        {
            float z = _targetLocalScale.z * EaseInOut(Mathf.Clamp01(linearT));
            transform.localScale = new Vector3(_targetLocalScale.x, _targetLocalScale.y, z);
        }

        /// <summary>Smoothstep ease-in-out: slow start and end, faster in the middle.</summary>
        private static float EaseInOut(float t) => t * t * (3f - 2f * t);
    }
}
