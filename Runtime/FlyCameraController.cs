using UnityEngine;
using UnityEngine.InputSystem;

namespace ZGConnect
{
    /// <summary>
    /// Scene-view-style fly camera for runtime use.
    /// Requires the Unity Input System package (already present in this project).
    ///
    /// Controls:
    ///   Hold RMB          — enter fly mode / mouse look
    ///   W / A / S / D     — move forward / left / back / right
    ///   Q / E             — move down / up (world space)
    ///   Left Shift        — sprint (speed multiplier)
    ///   Scroll wheel      — adjust base move speed
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class FlyCameraController : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("Degrees per pixel of mouse movement.")]
        [SerializeField] private float lookSensitivity = 0.15f;

        [Header("Movement")]
        [SerializeField] private float moveSpeed        = 100f;
        [SerializeField] private float sprintMultiplier = 3f;

        [Header("Speed Scroll")]
        [Tooltip("Fraction of current speed added or removed per scroll tick. " +
                 "Multiplicative — feels logarithmic, same as Unity Scene View.")]
        [SerializeField] private float scrollStep = 0.15f;
        [SerializeField] private float minSpeed   = 1f;
        [SerializeField] private float maxSpeed   = 5000f;

        [Header("HUD")]
        [SerializeField] private bool showHUD = true;

        // ── Private state ──────────────────────────────────────────────────────
        private float _yaw;
        private float _pitch;
        private bool  _flying;

        private GUIStyle _hudStyle;
        private GUIStyle _hudShadowStyle;

        // ──────────────────────────────────────────────────────────────────────

        private void Start()
        {
            SyncOrientationFromTransform();
        }

        /// <summary>Call after externally moving/rotating the camera (e.g. dataset auto-focus).</summary>
        public void SyncOrientationFromTransform()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw   = euler.y;
            _pitch = euler.x;
            if (_pitch > 180f) _pitch -= 360f;
        }

        private void Update()
        {
            HandleFlyMode();
            HandleScrollSpeed();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) ReleaseCursor();
        }

        // ── Fly mode ───────────────────────────────────────────────────────────

        private void HandleFlyMode()
        {
            if (Mouse.current == null) return;

            _flying = Mouse.current.rightButton.isPressed;

            if (!_flying)
            {
                ReleaseCursor();
                return;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            HandleLook();
            HandleMovement();
        }

        private void HandleLook()
        {
            // Mouse.current.delta gives raw pixel delta per frame in the new Input System.
            // lookSensitivity is expressed as degrees-per-pixel.
            Vector2 delta = Mouse.current.delta.ReadValue();

            _yaw   += delta.x * lookSensitivity;
            _pitch -= delta.y * lookSensitivity;
            _pitch  = Mathf.Clamp(_pitch, -89f, 89f);

            transform.eulerAngles = new Vector3(_pitch, _yaw, 0f);
        }

        private void HandleMovement()
        {
            if (Keyboard.current == null) return;

            bool sprinting = Keyboard.current.leftShiftKey.isPressed ||
                             Keyboard.current.rightShiftKey.isPressed;
            float speed = moveSpeed * (sprinting ? sprintMultiplier : 1f);

            Vector3 move = Vector3.zero;

            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                move += transform.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                move -= transform.forward;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                move += transform.right;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                move -= transform.right;
            if (Keyboard.current.eKey.isPressed)
                move += Vector3.up;
            if (Keyboard.current.qKey.isPressed)
                move -= Vector3.up;

            if (move.sqrMagnitude > 1f)
                move.Normalize();

            transform.position += move * speed * Time.deltaTime;
        }

        // ── Speed scroll ───────────────────────────────────────────────────────

        private void HandleScrollSpeed()
        {
            if (Mouse.current == null) return;

            // scroll.y in the new Input System returns large values (e.g. 120 per tick on Windows).
            // We only care about the direction, not the magnitude.
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (scroll == 0f) return;

            moveSpeed *= 1f + Mathf.Sign(scroll) * scrollStep;
            moveSpeed  = Mathf.Clamp(moveSpeed, minSpeed, maxSpeed);
        }

        // ── Cursor ─────────────────────────────────────────────────────────────

        private static void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        // ── HUD ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!showHUD) return;

            EnsureHUDStyles();

            string text = _flying
                ? $"Speed  {moveSpeed:F0} m/s  |  Shift: sprint  ·  Q/E: up/down  ·  Scroll: adjust speed"
                : $"Speed  {moveSpeed:F0} m/s  |  Hold RMB to fly  ·  Scroll to adjust speed";

            float w = Screen.width - 20f;
            float h = 22f;
            float y = Screen.height - h - 10f;
            var   r = new Rect(10f, y, w, h);

            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _hudShadowStyle);
            GUI.Label(r, text, _hudStyle);
        }

        private void EnsureHUDStyles()
        {
            if (_hudStyle != null) return;

            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };

            _hudShadowStyle = new GUIStyle(_hudStyle)
            {
                normal = { textColor = new Color(0f, 0f, 0f, 0.8f) }
            };
        }
    }
}
