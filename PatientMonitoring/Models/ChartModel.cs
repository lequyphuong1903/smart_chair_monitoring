using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PatientMonitoring.Models
{
    public class ChartModel
    {
        private readonly LineSeries _series1;
        private readonly LineSeries _series2;
        private readonly LineSeries _series3;

        private readonly ScatterSeries _cursorPoints1;
        private readonly ScatterSeries _cursorPoints2;
        private readonly ScatterSeries _cursorPoints3;

        // History scatter points for Plot3
        private readonly ScatterSeries _historyHRPoints;  // HeartRate
        private readonly ScatterSeries _historyBRPoints;  // BreathRate
        private readonly ScatterSeries _historyT1Points;  // T1
        private readonly ScatterSeries _historyT2Points;  // T2
        private readonly ScatterSeries _historySpO2Points; // SpO2

        private int _writeIndex = 0;
        private int _count = 0;

        public int MaxPoints { get; set; } = 1000;

        private int _timeRangeHours = 1; // hours window for PlotValue3 (stored, clamped in [0..24])
        private const string LogFilePath = "patient_vitals_log.csv";

        /// <summary>
        /// Hiển thị marker tại điểm hiện tại trên Plot1/Plot2.
        /// </summary>
        public bool ShowCursorPoint { get; set; } = true;

        // Visibility for Plot3 history series
        public bool ShowHeartRate { get; set; } = true;
        public bool ShowBreathRate { get; set; } = true;
        public bool ShowT1 { get; set; } = true;
        public bool ShowT2 { get; set; } = true;
        public bool ShowSpO2 { get; set; } = true;

        // Auto-scale toggles for Plot1/Plot2 (tắt mặc định)
        public bool AutoScalePlot1 { get; set; } = false;
        public bool AutoScalePlot2 { get; set; } = false;
        public bool AutoScalePlot3 { get; set; } = false;

        public PlotModel PlotValue1 { get; }
        public PlotModel PlotValue2 { get; }
        public PlotModel PlotValue3 { get; }
        public PlotModel PlotValue4 { get; }

        // Invalidation throttle (coalesce UI updates at 10 ms)
        private readonly DispatcherTimer _invalidateTimer;
        private volatile bool _needsInvalidatePlot1;
        private volatile bool _needsInvalidatePlot2;
        private volatile bool _needsInvalidatePlot3;
        private volatile bool _needsInvalidatePlot4;

        public ChartModel(int maxPoints = 500)
        {
            MaxPoints = maxPoints;

            PlotValue1 = CreatePlot(true);
            PlotValue2 = CreatePlot(true);
            PlotValue3 = CreatePlot(true);
            PlotValue4 = CreatePlot(false);

            _series1 = new LineSeries
            {
                Color = OxyColors.Green,
                StrokeThickness = 5,
                LineJoin = LineJoin.Round,
            };
            _series2 = new LineSeries
            {
                Color = OxyColors.Orange,
                StrokeThickness = 5,
                LineJoin = LineJoin.Round,
            };
            _series3 = new LineSeries
            {
                Color = OxyColors.DeepSkyBlue,
                StrokeThickness = 5,
                LineJoin = LineJoin.Round,
            };

            PlotValue1.Series.Add(_series1);
            PlotValue2.Series.Add(_series2);
            PlotValue3.Series.Add(_series3);

            _cursorPoints1 = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.Green,
                MarkerSize = 8,
                Title = null
            };
            _cursorPoints2 = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.Orange,
                MarkerSize = 8,
                Title = null
            };
            _cursorPoints3 = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.DeepSkyBlue,
                MarkerSize = 8,
                Title = null
            };

            // History series (colors aligned with dashboard)
            _historyHRPoints = new ScatterSeries
            {
                Title = "HR",
                MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.Orange,
                MarkerStroke = OxyColors.Transparent,
                MarkerSize = 2
            };
            _historyBRPoints = new ScatterSeries
            {
                Title = "BR",
                MarkerType = MarkerType.Triangle,
                MarkerFill = OxyColors.DeepSkyBlue,
                MarkerStroke = OxyColors.Transparent,
                MarkerSize = 2
            };
            _historyT1Points = new ScatterSeries
            {
                Title = "T1",
                MarkerType = MarkerType.Diamond,
                MarkerFill = OxyColors.MediumPurple,
                MarkerStroke = OxyColors.Transparent,
                MarkerSize = 2
            };
            _historyT2Points = new ScatterSeries
            {
                Title = "T2",
                MarkerType = MarkerType.Square,
                MarkerFill = OxyColors.LimeGreen,
                MarkerStroke = OxyColors.Transparent,
                MarkerSize = 2
            };
            _historySpO2Points = new ScatterSeries
            {
                Title = "SpO2",
                MarkerType = MarkerType.Square,
                MarkerFill = OxyColors.Red,
                MarkerStroke = OxyColors.Transparent,
                MarkerSize = 2
            };

            // Khởi tạo trước
            ResetAxes(PlotValue1);
            ResetAxes(PlotValue2);
            ResetAxes(PlotValue3);
            ApplyFixedXAxis(PlotValue1);
            ApplyFixedXAxis(PlotValue2);
            ApplyFixedXAxis(PlotValue3);

            // Enable Plot3 Y-axis autoscale
            EnableAutoScaleYAxis(PlotValue3);

            // Initial visibility
            ApplyCursorVisibility();
            ApplyHistoryVisibility();

            // Start invalidation coalescing timer (10 ms)
            _invalidateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _invalidateTimer.Tick += (s, e) =>
            {
                // KHÔNG autoscale cho Plot1/Plot2 nữa (đã tắt)
                if (_needsInvalidatePlot1)
                {
                    _needsInvalidatePlot1 = false;
                    PlotValue1.InvalidatePlot(false);
                }
                if (_needsInvalidatePlot2)
                {
                    _needsInvalidatePlot2 = false;
                    PlotValue2.InvalidatePlot(false);
                }
                if (_needsInvalidatePlot3)
                {
                    _needsInvalidatePlot3 = false;
                    // refresh=true to let OxyPlot recompute auto-axes for Plot3
                    PlotValue3.InvalidatePlot(true);
                }
                if (_needsInvalidatePlot4)
                {
                    _needsInvalidatePlot4 = false;
                    PlotValue4.InvalidatePlot(false);
                }
            };
            _invalidateTimer.Start();
        }

        private PlotModel CreatePlot(bool hideXAxis)
        {
            var bg = OxyColors.Black; // Default dark background
            var pm = new PlotModel
            {
                PlotAreaBorderThickness = new OxyThickness(0),
                Background = bg,
                PlotAreaBackground = bg
            };
            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                Minimum = 0,
                Maximum = MaxPoints - 1
            };
            var yAxis = new LinearAxis { };

            if (!hideXAxis)
            {
                // Plot4: show Y axis with white text/ticks/axis line + fixed range (no autoscale)
                yAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    IsPanEnabled = false,
                    IsZoomEnabled = false,
                    IsAxisVisible = true,
                    TextColor = OxyColors.White,
                    TicklineColor = OxyColors.White,
                    AxislineColor = OxyColors.White,
                    MajorGridlineStyle = LineStyle.None,
                    MinorGridlineStyle = LineStyle.None,
                    Minimum = 0,
                    Maximum = 120
                };
            }
            else
            {
                // Plot1/Plot2/Plot3: default hidden axis visuals; ranges will be set by ResetAxes/EnableAutoScaleYAxis
                yAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    IsPanEnabled = false,
                    IsZoomEnabled = false,
                };
                HideAxisVisual(yAxis);
            }

            if (hideXAxis)
                HideAxisVisual(xAxis);

            pm.Axes.Add(xAxis);
            pm.Axes.Add(yAxis);

            return pm;
        }

        private static void HideAxisVisual(Axis axis)
        {
            try
            {
                axis.IsAxisVisible = false;
                return;
            }
            catch
            {
            }

            axis.MajorTickSize = 0;
            axis.MinorTickSize = 0;
            axis.TickStyle = TickStyle.None;
            axis.AxislineStyle = LineStyle.None;
            axis.TextColor = OxyColors.Transparent;
            axis.MajorGridlineStyle = LineStyle.None;
            axis.MinorGridlineStyle = LineStyle.None;
        }

        private void ConfigureTimeWindowAxis(PlotModel pm)
        {
            // Remove old X axis (if any)
            var oldX = pm.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (oldX != null)
                pm.Axes.Remove(oldX);

            var now = DateTime.Now;
            var currentHourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);

            int hoursClamped = Math.Clamp(_timeRangeHours, 0, 24);
            int effectiveHoursForWindow = Math.Max(1, hoursClamped);

            DateTime start;
            DateTime end;

            if (hoursClamped == 24)
            {
                start = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
                end = start.AddHours(24);
            }
            else
            {
                start = currentHourStart.AddHours(-(effectiveHoursForWindow - 1));
                end = currentHourStart.AddHours(1);
            }

            var total = end - start;
            var totalHours = total.TotalHours;

            const int targetDivisions = 24;
            double majorStepDays;
            DateTimeIntervalType intervalType;
            DateTimeIntervalType minorIntervalType = DateTimeIntervalType.Auto;

            if (hoursClamped == 24 || totalHours >= 24.0 - 1e-6)
            {
                intervalType = DateTimeIntervalType.Hours;
                majorStepDays = 1.0 / 24.0; // 1 hour
            }
            else
            {
                intervalType = DateTimeIntervalType.Auto;
                majorStepDays = total.TotalDays / targetDivisions;
            }

            var timeAxis = new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                Minimum = DateTimeAxis.ToDouble(start),
                Maximum = DateTimeAxis.ToDouble(end),
                StringFormat = "HH:mm",
                IntervalType = intervalType,
                MinorIntervalType = minorIntervalType,
                MajorStep = majorStepDays,
                MinorStep = majorStepDays / 2.0,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                AxislineColor = OxyColors.White,
                TextColor = OxyColors.White,
                TicklineColor = OxyColors.White
            };

            pm.Axes.Insert(0, timeAxis);
        }

        private void ApplyFixedXAxis(PlotModel pm)
        {
            if (pm.Axes.Count > 0)
            {
                var x = pm.Axes[0];
                x.Minimum = 0;
                x.Maximum = MaxPoints - 1;
            }
        }

        private void ResetAxes(PlotModel pm)
        {
            if (pm.Axes.Count > 1)
            {
                // Plot1/Plot2: fixed initial ranges; Plot3 uses auto (NaN) for Y
                if (pm == PlotValue1)
                {
                    pm.Axes[1].Minimum = 2000;
                    pm.Axes[1].Maximum = 3500;
                }
                else if (pm == PlotValue2)
                {
                    pm.Axes[1].Minimum = 1500;
                    pm.Axes[1].Maximum = 4000;
                }
                else if (pm == PlotValue3)
                {
                    // Auto range for Plot3
                    pm.Axes[1].Minimum = double.NaN;
                    pm.Axes[1].Maximum = double.NaN;

                    if (pm.Axes[1] is LinearAxis y3)
                    {
                        y3.MinimumPadding = 0.05;
                        y3.MaximumPadding = 0.05;
                        y3.AbsoluteMinimum = double.NaN;
                        y3.AbsoluteMaximum = double.NaN;
                    }
                }
                else
                {
                    pm.Axes[1].Minimum = 0;
                    pm.Axes[1].Maximum = 120;
                }
            }
        }

        // Lock Y axis range so OxyPlot will not autoscale on invalidate
        private void LockYAxis(PlotModel pm)
        {
            if (pm == null || pm.Axes.Count < 2) return;
            if (pm.Axes[1] is not LinearAxis yAxis) return;

            if (double.IsNaN(yAxis.Minimum) || double.IsNaN(yAxis.Maximum))
            {
                ResetAxes(pm);
            }

            yAxis.MinimumPadding = 0;
            yAxis.MaximumPadding = 0;

            var min = yAxis.Minimum;
            var max = yAxis.Maximum;

            // Constrain and apply the range
            yAxis.AbsoluteMinimum = min;
            yAxis.AbsoluteMaximum = max;
            yAxis.Zoom(min, max);
        }

        // Enable Y auto-scaling (clears fixed bounds) for a plot
        private void EnableAutoScaleYAxis(PlotModel pm, double minPad = 0.05, double maxPad = 0.05)
        {
            if (pm == null || pm.Axes.Count < 2) return;
            if (pm.Axes[1] is not LinearAxis y) return;

            y.Minimum = double.NaN;
            y.Maximum = double.NaN;
            y.AbsoluteMinimum = double.NaN;
            y.AbsoluteMaximum = double.NaN;
            y.MinimumPadding = minPad;
            y.MaximumPadding = maxPad;
        }

        /// <summary>
        /// Thêm dữ liệu (v1, v2) theo chế độ chasing.
        /// </summary>
        public void AddData(short v1, short v2, uint v3)
        {
            if (_count < MaxPoints)
            {
                _series1.Points.Add(new DataPoint(_writeIndex, v1));
                _series2.Points.Add(new DataPoint(_writeIndex, v2));
                _series3.Points.Add(new DataPoint(_writeIndex, v3));
                _count++;
            }
            else
            {
                _series1.Points[_writeIndex] = new DataPoint(_writeIndex, v1);
                _series2.Points[_writeIndex] = new DataPoint(_writeIndex, v2);
                _series3.Points[_writeIndex] = new DataPoint(_writeIndex, v3);
            }

            // Cập nhật cursor scatter
            UpdateCursorPoint(_cursorPoints1, _series1, v1);
            UpdateCursorPoint(_cursorPoints2, _series2, v2);
            UpdateCursorPoint(_cursorPoints3, _series3, v3);

            // Tăng writeIndex
            _writeIndex++;
            if (_writeIndex >= MaxPoints)
                _writeIndex = 0;

            // Ensure Plot3 Y is autoscaled (clear any fixed bounds)
            EnableAutoScaleYAxis(PlotValue3);

            // Coalesced invalidation
            RequestInvalidate(PlotValue1);
            RequestInvalidate(PlotValue2);
            RequestInvalidate(PlotValue3);

            LoadHistory();
        }

        public void LoadHistory()
        {
            // Cập nhật trục thời gian trước để lấy Min/Max hiện tại
            ConfigureTimeWindowAxis(PlotValue4);

            _historyHRPoints.Points.Clear();
            _historyBRPoints.Points.Clear();
            _historyT1Points.Points.Clear();
            _historyT2Points.Points.Clear();
            _historySpO2Points.Points.Clear();

            try
            {
                if (!File.Exists(LogFilePath))
                {
                    RequestInvalidate(PlotValue4);
                    return;
                }

                var lines = File.ReadAllLines(LogFilePath);
                if (lines.Length <= 1)
                {
                    RequestInvalidate(PlotValue4);
                    return;
                }

                // Lấy trục thời gian để lọc theo cửa sổ hiện tại
                var axis = PlotValue4.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom) as DateTimeAxis;
                double minX = axis?.Minimum ?? double.NegativeInfinity;
                double maxX = axis?.Maximum ?? double.PositiveInfinity;

                // Xác định cột theo header
                var headers = lines[0].Split(',', StringSplitOptions.TrimEntries);
                int idxTs = FindHeaderIndex(headers, "Timestamp");
                int idxHR = FindHeaderIndex(headers, "HeartRate", "HR");
                int idxBR = FindHeaderIndex(headers, "BreathRate", "BR", "Breath");
                int idxT1 = FindHeaderIndex(headers, "T1");
                int idxT2 = FindHeaderIndex(headers, "T2");
                int idxSpO2 = FindHeaderIndex(headers, "SpO2", "SPO2", "SpO_2");

                if (idxTs < 0)
                {
                    RequestInvalidate(PlotValue4);
                    return;
                }

                string[] tsFormats =
                {
                    "yyyy/MM/dd HH:mm:ss",
                    "yyyy-MM-dd HH:mm:ss",
                    "M/d/yyyy H:mm",
                    "M/d/yyyy HH:mm"
                };

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;

                    var parts = lines[i].Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length <= idxTs) continue;

                    if (!DateTime.TryParseExact(parts[idxTs], tsFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts))
                    {
                        if (!DateTime.TryParse(parts[idxTs], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts))
                            continue;
                    }
                    double x = DateTimeAxis.ToDouble(ts);
                    if (x < minX || x > maxX) continue;

                    if (idxHR >= 0 && idxHR < parts.Length && int.TryParse(parts[idxHR], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hr))
                        _historyHRPoints.Points.Add(new ScatterPoint(x, hr));

                    if (idxBR >= 0 && idxBR < parts.Length && int.TryParse(parts[idxBR], NumberStyles.Integer, CultureInfo.InvariantCulture, out var br))
                        _historyBRPoints.Points.Add(new ScatterPoint(x, br));

                    if (idxT1 >= 0 && idxT1 < parts.Length && double.TryParse(parts[idxT1], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t1))
                        _historyT1Points.Points.Add(new ScatterPoint(x, t1));

                    if (idxT2 >= 0 && idxT2 < parts.Length && double.TryParse(parts[idxT2], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t2))
                        _historyT2Points.Points.Add(new ScatterPoint(x, t2));

                    if (idxSpO2 >= 0 && idxSpO2 < parts.Length && double.TryParse(parts[idxSpO2], NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var spo2))
                        _historySpO2Points.Points.Add(new ScatterPoint(x, spo2));
                }
            }
            catch
            {
                // Ignore file/parse errors to avoid breaking UI
            }

            RequestInvalidate(PlotValue4);
            PlotValue4.InvalidatePlot(false);
        }

        // Auto-fit Y for Plot1/2 with padding; use defaults when no data
        private void FitPlot12YAxis(PlotModel pm, LineSeries series, double defaultMin, double defaultMax)
        {
            // Giữ nguyên để dùng nếu bật lại autoscale (hiện tại không dùng)
            if (pm.Axes.Count < 2) return;
            var yAxis = pm.Axes[1] as LinearAxis;
            if (yAxis == null) return;

            if (series.Points.Count == 0)
            {
                yAxis.Minimum = defaultMin;
                yAxis.Maximum = defaultMax;
                return;
            }

            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;

            foreach (var p in series.Points)
            {
                if (double.IsNaN(p.Y)) continue;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            if (!double.IsFinite(minY) || !double.IsFinite(maxY))
            {
                yAxis.Minimum = defaultMin;
                yAxis.Maximum = defaultMax;
                return;
            }

            double range = Math.Max(1e-6, maxY - minY);
            double pad = Math.Max(1, range * 0.1);
            yAxis.Minimum = minY - pad;
            yAxis.Maximum = maxY + pad;
        }

        private static int FindHeaderIndex(string[] headers, params string[] candidates)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(headers[i], c, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            for (int i = 0; i < headers.Length; i++)
            {
                foreach (var c in candidates)
                {
                    if (headers[i].Contains(c, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private void UpdateCursorPoint(ScatterSeries scatter, LineSeries series, double value)
        {
            if (!ShowCursorPoint)
                return;

            scatter.Points.Clear();
            scatter.Points.Add(new ScatterPoint(_writeIndex, value));
        }

        /// <summary>
        /// Xóa dữ liệu & reset cursor.
        /// </summary>
        public void Clear()
        {
            _series1.Points.Clear();
            _series2.Points.Clear();
            _series3.Points.Clear();

            _cursorPoints1.Points.Clear();
            _cursorPoints2.Points.Clear();
            _cursorPoints3.Points.Clear();

            _writeIndex = 0;
            _count = 0;

            ApplyFixedXAxis(PlotValue1);
            ApplyFixedXAxis(PlotValue2);
            ApplyFixedXAxis(PlotValue3);

            ResetAxes(PlotValue1);
            ResetAxes(PlotValue2);
            ResetAxes(PlotValue3);

            // Keep Plot3 Y auto after reset
            EnableAutoScaleYAxis(PlotValue3);

            RequestInvalidate(PlotValue1);
            RequestInvalidate(PlotValue2);
            RequestInvalidate(PlotValue3);
        }

        /// <summary>
        /// Áp dụng hiển thị / ẩn cursor (line + point) cho Plot1/2.
        /// </summary>
        public void ApplyCursorVisibility()
        {
            PlotValue1.Series.Remove(_cursorPoints1);
            PlotValue2.Series.Remove(_cursorPoints2);
            PlotValue3.Series.Remove(_cursorPoints3);

            if (ShowCursorPoint)
            {
                if (!PlotValue1.Series.Contains(_cursorPoints1))
                    PlotValue1.Series.Add(_cursorPoints1);
                if (!PlotValue2.Series.Contains(_cursorPoints2))
                    PlotValue2.Series.Add(_cursorPoints2);
                if (!PlotValue3.Series.Contains(_cursorPoints3))
                    PlotValue3.Series.Add(_cursorPoints3);
            }

            RequestInvalidate(PlotValue1);
            RequestInvalidate(PlotValue2);
            RequestInvalidate(PlotValue3);
        }

        /// <summary>
        /// Thêm/bớt các series lịch sử trên Plot3 theo cài đặt hiển thị.
        /// </summary>
        public void ApplyHistoryVisibility()
        {
            PlotValue4.Series.Remove(_historyHRPoints);
            PlotValue4.Series.Remove(_historyBRPoints);
            PlotValue4.Series.Remove(_historyT1Points);
            PlotValue4.Series.Remove(_historyT2Points);
            PlotValue4.Series.Remove(_historySpO2Points);

            if (ShowHeartRate && !PlotValue4.Series.Contains(_historyHRPoints))
                PlotValue4.Series.Add(_historyHRPoints);
            if (ShowBreathRate && !PlotValue4.Series.Contains(_historyBRPoints))
                PlotValue4.Series.Add(_historyBRPoints);
            if (ShowT1 && !PlotValue4.Series.Contains(_historyT1Points))
                PlotValue4.Series.Add(_historyT1Points);
            if (ShowT2 && !PlotValue4.Series.Contains(_historyT2Points))
                PlotValue4.Series.Add(_historyT2Points);
            if (ShowSpO2 && !PlotValue4.Series.Contains(_historySpO2Points))
                PlotValue4.Series.Add(_historySpO2Points);

            RequestInvalidate(PlotValue4);
            PlotValue4.InvalidatePlot(false);
        }

        public void ChangeCapacity(int newCapacity)
        {
            if (newCapacity <= 0) return;
            if (newCapacity == MaxPoints) return;

            MaxPoints = newCapacity;
            Clear();
        }

        public void SetTimeRangeHours(int hours)
        {
            _timeRangeHours = Math.Clamp(hours, 0, 24);
            LoadHistory();
        }

        private void RequestInvalidate(PlotModel pm)
        {
            if (pm == PlotValue1)
                _needsInvalidatePlot1 = true;
            else if (pm == PlotValue2)
                _needsInvalidatePlot2 = true;
            else if (pm == PlotValue3)
                _needsInvalidatePlot3 = true;
            else if (pm == PlotValue4)
                _needsInvalidatePlot4 = false;
        }

        // ==== Manual Y adjustments for Plot1/2 ====

        private static void EnsureMinRange(LinearAxis yAxis, double minRange = 10)
        {
            if (yAxis.Maximum - yAxis.Minimum < minRange)
            {
                yAxis.Maximum = yAxis.Minimum + minRange;
            }
        }

        private void AdjustYMax(PlotModel pm, double delta, bool increase)
        {
            if (pm.Axes.Count < 2) return;
            var yAxis = pm.Axes[1] as LinearAxis;
            if (yAxis == null) return;

            if (double.IsNaN(yAxis.Maximum) || double.IsNaN(yAxis.Minimum))
                ResetAxes(pm);
            if (increase)
                yAxis.Maximum += delta;
            else
                yAxis.Maximum -= delta;

            EnsureMinRange(yAxis);

            // Keep the adjusted range locked for Plot1/2; Plot3 stays auto
            if (pm != PlotValue3)
            {
                yAxis.AbsoluteMinimum = yAxis.Minimum;
                yAxis.AbsoluteMaximum = yAxis.Maximum;
            }

            RequestInvalidate(pm);
        }

        private void AdjustYMin(PlotModel pm, double delta, bool increase)
        {
            if (pm.Axes.Count < 2) return;
            var yAxis = pm.Axes[1] as LinearAxis;
            if (yAxis == null) return;

            if (double.IsNaN(yAxis.Maximum) || double.IsNaN(yAxis.Minimum))
                ResetAxes(pm);

            if (increase)
                yAxis.Minimum += delta;
            else
                yAxis.Minimum -= delta;

            EnsureMinRange(yAxis);

            // Keep the adjusted range locked for Plot1/2; Plot3 stays auto
            if (pm != PlotValue3)
            {
                yAxis.AbsoluteMinimum = yAxis.Minimum;
                yAxis.AbsoluteMaximum = yAxis.Maximum;
            }

            RequestInvalidate(pm);
        }

        public void IncreasePlot1YMax(double delta) => AdjustYMax(PlotValue1, delta, true);
        public void DecreasePlot1YMax(double delta) => AdjustYMax(PlotValue1, delta, false);
        public void IncreasePlot1YMin(double delta) => AdjustYMin(PlotValue1, delta, true);
        public void DecreasePlot1YMin(double delta) => AdjustYMin(PlotValue1, delta, false);

        public void IncreasePlot2YMax(double delta) => AdjustYMax(PlotValue2, delta, true);
        public void DecreasePlot2YMax(double delta) => AdjustYMax(PlotValue2, delta, false);
        public void IncreasePlot2YMin(double delta) => AdjustYMin(PlotValue2, delta, true);
        public void DecreasePlot2YMin(double delta) => AdjustYMin(PlotValue2, delta, false);

        public void IncreasePlot3YMax(double delta) => AdjustYMax(PlotValue3, delta, true);
        public void DecreasePlot3YMax(double delta) => AdjustYMax(PlotValue3, delta, false);
        public void IncreasePlot3YMin(double delta) => AdjustYMin(PlotValue3, delta, true);
        public void DecreasePlot3YMin(double delta) => AdjustYMin(PlotValue3, delta, false);
    }
}