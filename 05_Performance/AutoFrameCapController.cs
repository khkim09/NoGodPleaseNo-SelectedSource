using UnityEngine;

namespace NGPN.Gameplay
{
    /// <summary>
    /// 자동 FPS Cap 컨트롤러.
    /// - FrameTimingManager로 CPU/GPU 프레임 타임을 보고,
    ///   현재 캡을 "지속적으로 못 지키면" 한 단계 내림,
    ///   충분히 여유가 오래 유지되면 한 단계 올림.
    /// - 호스트일 때는 더 보수적으로 시작/상향 조건을 강화하는 용도.
    /// </summary>
    public class AutoFrameCapController : MonoBehaviour
    {
        // 단계식 프레임 상한 후보
        private static readonly int[] Caps = { 240, 165, 144, 120, 90, 60 };

        [Header("Auto FrameCap")]
        [SerializeField] private bool autoEnabled = true;

        [Tooltip("Auto 모드에서 절대 넘어가지 않는 최대 캡")]
        [SerializeField] private int maxCap = 120;

        [Tooltip("호스트일 때 시작 캡(권장 60 또는 90)")]
        [SerializeField] private int hostStartCap = 60;

        [Tooltip("클라일 때 시작 캡(권장 120)")]
        [SerializeField] private int clientStartCap = 120;

        [Tooltip("샘플링 주기(초)")]
        [SerializeField] private float sampleInterval = 1.0f;

        [Tooltip("내리는 판정: 목표 프레임타임 대비 몇 % 초과면 내릴지")]
        [SerializeField] private float downThresholdRatio = 1.10f; // 110%

        [Tooltip("올리는 판정: 다음 단계 목표 프레임타임 대비 몇 % 이하로 유지되면 올릴지")]
        [SerializeField] private float upThresholdRatio = 0.80f; // 80%

        [Tooltip("올리기 위해 여유가 유지되어야 하는 시간(초)")]
        [SerializeField] private float upHoldSeconds = 6.0f;

        private int _capIndex = -1;
        private float _timer;
        private float _goodTime;

        private FrameTiming[] _timings = new FrameTiming[1];

        public bool AutoEnabled => autoEnabled;

        private int _desiredFrameRate = int.MinValue;
        private bool _hasDesiredFrameRate;

        private bool _lastIsHost;
        private bool _lastAutoEnabled;

        public void Configure(bool enabled, int maxCapFps, bool isHost)
        {
            autoEnabled = enabled;
            maxCap = Mathf.Max(60, maxCapFps);

            bool hostJustBecame = isHost && !_lastIsHost;
            bool autoJustEnabled = autoEnabled && !_lastAutoEnabled;

            // 호스트는 auto 진입/호스트 전환 시 "무조건 60부터 시작"
            int start = isHost ? hostStartCap : clientStartCap;

            int currentOrStart;
            if (autoEnabled && (hostJustBecame || autoJustEnabled) && isHost)
                currentOrStart = hostStartCap;
            else if (_capIndex >= 0)
                currentOrStart = Caps[_capIndex];
            else
                currentOrStart = start;

            int allowedMax = ClampToAllowed(maxCap);
            currentOrStart = Mathf.Min(currentOrStart, allowedMax);

            ApplyHostTuning(isHost);
            SetCapImmediate(ClampToAllowed(currentOrStart));

            _lastIsHost = isHost;
            _lastAutoEnabled = autoEnabled;
        }

        private void ApplyHostTuning(bool isHost)
        {
            if (isHost)
            {
                downThresholdRatio = 1.05f; // 하향은 빠르게
                upThresholdRatio = 0.70f;   // 상향은 매우 보수적으로
                upHoldSeconds = 12.0f;      // 오래 안정 유지해야 상향
            }
            else
            {
                downThresholdRatio = 1.10f;
                upThresholdRatio = 0.80f;
                upHoldSeconds = 6.0f;
            }
        }

        public int GetCurrentCap()
        {
            if (_capIndex < 0) return -1;
            return Caps[_capIndex];
        }

        /// <summary>수동 설정(자동 꺼짐)</summary>
        public void SetManualCap(int fps)
        {
            autoEnabled = false;
            SetCapImmediate(fps <= 0 ? -1 : fps);
        }

        private void LateUpdate()
        {
            // 우리가 원하는 목표 프레임레이트가 있으면 항상 마지막에 재주입
            if (_hasDesiredFrameRate)
            {
                // VSync 켜져있으면 targetFrameRate 무시될 수 있어서 항상 끔
                if (QualitySettings.vSyncCount != 0)
                    QualitySettings.vSyncCount = 0;

                int desired = _desiredFrameRate;

                // AutoEnabled 상태라면 maxCap 상한 적용
                if (autoEnabled && desired > 0)
                {
                    int allowedMaxAuto = ClampToAllowed(maxCap);
                    if (desired > allowedMaxAuto)
                        desired = allowedMaxAuto;
                }

                if (Application.targetFrameRate != desired)
                    Application.targetFrameRate = desired;
            }

            if (!autoEnabled) return;

            _timer += Time.unscaledDeltaTime;
            if (_timer < sampleInterval) return;
            _timer = 0f;

            // 최신 프레임 타이밍 캡처
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings(1, _timings);
            if (count == 0) return;

            double cpuMs = _timings[0].cpuFrameTime;
            double gpuMs = _timings[0].gpuFrameTime;
            double frameMs = System.Math.Max(cpuMs, gpuMs);

            int curCap = GetCurrentCap();
            if (curCap <= 0) return;

            // auto 최대 캡 제한
            int allowedMax = ClampToAllowed(maxCap);
            if (curCap > allowedMax)
                SetCapImmediate(allowedMax);

            curCap = GetCurrentCap();
            double curTargetMs = 1000.0 / curCap;

            // 1) 현재 캡을 지속적으로 못 지키면 내림
            if (frameMs > curTargetMs * downThresholdRatio)
            {
                _goodTime = 0f;
                StepDown();
                return;
            }

            // 2) 다음 단계(더 높은 캡)를 올려도 될 만큼 여유가 있는지 체크
            int nextHigher = GetNextHigherCapWithinMax();
            if (nextHigher > curCap)
            {
                double nextTargetMs = 1000.0 / nextHigher;

                // next 캡 기준으로도 충분히 여유면 goodTime 누적
                if (frameMs < nextTargetMs * upThresholdRatio)
                {
                    _goodTime += sampleInterval;
                    if (_goodTime >= upHoldSeconds)
                    {
                        _goodTime = 0f;
                        SetCapImmediate(nextHigher);
                    }
                }
                else
                {
                    _goodTime = 0f;
                }
            }
        }

        private void StepDown()
        {
            if (_capIndex < 0) return;
            int next = Mathf.Min(_capIndex + 1, Caps.Length - 1);
            if (next != _capIndex)
                SetCapImmediate(Caps[next]);
        }

        private int GetNextHigherCapWithinMax()
        {
            int allowedMax = ClampToAllowed(maxCap);
            if (_capIndex < 0) return allowedMax;

            // Caps는 높은->낮은 배열이므로 "더 높은 캡"은 index 감소
            for (int i = _capIndex - 1; i >= 0; i--)
            {
                int candidate = Caps[i];
                if (candidate <= allowedMax)
                    return candidate;
            }
            return Caps[_capIndex];
        }

        private int ClampToAllowed(int cap)
        {
            // 가장 가까운 허용 단계로 내림 매칭
            int best = Caps[Caps.Length - 1];
            for (int i = 0; i < Caps.Length; i++)
            {
                if (Caps[i] <= cap)
                {
                    best = Caps[i];
                    break;
                }
            }
            return best;
        }

        private void SetCapImmediate(int fps)
        {
            QualitySettings.vSyncCount = 0;

            // 우리가 원하는 목표값을 캐시해둠 (LateUpdate에서 항상 재주입)
            _desiredFrameRate = (fps < 0) ? -1 : fps;
            _hasDesiredFrameRate = true;

            if (fps < 0)
            {
                // -1: unlimited
                Application.targetFrameRate = -1;
                _capIndex = -1;
                return;
            }

            // Caps 배열에서 인덱스 찾기
            int idx = System.Array.IndexOf(Caps, fps);
            if (idx < 0)
                fps = ClampToAllowed(fps);

            idx = System.Array.IndexOf(Caps, fps);
            _capIndex = Mathf.Max(0, idx);

            Application.targetFrameRate = Caps[_capIndex];
        }
    }
}
