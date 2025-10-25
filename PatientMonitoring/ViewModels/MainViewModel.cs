using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.ML.OnnxRuntime;
using PatientMonitoring.Models;
using PatientMonitoring.Onnx;
using PatientMonitoring.Services;
using PatientMonitoring.Views;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace PatientMonitoring.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty] private bool isRunning;
        [ObservableProperty] private bool showCursorPoint = true; // keeps cursor on Plot1/2
        [ObservableProperty] private bool showHeartRate = true;
        [ObservableProperty] private bool showBreathRate = true;
        [ObservableProperty] private bool showT1Series = true;
        [ObservableProperty] private bool showT2Series = true;
        [ObservableProperty] private int hRValue;
        [ObservableProperty] private int rRValue;
        [ObservableProperty] private float t1;
        [ObservableProperty] private float t2;
        [ObservableProperty] private string currentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        [ObservableProperty] private int selectedTimeRangeHours = 1; // default 1 hour
        [ObservableProperty] private bool isPaused; // pause flag: true => ignore incoming data
        [ObservableProperty] private int spO2Value;
        [ObservableProperty] private bool spo2BlinkOn = true; // keep always on (no blink)
        [ObservableProperty] private bool showSpO2Value = true;

        // Bước điều chỉnh Y (đơn vị ADC count)
        private const double YAdjustStep = 100.0;

        // Các nút điều chỉnh Y cho Plot1/Plot2
        [RelayCommand]
        private void IncreasePlot1YMax() => _plots.IncreasePlot1YMax(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot1YMax() => _plots.DecreasePlot1YMax(YAdjustStep);
        [RelayCommand]
        private void IncreasePlot1YMin() => _plots.IncreasePlot1YMin(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot1YMin() => _plots.DecreasePlot1YMin(YAdjustStep);



        [RelayCommand]
        private void IncreasePlot2YMax() => _plots.IncreasePlot2YMax(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot2YMax() => _plots.DecreasePlot2YMax(YAdjustStep);
        [RelayCommand]
        private void IncreasePlot2YMin() => _plots.IncreasePlot2YMin(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot2YMin() => _plots.DecreasePlot2YMin(YAdjustStep);


        [RelayCommand]
        private void IncreasePlot3YMax() => _plots.IncreasePlot3YMax(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot3YMax() => _plots.DecreasePlot3YMax(YAdjustStep);
        [RelayCommand]
        private void IncreasePlot3YMin() => _plots.IncreasePlot3YMin(YAdjustStep);
        [RelayCommand]
        private void DecreasePlot3YMin() => _plots.DecreasePlot3YMin(YAdjustStep);


        // Flag phát hiện không có người đo (tín hiệu std thấp đủ lâu)
        [ObservableProperty] private bool noPersonMeasuring = true;

        // Thuộc tính điều khiển overlay chớp tắt
        [ObservableProperty] private bool showNoPersonOverlay;

        // HR/RR hiển thị: "--" khi NoPerson
        public string HRDisplay => NoPersonMeasuring ? "--" : HRValue.ToString(CultureInfo.InvariantCulture);
        public string RRDisplay => NoPersonMeasuring ? "--" : RRValue.ToString(CultureInfo.InvariantCulture);
        public string Spo2Display => NoPersonMeasuring || SpO2Value <= 0 ? "--" : SpO2Value.ToString(CultureInfo.InvariantCulture);

        // Ngưỡng cảnh báo
        private const int HrHighThreshold = 100;
        private const int HrLowThreshold = 60;
        private const int RrHighThreshold = 30;
        private const int RrLowThreshold = 10;
        private const int Spo2LowThreshold = 90;

        // Cảnh báo và thông điệp (tính từ HR/RR hiện tại)
        public bool HrAlertActive => !NoPersonMeasuring && HRValue > 0 && (HRValue > HrHighThreshold || HRValue < HrLowThreshold);
        public string HrAlertMessage => !HrAlertActive ? string.Empty : (HRValue > HrHighThreshold ? "Heart rate is high" : "Heart rate is low");
        public bool RrAlertActive => !NoPersonMeasuring && RRValue > 0 && (RRValue > RrHighThreshold || RRValue < RrLowThreshold);
        public string RrAlertMessage => !RrAlertActive ? string.Empty : (RRValue > RrHighThreshold ? "Respiration rate is high" : "Respiration rate is low");
        public bool Spo2AlertActive => !NoPersonMeasuring && SpO2Value > 0 && SpO2Value < Spo2LowThreshold;
        public string Spo2AlertMessage => !Spo2AlertActive ? string.Empty : "SpO2 is low";

        // --- SpO2 calculation buffers and state (3s window @ 50Hz) ---
        private const int SpO2Window = 160;
        private readonly double[] _redBuf = new double[SpO2Window];
        private readonly double[] _irBuf = new double[SpO2Window];
        private int _spo2Idx, _spo2Count;
        private double _redSum, _redSumSq, _irSum, _irSumSq;
        private double _spo2Ema; // smoothing
        private const double Spo2EmaAlpha = 0.2;

        // Calibration: SpO2 = A - B * (k * R)
        private const double Spo2CoeffA = 121.0;
        private const double Spo2CoeffB = 9.0;
        private const double Spo2K = 1.0; // adjust 0.7..1.3 to calibrate if needed

        // Blink state cho icon
        [ObservableProperty] private bool hrBlinkOn = true;
        [ObservableProperty] private bool rrBlinkOn = true;

        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _noPersonBlinkTimer;

        // Timers cho chớp icon theo HR/RR
        private readonly DispatcherTimer _hrBlinkTimer;
        private readonly DispatcherTimer _rrBlinkTimer;

        private static readonly object _logLock = new();
        private readonly string _logFilePath;

        public ObservableCollection<DataPointModel> Samples { get; } = new();

        private readonly ChartModel _plots;
        public OxyPlot.PlotModel Plot1 => _plots.PlotValue1;
        public OxyPlot.PlotModel Plot2 => _plots.PlotValue2;
        public OxyPlot.PlotModel Plot3 => _plots.PlotValue3;
        public OxyPlot.PlotModel Plot4 => _plots.PlotValue4;

        private float[] ppg = new float[500];
        private float[] bcg = new float[500];
        private int count = 0;

        private CancellationTokenSource? _cts;

        // Tham số phát hiện "flat" bằng độ lệch chuẩn (std dev) trên cửa sổ trượt
        private const int FlatWindow = 50;                 // số mẫu để tính std (≈ thời lượng cửa sổ)
        private const float FlatStdThresholdCh1 = 10.0f;    // ngưỡng std cho kênh 1 (ADC count)
        private static readonly TimeSpan NoPersonDebounce = TimeSpan.FromSeconds(2); // yêu cầu flat liên tục >= 2s
        private DateTime? _flatSinceUtc;
        private int _totalSamples; // tránh báo flat sớm khi buffer chưa đủ dữ liệu

        // ==== PPG normalization + HR from v3 ====
        // Sample rate for MAX86150 stream
        private const int PpgSampleRateHz = 80;
        // High-pass cutoff to remove DC (Hz)
        private const double HpCutoffHz = 0.7;
        private readonly double _hpAlpha;
        private double _hpPrevX, _hpPrevY;

        // Simple moving average smoothing (low-pass)
        private const int SmoothWin = 5;
        private readonly double[] _smoothBuf = new double[SmoothWin];
        private int _smoothIdx, _smoothCount;
        private double _smoothSum;

        // Z-score normalization over sliding window
        private const int NormWindow = 160; // ~3s @50Hz
        private readonly double[] _normBuf = new double[NormWindow];
        private int _normIndex, _normCount;
        private double _normSum, _normSumSq;

        // Adaptive threshold via EMA of absolute amplitude
        private double _emaAbs;
        private const double EmaAlpha = 0.1;
        private const double ThresholdScale = 0.6;

        // Peak detection state
        private bool _isAbove;
        private double _candidateMax;
        private int _candidateIndex;
        private int _lastPeakIndex = int.MinValue / 4;
        private const int MinPeakDistanceSamples = PpgSampleRateHz / 3; // ~0.33s refractory

        // IBI buffer (store last N intervals in samples)
        private readonly int[] _ibiBuffer = new int[8];
        private int _ibiCount, _ibiWrite;

        // Running sample index for PPG
        private int _ppgSampleIndex;
        private int _ppgHrBpm; // latest BPM computed from PPG

        // ==== HR constraints (limit jumps and confirm outliers) ====
        private const int HrMinBpm = 40;
        private const int HrMaxBpm = 200;
        private const int HrMaxStepPerUpdate = 5;       // max bpm change per update
        private const int HrOutlierThreshold = 15;      // if delta > this, require confirmation
        private static readonly TimeSpan HrPendingTimeout = TimeSpan.FromSeconds(15);
        private int? _hrPendingCandidate;
        private DateTime _hrPendingAt;

        // ==== Respiration (RR) from BCG (v2) ====
        private const int BcgSampleRateHz = PpgSampleRateHz;   // assume same stream rate
        private const double BcgHpCutoffHz = 0.1;              // remove drift (<0.1 Hz)
        private readonly double _bcgHpAlpha;
        private double _bcgPrevX, _bcgPrevY;

        private const int BcgSmoothWin = 7;
        private readonly double[] _bcgSmoothBuf = new double[BcgSmoothWin];
        private int _bcgSmoothIdx, _bcgSmoothCount;
        private double _bcgSmoothSum;

        // Adaptive envelope threshold
        private double _rrEmaAbs;
        private const double RrEmaAlpha = 0.1;
        private const double RrThresholdScale = 0.6;

        // Peak detection for breaths
        private bool _rrIsAbove;
        private double _rrCandidateMax;
        private int _rrCandidateIndex;
        private int _rrLastPeakIndex = int.MinValue / 4;
        private readonly int _rrMinPeakDistanceSamples = (int)(BcgSampleRateHz * 1.0); // ~1.0s refractory

        // IBI buffer (breath intervals in samples) -> RR smoothing
        private readonly int[] _rrIbiBuffer = new int[6];
        private int _rrIbiCount, _rrIbiWrite;

        private int _bcgSampleIndex;
        private int _rrBpm; // latest computed RR (breaths/min)

        // PPG baseline for plotting (EMA)
        private double _ppgDcEma;
        private bool _ppgDcInit;
        private const double PpgDcAlpha = 0.01;

        public MainViewModel()
        {
            _plots = new ChartModel(Config.MaxPoints)
            {
                ShowCursorPoint = ShowCursorPoint,
                ShowHeartRate = ShowHeartRate,
                ShowBreathRate = ShowBreathRate,
                ShowT1 = ShowT1Series,
                ShowT2 = ShowT2Series,
                ShowSpO2 = ShowSpO2Value
            };
            _plots.ApplyCursorVisibility();
            _plots.ApplyHistoryVisibility();
            _plots.SetTimeRangeHours(SelectedTimeRangeHours); // initialize Plot3 axis

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _clockTimer.Tick += (_, _) => CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _clockTimer.Start();

            // Timer nháy overlay "No person"
            _noPersonBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _noPersonBlinkTimer.Tick += (_, _) =>
            {
                if (NoPersonMeasuring)
                    ShowNoPersonOverlay = !ShowNoPersonOverlay;
                else
                    ShowNoPersonOverlay = false;
            };

            // Timers blink cho HR/RR (toggle Opacity qua HrBlinkOn/RrBlinkOn)
            _hrBlinkTimer = new DispatcherTimer();
            _hrBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
            _hrBlinkTimer.Tick += (_, _) => HrBlinkOn = !HrBlinkOn;

            _rrBlinkTimer = new DispatcherTimer();
            _rrBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
            _rrBlinkTimer.Tick += (_, _) => RrBlinkOn = !RrBlinkOn;

            _logFilePath = "patient_vitals_log.csv";
            EnsureLogFile();

            // High-pass alpha precompute
            var rc = 1.0 / (2.0 * Math.PI * HpCutoffHz);
            var dt = 1.0 / PpgSampleRateHz;
            _hpAlpha = rc / (rc + dt);

            // BCG high-pass precompute for RR
            var rrRc = 1.0 / (2.0 * Math.PI * BcgHpCutoffHz);
            var rrDt = 1.0 / BcgSampleRateHz;
            _bcgHpAlpha = rrRc / (rrRc + rrDt);
        }

        partial void OnSelectedTimeRangeHoursChanged(int value)
        {
            _plots.SetTimeRangeHours(value);
        }

        partial void OnShowCursorPointChanged(bool value)
        {
            _plots.ShowCursorPoint = value;
            _plots.ApplyCursorVisibility();
        }

        partial void OnShowHeartRateChanged(bool value)
        {
            _plots.ShowHeartRate = value;
            _plots.ApplyHistoryVisibility();
            _plots.LoadHistory();
        }

        partial void OnShowBreathRateChanged(bool value)
        {
            _plots.ShowBreathRate = value;
            _plots.ApplyHistoryVisibility();
            _plots.LoadHistory();
        }

        partial void OnShowT1SeriesChanged(bool value)
        {
            _plots.ShowT1 = value;
            _plots.ApplyHistoryVisibility();
            _plots.LoadHistory();
        }

        partial void OnShowT2SeriesChanged(bool value)
        {
            _plots.ShowT2 = value;
            _plots.ApplyHistoryVisibility();
            _plots.LoadHistory();
        }

        partial void OnShowSpO2ValueChanged(bool value)
        {
            _plots.ShowSpO2 = value;
            _plots.ApplyHistoryVisibility();
            _plots.LoadHistory();
        }

        // Cập nhật chu kỳ blink và cảnh báo khi giá trị HR/RR đổi
        partial void OnHRValueChanged(int value)
        {
            OnPropertyChanged(nameof(HRDisplay));
            OnPropertyChanged(nameof(HrAlertActive));
            OnPropertyChanged(nameof(HrAlertMessage));
            UpdateHrBlinkTimer();
        }

        partial void OnRRValueChanged(int value)
        {
            OnPropertyChanged(nameof(RRDisplay));
            OnPropertyChanged(nameof(RrAlertActive));
            OnPropertyChanged(nameof(RrAlertMessage));
            UpdateHrBlinkTimer();
        }

        // Notify XAML bindings when SpO2Value changes
        partial void OnSpO2ValueChanged(int value)
        {
            OnPropertyChanged(nameof(Spo2Display));
            OnPropertyChanged(nameof(Spo2AlertActive));
            OnPropertyChanged(nameof(Spo2AlertMessage));
        }

        // Khi thay đổi trạng thái NoPersonMeasuring -> bật/tắt nháy overlay và icon; reset bộ đếm
        partial void OnNoPersonMeasuringChanged(bool value)
        {
            if (value)
            {
                ShowNoPersonOverlay = true;
                if (!_noPersonBlinkTimer.IsEnabled) _noPersonBlinkTimer.Start();

                // No person: stop blinking and keep icons steadily on
                StopBlinkTimersAndHoldIconsOn();

                // Reset counters to avoid carrying partial calculations
                count = 0;
            }
            else
            {
                if (_noPersonBlinkTimer.IsEnabled) _noPersonBlinkTimer.Stop();
                ShowNoPersonOverlay = false;

                // Person detected: enable blinking
                UpdateHrBlinkTimer();
            }

            // Force re-evaluate displays and alerts
            OnPropertyChanged(nameof(HRDisplay));
            OnPropertyChanged(nameof(RRDisplay));
            OnPropertyChanged(nameof(Spo2Display));
            OnPropertyChanged(nameof(Spo2AlertActive));
            OnPropertyChanged(nameof(Spo2AlertMessage));
            OnPropertyChanged(nameof(HrAlertActive));
            OnPropertyChanged(nameof(HrAlertMessage));
            OnPropertyChanged(nameof(RrAlertActive));
            OnPropertyChanged(nameof(RrAlertMessage));
        }

        // --- update EnsureLogFile header to include SpO2
        private void EnsureLogFile()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    var header = "Timestamp,HeartRate,BreathRate,T1,T2,SpO2" + Environment.NewLine;
                    File.WriteAllText(_logFilePath, header);
                }
            }
            catch { }
        }

        // --- update LogVitals to append SpO2
        private void LogVitals()
        {
            try
            {
                string line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss},{HRValue},{RRValue},{T1.ToString(CultureInfo.InvariantCulture)},{T2.ToString(CultureInfo.InvariantCulture)},{SpO2Value}";
                lock (_logLock)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch { }
        }

        private float[] PushBack(float[] array, float newValue)
        {
            float[] newArray = new float[array.Length];
            for (int i = 1; i < array.Length; i++)
            {
                newArray[i - 1] = array[i];
            }
            newArray[^1] = newValue;
            return newArray;
        }

        /// <summary>
        /// Xử lý một mẫu dữ liệu mới
        /// v1: ECG/BCG? short, v2: BCG short, v3: RED (MAX86150) uint, v4: IR (MAX86150) uint, v5/v6: temps (raw)
        /// </summary>
        private void AddSample(short v1, short v2, uint v3, uint v4, ushort v5, ushort v6)
        {
            // Cập nhật buffer để phát hiện NoPerson
            bcg = PushBack(bcg, v2);
            ppg = PushBack(ppg, v3);
            _totalSamples++;

            // Ngưỡng PPG: nếu RED và IR đều dưới 20000 -> không vẽ, báo "No person"
            const uint PpgThreshold = 20000;
            if (v3 < PpgThreshold && v4 < PpgThreshold)
            {
                if (!NoPersonMeasuring) NoPersonMeasuring = true;
                return; // không vẽ, không tính toán
            }
            else
            {
                if (NoPersonMeasuring) NoPersonMeasuring = false;
            }

            // Phát hiện "No person" theo std-dev BCG (lớp bảo vệ bổ sung)
            CheckNoPerson();
            if (NoPersonMeasuring) return;

            // HR from RED (v3)
            var (zNorm, ppgFilteredForPlot) = ProcessPpgAndGetNormalized(v3);
            UpdateHeartRateFromPpg(zNorm);

            // RR from BCG (v2)
            double bcgFiltered = ProcessBcgAndGetFiltered(v2);
            UpdateRespRateFromBcg(bcgFiltered);

            // Tính SpO2 (RED/IR)
            AccumulateSpO2Window(v3, v4);

            // Vẽ tín hiệu đã filter
            short bcgFilteredShort = (short)Math.Clamp(Math.Round(bcgFiltered), short.MinValue, short.MaxValue);
            uint ppgFilteredUint = (uint)Math.Clamp(Math.Round(ppgFilteredForPlot), 0.0, uint.MaxValue);
            _plots.AddData(v1, bcgFilteredShort, ppgFilteredUint);

            Samples.Add(new DataPointModel(DateTime.Now, v1, v2, v3, v4, v5, v6));
            if (Samples.Count > 2000) Samples.RemoveAt(0);

            count++;
            if (count == 100)
            {
                // Note: v3/v4 are RED/IR; if v5/v6 are temps, move these lines accordingly.
                T1 = MathF.Round(v3 * 0.02f - 273.15f, 1);
                T2 = MathF.Round(v4 * 0.02f - 273.15f, 1);

                OutputRuntime.counthr++;

                if (OutputRuntime.counthr == 5)
                {
                    OutputRuntime.counthr = 0;

                    if (_ppgHrBpm > 0)
                        HRValue = ApplyHrConstraints(_ppgHrBpm);

                    if (_rrBpm > 0)
                        RRValue = _rrBpm;

                    // Commit SpO2
                    CommitSpO2FromWindow();

                    LogVitals();
                }
                count = 0;
            }
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private void Start()
        {
            // Resume if already running but paused
            if (IsRunning && IsPaused)
            {
                IsPaused = false;
                OnPropertyChanged(nameof(CanStart));
                return;
            }

            // Start new session
            _cts = new CancellationTokenSource();
            IsRunning = true;
            IsPaused = false;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));

            var client = new TcpSensorClient(Config.Host, Config.Port);
            client.OnPayload += (v1, v2, v3, v4, v5, v6) =>
            {
                // Pause gate: ignore incoming data while paused
                if (!IsPaused)
                {
                    DispatchUI(() => AddSample(v1, v2, v3, v4, v5, v6));
                }
            };

            Task.Run(() => client.RunAsync(_cts.Token));
        }

        // Allow Start when not running or when paused (to resume)
        private bool CanStart() => !IsRunning || IsPaused;

        // Always enable Stop
        private bool CanStop() => true;

        [RelayCommand(CanExecute = nameof(CanStop))]
        private void Stop()
        {
            // Pause only (do not cancel the connection, do not set IsRunning=false)
            IsPaused = true;
            OnPropertyChanged(nameof(CanStart));
        }

        [RelayCommand]
        private void Clear()
        {
            _plots.Clear();
            Samples.Clear();

            // Reset phát hiện flat
            _flatSinceUtc = null;
            _totalSamples = 0;
            NoPersonMeasuring = false;
            ShowNoPersonOverlay = false;
            if (_noPersonBlinkTimer.IsEnabled) _noPersonBlinkTimer.Stop();

            // Dừng blink icon và để sáng ổn định
            StopBlinkTimersAndHoldIconsOn();

            // Reset PPG/HR states
            _hpPrevX = _hpPrevY = 0;
            _smoothIdx = _smoothCount = 0; _smoothSum = 0; Array.Clear(_smoothBuf, 0, _smoothBuf.Length);
            _normIndex = _normCount = 0; _normSum = _normSumSq = 0; Array.Clear(_normBuf, 0, _normBuf.Length);
            _ppgDcEma = 0; _ppgDcInit = false;
            _emaAbs = 0; _isAbove = false; _candidateMax = 0; _candidateIndex = 0; _lastPeakIndex = int.MinValue / 4;
            _ibiCount = _ibiWrite = 0; Array.Clear(_ibiBuffer, 0, _ibiBuffer.Length);
            _ppgSampleIndex = 0; _ppgHrBpm = 0;

            // Reset BCG/RR states
            _bcgPrevX = _bcgPrevY = 0;
            _bcgSmoothIdx = _bcgSmoothCount = 0; _bcgSmoothSum = 0; Array.Clear(_bcgSmoothBuf, 0, _bcgSmoothBuf.Length);
            _rrEmaAbs = 0; _rrIsAbove = false; _rrCandidateMax = 0; _rrCandidateIndex = 0; _rrLastPeakIndex = int.MinValue / 4;
            _rrIbiCount = _rrIbiWrite = 0; Array.Clear(_rrIbiBuffer, 0, _rrIbiBuffer.Length);
            _bcgSampleIndex = 0; _rrBpm = 0;

            // Reset HR constraint state
            _hrPendingCandidate = null;
            _hrPendingAt = default;

            // Cập nhật lại trạng thái hiển thị và cảnh báo
            OnPropertyChanged(nameof(HRDisplay));
            OnPropertyChanged(nameof(RRDisplay));
            OnPropertyChanged(nameof(Spo2Display));
            OnPropertyChanged(nameof(Spo2AlertActive));
            OnPropertyChanged(nameof(Spo2AlertMessage));
            OnPropertyChanged(nameof(HrAlertActive));
            OnPropertyChanged(nameof(HrAlertMessage));
            OnPropertyChanged(nameof(RrAlertActive));
            OnPropertyChanged(nameof(RrAlertMessage));
        }

        [RelayCommand]
        private void Setting()
        {
            var win = new SettingPage
            {
                Owner = Application.Current?.MainWindow,
                DataContext = new SettingViewModel()
            };
            win.Show();
        }

        [RelayCommand]
        private void Quit()
        {
            try { _cts?.Cancel(); } catch { }
            Application.Current?.Shutdown();
        }

        private void DispatchUI(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a();
            else d.Invoke(a);
        }

        // ==== Flat detection (std deviation) helpers === =
        private void CheckNoPerson()
        {
            if (_totalSamples < FlatWindow)
            {
                _flatSinceUtc = null;
                if (NoPersonMeasuring) NoPersonMeasuring = false;
                return;
            }

            bool flat1 = IsLowDeviation(bcg, FlatWindow, FlatStdThresholdCh1);

            if (flat1)
            {
                _flatSinceUtc ??= DateTime.UtcNow;
                if (!NoPersonMeasuring && DateTime.UtcNow - _flatSinceUtc.Value >= NoPersonDebounce)
                {
                    NoPersonMeasuring = true;
                }
            }
            else
            {
                _flatSinceUtc = null;
                if (NoPersonMeasuring) NoPersonMeasuring = false;
            }
        }

        private static bool IsLowDeviation(float[] data, int window, float stdThreshold)
        {
            int start = Math.Max(0, data.Length - window);
            int n = data.Length - start;
            if (n <= 1) return false;

            double sum = 0d, sumSq = 0d;
            for (int i = start; i < data.Length; i++)
            {
                float v = data[i];
                if (!float.IsFinite(v)) return false;
                sum += v;
                sumSq += (double)v * v;
            }

            double mean = sum / n;
            double variance = Math.Max(0.0, (sumSq / n) - (mean * mean));
            double std = Math.Sqrt(variance);

            return std <= stdThreshold;
        }

        // ==== Blink helpers ====

        private void StopBlinkTimersAndHoldIconsOn()
        {
            if (_hrBlinkTimer.IsEnabled) _hrBlinkTimer.Stop();
            if (_rrBlinkTimer.IsEnabled) _rrBlinkTimer.Stop();
            HrBlinkOn = true;
            RrBlinkOn = true;
        }

        private void UpdateHrBlinkTimer()
        {
            // Không blink khi NoPerson hoặc đang cảnh báo HR
            if (NoPersonMeasuring || HrAlertActive || HRValue <= 0)
            {
                if (_hrBlinkTimer.IsEnabled) _hrBlinkTimer.Stop();
                HrBlinkOn = true;
                return;
            }
            if (!_hrBlinkTimer.IsEnabled) _hrBlinkTimer.Start();
        }

        // ==== PPG processing helpers ====

        // High-pass, smooth, z-score normalize AND provide filtered value for plotting
        private (double zNorm, double filteredForPlot) ProcessPpgAndGetNormalized(uint v3Raw)
        {
            // Remove DC with 1st-order high-pass
            double x = v3Raw;

            // Initialize baseline once
            if (!_ppgDcInit)
            {
                _ppgDcEma = x;
                _ppgDcInit = true;
            }
            else
            {
                // Slow EMA baseline (low-pass)
                _ppgDcEma = (1.0 - PpgDcAlpha) * _ppgDcEma + PpgDcAlpha * x;
            }

            double y = _hpAlpha * (_hpPrevY + x - _hpPrevX);
            _hpPrevX = x;
            _hpPrevY = y;

            // Moving average smoothing
            if (_smoothCount < SmoothWin) _smoothCount++;
            _smoothSum -= _smoothBuf[_smoothIdx];
            _smoothBuf[_smoothIdx] = y;
            _smoothSum += y;
            _smoothIdx++;
            if (_smoothIdx >= SmoothWin) _smoothIdx = 0;
            double ySmooth = _smoothSum / Math.Max(1, _smoothCount);

            // Sliding window z-score normalization
            if (_normCount < NormWindow) _normCount++;
            _normSum -= _normBuf[_normIndex];
            _normSumSq -= _normBuf[_normIndex] * _normBuf[_normIndex];
            _normBuf[_normIndex] = ySmooth;
            _normSum += ySmooth;
            _normSumSq += ySmooth * ySmooth;
            _normIndex++;
            if (_normIndex >= NormWindow) _normIndex = 0;

            double mean = _normSum / Math.Max(1, _normCount);
            double var = Math.Max(1e-9, (_normSumSq / Math.Max(1, _normCount)) - mean * mean);
            double std = Math.Sqrt(var);
            double z = (ySmooth - mean) / std;

            // For plotting: detrended + smoothed (baseline + AC)
            double filteredForPlot = _ppgDcEma + ySmooth;

            _ppgSampleIndex++; // advance sample counter
            return (z, filteredForPlot);
        }

        private void UpdateHeartRateFromPpg(double zNorm)
        {
            // Adaptive threshold from envelope
            _emaAbs = (1.0 - EmaAlpha) * _emaAbs + EmaAlpha * Math.Abs(zNorm);
            double threshold = ThresholdScale * _emaAbs;

            // Track segments above threshold, pick maximum as peak
            if (zNorm > threshold)
            {
                if (!_isAbove)
                {
                    _isAbove = true;
                    _candidateMax = zNorm;
                    _candidateIndex = _ppgSampleIndex;
                }
                else if (zNorm > _candidateMax)
                {
                    _candidateMax = zNorm;
                    _candidateIndex = _ppgSampleIndex;
                }
            }
            else
            {
                if (_isAbove)
                {
                    // Crossing down -> commit peak
                    int peakIdx = _candidateIndex;
                    if (peakIdx - _lastPeakIndex >= MinPeakDistanceSamples)
                    {
                        int ibi = peakIdx - _lastPeakIndex;
                        _lastPeakIndex = peakIdx;

                        if (ibi > 0)
                        {
                            // Keep plausible range by checking BPM bounds
                            double bpmInstant = 60.0 * PpgSampleRateHz / ibi;
                            if (bpmInstant >= 40 && bpmInstant <= 200)
                            {
                                // Store IBI and compute BPM from mean IBI (more stable)
                                _ibiBuffer[_ibiWrite] = ibi;
                                _ibiWrite = (_ibiWrite + 1) % _ibiBuffer.Length;
                                if (_ibiCount < _ibiBuffer.Length) _ibiCount++;

                                double meanIbi = 0;
                                for (int i = 0; i < _ibiCount; i++) meanIbi += _ibiBuffer[i];
                                meanIbi /= Math.Max(1, _ibiCount);

                                int bpm = (int)Math.Round(60.0 * PpgSampleRateHz / meanIbi);
                                if (bpm >= 40 && bpm <= 200)
                                {
                                    _ppgHrBpm = bpm;
                                }
                            }
                        }
                    }
                    _isAbove = false;
                }
            }
        }

        // ==== HR constraints helper ====
        private int ApplyHrConstraints(int candidateBpm)
        {
            // Basic bounds check
            if (candidateBpm < HrMinBpm || candidateBpm > HrMaxBpm)
                return HRValue > 0 ? HRValue : Math.Clamp(candidateBpm, HrMinBpm, HrMaxBpm);

            // First valid reading
            if (HRValue <= 0)
            {
                _hrPendingCandidate = null;
                return candidateBpm;
            }

            int delta = candidateBpm - HRValue;
            int absDelta = Math.Abs(delta);

            // Small change -> allow but limit max step
            if (absDelta <= HrOutlierThreshold)
            {
                int step = Math.Min(absDelta, HrMaxStepPerUpdate);
                return HRValue + Math.Sign(delta) * step;
            }

            // Large jump -> require confirmation
            var now = DateTime.UtcNow;

            // If we have a pending candidate close to this one and still valid -> accept with ramp
            if (_hrPendingCandidate.HasValue &&
                now - _hrPendingAt <= HrPendingTimeout &&
                Math.Abs(candidateBpm - _hrPendingCandidate.Value) <= HrMaxStepPerUpdate)
            {
                _hrPendingCandidate = null;
                int step = Math.Min(absDelta, HrMaxStepPerUpdate * 2); // ramp a bit faster after confirm
                return HRValue + Math.Sign(delta) * step;
            }

            // Start/refresh pending candidate and keep current value this cycle
            _hrPendingCandidate = candidateBpm;
            _hrPendingAt = now;
            return HRValue;
        }
        // Maintain sliding window for SpO2 (do not set UI value here)
        private void AccumulateSpO2Window(uint redRaw, uint irRaw)
        {
            double red = redRaw;
            double ir = irRaw;

            // subtract outgoing
            _redSum   -= _redBuf[_spo2Idx];
            _redSumSq -= _redBuf[_spo2Idx] * _redBuf[_spo2Idx];
            _irSum    -= _irBuf[_spo2Idx];
            _irSumSq  -= _irBuf[_spo2Idx]  * _irBuf[_spo2Idx];

            // add incoming
            _redBuf[_spo2Idx] = red;   _redSum += red;   _redSumSq += red * red;
            _irBuf[_spo2Idx]  = ir;    _irSum  += ir;    _irSumSq  += ir  * ir;

            _spo2Idx++;
            if (_spo2Idx >= SpO2Window) _spo2Idx = 0;
            if (_spo2Count < SpO2Window) _spo2Count++;
        }

        // Compute SpO2 from the current sliding window and commit to SpO2Value
        private void CommitSpO2FromWindow()
        {
            if (_spo2Count < SpO2Window / 3) return; // need enough samples in the window

            double n = _spo2Count;
            double dcRed = _redSum / n;
            double dcIr  = _irSum  / n;

            // AC via stddev
            double varRed = Math.Max(0, (_redSumSq / n) - dcRed * dcRed);
            double varIr  = Math.Max(0, (_irSumSq  / n) - dcIr  * dcIr);
            double acRed  = Math.Sqrt(varRed);
            double acIr   = Math.Sqrt(varIr);

            // guard
            if (dcRed <= 1 || dcIr <= 1 || acRed <= 1e-6 || acIr <= 1e-6) return;

            // ratio-of-ratios
            double r = (acRed / dcRed) / (acIr / dcIr);

            // Linear approximation; clamp
            double spo2 = Spo2CoeffA - Spo2CoeffB * r;
            spo2 = Math.Clamp(spo2, 90.0, 100.0);

            // smooth at commit time
            _spo2Ema = (SpO2Value <= 0) ? spo2 : (1 - Spo2EmaAlpha) * _spo2Ema + Spo2EmaAlpha * spo2;

            int spo2Int = (int)Math.Round(_spo2Ema);
            if (spo2Int != SpO2Value)
                SpO2Value = spo2Int;
        }

        // Filter BCG (v2): high-pass + moving average smoothing
        private double ProcessBcgAndGetFiltered(short v2Raw)
        {
            double x = v2Raw;
            double y = _bcgHpAlpha * (_bcgPrevY + x - _bcgPrevX); // high-pass
            _bcgPrevX = x;
            _bcgPrevY = y;

            // moving average
            if (_bcgSmoothCount < BcgSmoothWin) _bcgSmoothCount++;
            _bcgSmoothSum -= _bcgSmoothBuf[_bcgSmoothIdx];
            _bcgSmoothBuf[_bcgSmoothIdx] = y;
            _bcgSmoothSum += y;
            _bcgSmoothIdx = (_bcgSmoothIdx + 1) % BcgSmoothWin;

            _bcgSampleIndex++;
            return _bcgSmoothSum / Math.Max(1, _bcgSmoothCount);
        }

        private void UpdateRespRateFromBcg(double bcgFiltered)
        {
            // Adaptive threshold from envelope
            _rrEmaAbs = (1.0 - RrEmaAlpha) * _rrEmaAbs + RrEmaAlpha * Math.Abs(bcgFiltered);
            double thr = RrThresholdScale * _rrEmaAbs;

            if (bcgFiltered > thr)
            {
                if (!_rrIsAbove)
                {
                    _rrIsAbove = true;
                    _rrCandidateMax = bcgFiltered;
                    _rrCandidateIndex = _bcgSampleIndex;
                }
                else if (bcgFiltered > _rrCandidateMax)
                {
                    _rrCandidateMax = bcgFiltered;
                    _rrCandidateIndex = _bcgSampleIndex;
                }
            }
            else
            {
                if (_rrIsAbove)
                {
                    int peakIdx = _rrCandidateIndex;
                    if (peakIdx - _rrLastPeakIndex >= _rrMinPeakDistanceSamples)
                    {
                        int ibi = peakIdx - _rrLastPeakIndex;
                        _rrLastPeakIndex = peakIdx;

                        if (ibi > 0)
                        {
                            // breaths per minute
                            double bpmInstant = 60.0 * BcgSampleRateHz / ibi;
                            if (bpmInstant >= 6 && bpmInstant <= 40)
                            {
                                _rrIbiBuffer[_rrIbiWrite] = ibi;
                                _rrIbiWrite = (_rrIbiWrite + 1) % _rrIbiBuffer.Length;
                                if (_rrIbiCount < _rrIbiBuffer.Length) _rrIbiCount++;

                                double meanIbi = 0;
                                for (int i = 0; i < _rrIbiCount; i++) meanIbi += _rrIbiBuffer[i];
                                meanIbi /= Math.Max(1, _rrIbiCount);

                                int bpm = (int)Math.Round(60.0 * BcgSampleRateHz / meanIbi);
                                if (bpm >= 6 && bpm <= 40) _rrBpm = bpm;
                            }
                        }
                    }
                    _rrIsAbove = false;
                }
            }
        }
    }
}