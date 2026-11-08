using Game.Client.Net;
using Game.Shared.Protocol;
using UnityEngine;

namespace Game.Client.Player
{
    /// <summary>Sends the predicted state to the server at clientSendRate and applies server corrections.</summary>
    public sealed class LocalPlayerNet : MonoBehaviour
    {
        NetClient _net;
        PlayerMotor _motor;
        float _interval = 0.05f;
        float _timer;
        uint _seq;
        byte _epoch;

        public int Corrections { get; private set; }
        public string LastCorrection { get; private set; } = "-";
        public event System.Action<CorrectionReason> Corrected;

        public void Init(NetClient net, PlayerMotor motor, int sendRate)
        {
            _net = net;
            _motor = motor;
            _interval = 1f / Mathf.Max(1, sendRate);
            _net.OnCorrection += HandleCorrection;
        }

        void OnDestroy()
        {
            if (_net != null) _net.OnCorrection -= HandleCorrection;
        }

        void HandleCorrection(PositionCorrection c)
        {
            _epoch = c.Epoch;
            _motor.Teleport(new Vector3(c.X, c.Y, c.Z));
            Corrected?.Invoke(c.Reason);
            if (c.Reason is CorrectionReason.Respawn or CorrectionReason.Knockback or CorrectionReason.CcLocked or CorrectionReason.Teleport) return; // not violations
            Corrections++;
            LastCorrection = c.Reason.ToString();
            Debug.LogWarning($"[net] server correction: {c.Reason}");
        }

        void LateUpdate()
        {
            if (_net == null || !_net.InGame) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < _interval) return;
            _timer %= _interval;

            var p = transform.position;
            _net.Send(new MoveState
            {
                Seq = ++_seq,
                CorrectionEpoch = _epoch,
                X = p.x, Y = p.y, Z = p.z,
                Yaw = _motor.Yaw,
                Flags = _motor.CurrentFlags(),
                JumpCount = (byte)_motor.JumpCount,
            });
            _motor.PendingDashStart = false;
            _motor.PendingJumpStart = false;
            _motor.PendingDodgeStart = false;
        }
    }
}
