using Game.Client.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Client.CameraRig
{
    /// <summary>
    /// 3rd-person over-the-shoulder orbit camera (spec vol.5 12.1). Click to lock the mouse, Esc to release.
    /// Quarter view is added before stage 2 as a second rig.
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        public Transform Target;
        public float Distance = 6f;
        public float MinDistance = 2.5f;
        public float MaxDistance = 12f;
        public float Sensitivity = 0.12f;
        public float PivotHeight = 1.6f;
        public float ShoulderOffset = 0.6f;
        public LayerMask CollisionMask = ~0;
        /// <summary>True while a UI window needs the mouse: clicking must not grab it.</summary>
        public System.Func<bool> LockBlocked;
        /// <summary>Look at the character from the front (the look picker), no mouse control.</summary>
        public bool FrontPreview;
        /// <summary>Archer aiming (right mouse held): the view zooms in a little.</summary>
        public bool ZoomAim;

        // Quarter view (spec vol.5 12.1): fixed diagonal angle, cursor free, aim with the cursor (GameInput.AimYaw).
        const float QuarterPitch = 55f, QuarterYaw = 45f, QuarterMin = 8f, QuarterMax = 28f;
        float _quarterDist = 16f;
        bool _wasQuarter;
        Camera _camComp;
        float _baseFov = 60f;

        float _yaw;
        float _pitch = 15f;
        float _dist;

        public bool MouseLocked => Cursor.lockState == CursorLockMode.Locked;

        void Start()
        {
            _dist = Distance;
            _camComp = GetComponent<Camera>();
            if (_camComp) _baseFov = _camComp.fieldOfView;
        }

        public void SnapBehind()
        {
            if (Target) _yaw = Target.eulerAngles.y;
        }

        void LateUpdate()
        {
            if (!Target) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            bool quarter = GameInput.Quarter;
            if (quarter != _wasQuarter)
            {
                _wasQuarter = quarter;
                if (quarter) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            }
            if (_camComp)
            {
                float fov = ZoomAim && !quarter ? _baseFov * 0.62f : _baseFov;
                _camComp.fieldOfView = Mathf.Lerp(_camComp.fieldOfView, fov, 1f - Mathf.Exp(-12f * Time.deltaTime));
            }

            if (!quarter && mouse != null && mouse.leftButton.wasPressedThisFrame && !MouseLocked && (LockBlocked == null || !LockBlocked()))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (FrontPreview)
            {
                var fr = Quaternion.Euler(6f, Target.eulerAngles.y + 180f, 0);
                var fp = Target.position + Vector3.up * 1.1f;
                transform.SetPositionAndRotation(fp + fr * Vector3.back * 3.6f, fr);
                return;
            }

            if (quarter)
            {
                if (mouse != null)
                {
                    float sc = mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(sc) > 0.01f && !(UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()))
                        _quarterDist = Mathf.Clamp(_quarterDist - Mathf.Sign(sc) * 1.5f, QuarterMin, QuarterMax);
                }
                var qrot = Quaternion.Euler(QuarterPitch, QuarterYaw, 0);
                var qpivot = Target.position + Vector3.up * 1.0f;
                transform.SetPositionAndRotation(qpivot - qrot * Vector3.forward * _quarterDist, qrot);
                return;
            }

            if (mouse != null && MouseLocked)
            {
                Vector2 d = mouse.delta.ReadValue();
                _yaw += d.x * Sensitivity;
                _pitch = Mathf.Clamp(_pitch - d.y * Sensitivity, -35f, 70f);
            }
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance - Mathf.Sign(scroll) * 0.8f, MinDistance, MaxDistance);
            }

            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            Vector3 pivot = Target.position + Vector3.up * PivotHeight + rot * Vector3.right * ShoulderOffset;
            Vector3 dir = rot * Vector3.back;

            // Pull in when something is between the camera and the player.
            float want = Distance;
            if (Physics.SphereCast(pivot, 0.25f, dir, out var hit, want, CollisionMask, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(Target))
                want = Mathf.Max(0.5f, hit.distance - 0.1f);
            _dist = want < _dist ? want : Mathf.Lerp(_dist, want, 1f - Mathf.Exp(-6f * Time.deltaTime));

            transform.SetPositionAndRotation(pivot + dir * _dist, rot);
        }
    }
}
