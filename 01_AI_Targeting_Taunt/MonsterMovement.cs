using UnityEngine;
using UnityEngine.AI;
using FishNet;
using FishNet.Object;
using NGPN.Combat;

/// <summary>
/// NavMeshAgent 제어/충돌 반응, 움직임 제어
/// </summary>
namespace NGPN.Gameplay
{
    public class MonsterMovement : NetworkBehaviour, IKnockbackReceiver, IDeathCleanable, IRespawnable
    {
        [Header("Move")] [SerializeField] protected float moveSpeed;
        [SerializeField] protected float turnSpeed = 120f; // 회전 보간 속도

        [Header("Animation")] [SerializeField] private Animator animator;
        [SerializeField] private string speedParam = "Speed";

        [Header("Facing")] [SerializeField] private float facingToleranceDegDefault = 7.5f; // 정면 간주 각도
        private bool _faceActive;
        private float _faceToleranceDeg;
        private Vector3 _faceTargetPos;

        // 내부 변수
        protected bool isLocked = false;
        protected bool isDead = false;
        protected NavMeshAgent navAgent;
        protected Rigidbody rb;
        public Rigidbody RB => rb;

        private Vector3 _lastPlanarVel; // 애니 판정용

        protected bool isCaptured = false; // 해당 몬스터가 해적에 의해 장전되어있는 상태인가?
        protected bool isLaunched = false; // 해당 몬스터가 해적에 의해 발사되었고 아직 착탄되기 전 상태인가?

        // --- 넉백 버퍼 ---
        protected Vector3 _extVel;
        protected float _extDamping = 12f;
        protected float _extHold;

        private SlimeAttack _slimeAttack;

        private void Awake()
        {
            navAgent = GetComponent<NavMeshAgent>();
            rb = GetComponent<Rigidbody>();

            _slimeAttack = GetComponent<SlimeAttack>();

            // Rigidbody 설정
            if (rb != null)
                rb.freezeRotation = true;
        }

        public override void OnStartNetwork()
        {
            // 서버에서만 NavMesh/물리 시뮬
            bool simulateHere = IsServerInitialized;

            if (navAgent != null)
            {
                navAgent.enabled = simulateHere; // 클라에선 완전히 비활성화
                if (simulateHere)
                {
                    navAgent.updatePosition = false;
                    navAgent.updateRotation = false;
                }
            }

            if (rb != null)
            {
                rb.isKinematic = !simulateHere;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.freezeRotation = true;
            }
        }

        public override void OnStartServer()
        {
            InstanceFinder.TimeManager.OnTick += ServerTick;

            // 게임오버면 즉시 정지
            DefenseGameManager gm = DefenseGameManager.Instance;
            if (gm != null && gm.IsGameOver)
                SetFrozen_Server(true);
        }

        public override void OnStopServer()
        {
            if (InstanceFinder.TimeManager != null)
                InstanceFinder.TimeManager.OnTick -= ServerTick;
        }

        [Server]
        public void Initialize(float moveSpd)
        {
            moveSpeed = moveSpd;
            if (navAgent != null) navAgent.speed = moveSpd;
        }

        [Server]
        public void MoveTo(Vector3 worldPos)
        {
            if (isLocked) return;
            if (navAgent == null || !navAgent.isOnNavMesh) return;

            navAgent.isStopped = false;
            navAgent.SetDestination(worldPos);
        }

        [Server]
        public void Stop()
        {
            if (navAgent == null) return;
            if (!navAgent.enabled || !navAgent.isOnNavMesh) return;

            navAgent.isStopped = true;
            navAgent.ResetPath();
            navAgent.velocity = Vector3.zero;
        }

        [Server]
        public void SetFrozen_Server(bool on)
        {
            if (on)
            {
                Lock();
                Stop();
            }
            else
            {
                Unlock();
            }

            SetFrozen_ObserversRpc(on);
        }

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SetFrozen_ObserversRpc(bool on)
        {
            // timeScale=0을 쓰지 않으므로, 로컬 애니메이션을 직접 멈춰준다.
            if (animator != null)
                animator.speed = on ? 0f : 1f;

            // 필요하면 클라 전용 후처리(예: 발광 VFX 끄기)도 여기에.
        }

        /// <summary>정면 보도록 강제</summary>
        [Server]
        public void RequestFaceTowards(Vector3 worldPos, float toleranceDeg = -1f)
        {
            _faceTargetPos = worldPos;
            _faceToleranceDeg = toleranceDeg > 0f ? toleranceDeg : facingToleranceDegDefault;
            _faceActive = true; // ServerMove()에서 부드럽게 회전 처리
        }

        /// <summary>정면을 보고 있나</summary>
        [Server]
        public bool IsFacingTowards(Vector3 worldPos, float toleranceDeg = -1f)
        {
            if (rb == null) return true;
            Vector3 dir = worldPos - rb.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return true;

            Quaternion target = Quaternion.LookRotation(dir);
            float tol = toleranceDeg > 0f ? toleranceDeg : facingToleranceDegDefault;
            float ang = Quaternion.Angle(rb.rotation, target);
            return ang <= tol;
        }

        public virtual void Lock()
        {
            isLocked = true;
        }

        public virtual void Unlock()
        {
            isLocked = false;
        }

        private void ServerTick()
        {
            float dt = (float)InstanceFinder.TimeManager.TickDelta;
            ServerMove(dt);
        }

        protected virtual void ServerMove(float dt)
        {
            if (!IsServerInitialized) return;

            // 해적에게 장전되었거나 발사로 인해 날아가는 중엔 물리 연산만 진행(움직임 X)
            if (isCaptured || isLaunched)
            {
                _extVel = Vector3.MoveTowards(_extVel, Vector3.zero, _extDamping * dt);
                _extHold = Mathf.Max(0f, _extHold - dt);
                return;
            }

            Vector3 planVel = Vector3.zero;

            if (!isDead)
            {
                // 명시적 '정면 맞추기' 요청 처리 (isLocked라도 회전은 허용)
                if (_faceActive)
                {
                    Vector3 dir = _faceTargetPos - rb.position;
                    dir.y = 0f;

                    if (dir.sqrMagnitude < 1e-6f)
                    {
                        _faceActive = false;
                    }
                    else
                    {
                        Quaternion targetRot = Quaternion.LookRotation(dir);
                        float ang = Quaternion.Angle(rb.rotation, targetRot);
                        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, turnSpeed * dt));
                        if (ang <= _faceToleranceDeg) _faceActive = false;
                    }
                }

                // 에이전트 기반 계획 속도
                if (navAgent && navAgent.isOnNavMesh)
                {
                    if (isLocked)
                    {
                        // 공격 중: 에이전트 추진만 멈춤(추격 금지)
                        if (!navAgent.isStopped) navAgent.isStopped = true;
                        planVel = Vector3.zero;
                    }
                    else
                    {
                        // 넉백이 끝났으면 자동 재개(공격 중엔 재개하지 않음)
                        if (navAgent.isStopped && _extHold <= 0f && _extVel.sqrMagnitude < 1e-4f)
                            navAgent.isStopped = false;

                        Vector3 desired = navAgent.desiredVelocity; // m/s
                        planVel = new Vector3(desired.x, 0f, desired.z);
                    }
                }

                if (planVel.sqrMagnitude > moveSpeed * moveSpeed)
                    planVel = planVel.normalized * moveSpeed;
            }

            // 넉백 합산 — NavMesh 추진 + 넉백
            Vector3 vel = planVel + _extVel;

            // 이동/회전
            rb.MovePosition(rb.position + vel * dt);

            if (!isDead)
            {
                if (!_faceActive && vel.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(new Vector3(vel.x, 0f, vel.z));
                    rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, turnSpeed * dt));
                }

                // 에이전트에 현재 위치 알려 경로 안정화
                if (navAgent) navAgent.nextPosition = rb.position;

                // 애니메이션(서버에서만 변경 → NetworkAnimator가 복제)
                SyncAnimatorSpeed(vel);
            }
            else
            {
                SyncAnimatorSpeed(Vector3.zero);
            }

            // 넉백 감쇠 + 보장시간 감소
            _extVel = Vector3.MoveTowards(_extVel, Vector3.zero, _extDamping * dt);
            _extHold = Mathf.Max(0f, _extHold - dt);
        }

        [Server]
        protected void SyncAnimatorSpeed(Vector3 planarVel)
        {
            _lastPlanarVel = Vector3.Lerp(_lastPlanarVel, planarVel, 0.25f);
            float speed = new Vector2(_lastPlanarVel.x, _lastPlanarVel.z).magnitude;
            if (animator) animator.SetFloat(speedParam, speed);
        }

        // 속도 적용
        [Server]
        public void ApplyVector3Velocity(Vector3 v0)
        {
            rb.isKinematic = false;
            rb.AddForce(v0, ForceMode.VelocityChange);
        }

        // 해적에 의한 장전당함 여부 적용
        [Server]
        public void ApplyCaptured(bool captured)
        {
            isCaptured = captured;
        }

        // 발사 여부 적용
        [Server]
        public void ApplyLaunched(bool launched)
        {
            isLaunched = launched;
        }

        [Server]
        public void ApplyExplosionKnockback(Vector3 impulseVel, float drag = 12, float hold = 0.2F,
            float ctrlScale = 0.2F)
        {
            _extVel += impulseVel;
            _extDamping = Mathf.Max(0f, drag);
            _extHold = Mathf.Max(_extHold, hold);

            Vector3 v = rb.linearVelocity;
            v.y = Mathf.Max(v.y, impulseVel.y);
            rb.linearVelocity = v;
        }

        // === IDeathCleanable ===
        public void CleanUpOnDeath_Server()
        {
            isDead = true;
            // SlimeAttack 컴포넌트가 있을 때만 특수 로직 실행
            if (_slimeAttack != null)
            {
                // 사망 시 NavMesh Agent를 즉시 비활성화하여 물리 간섭을 차단합니다.
                if (navAgent != null)
                {
                    if (navAgent.enabled && navAgent.isOnNavMesh)
                    {
                        navAgent.isStopped = true;
                        navAgent.ResetPath();
                    }
                    navAgent.enabled = false;
                }
                // Rigidbody의 Kinematic 상태를 해제하여 슬라임 데스 로직에 제어권을 넘깁니다.
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.freezeRotation = false;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }

        // === IRespawnable ===
        public void OnAfterRespawn_Server()
        {
            Unlock();
            isCaptured = false;
            isLaunched = false;
            isDead = false;

            if (_slimeAttack != null)
            {
                // 슬라임 전용 리스폰 복구 로직
                if (rb != null)
                {
                    // Rigidbody 설정을 Kinematic 상태로 복구
                    rb.isKinematic = true;
                    rb.freezeRotation = true;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.linearDamping = 0f;
                    rb.angularDamping = 0.05f;
                }
            }
        }
    }
}
