using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace StereoCalibration.Services
{
    /// <summary>
    /// JSONL-лог стереотрекинга: старт захвата, периодические heartbeat-снимки агрегатов и итог по остановке.
    /// По умолчанию: BaseDirectory/stereo_tracking_session.jsonl.
    /// </summary>
    public sealed class StereoTrackingSessionRecorder : IStereoTrackingDiagSink
    {
        public static StereoTrackingSessionRecorder Instance { get; } = new StereoTrackingSessionRecorder();

        /// <summary>Период heartbeat (кадров). 0 или отрицательное — только старт и итог.</summary>
        public static volatile int HeartbeatEveryFrames = 120;

        private const int MedianReservoirCapacity = 8192;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            FloatFormatHandling = FloatFormatHandling.String,
            Culture = CultureInfo.InvariantCulture,
            Formatting = Formatting.None
        };

        private readonly object _gate = new object();
        private readonly Random _rng = new Random();
        private readonly List<double> _zReservoir = new List<double>();
        private readonly HashSet<int> _uniqueMarkersEverAccepted = new HashSet<int>();

        private string _sessionId = "";
        private string _resolvedPath = "";
        private bool _active;
        private DateTime _sessionUtcStart;
        private long _stereoFrames;
        private long _framesWithoutAcceptedTriangulation;
        private long _framesWithAnyIdAsymmetry;
        private double _sumUnpairedSlots;
        private long _sumBilateralOverlapDenom;
        private long _sumStaleRejectedPairs;
        private long _acceptedTriangulationSamples;
        private int _peakConcurrentAccepted;
        private long _valCoord;
        private long _valDepth;
        private long _valJump;
        private long _solveFailApproxSum;
        private double _sumZ;
        private double _sumZSq;
        private long _zSampleCountForMoments;
        private long _ordinalZInsertion;

        private StereoTrackingSessionRecorder()
        {
        }

        /// <inheritdoc />
        public void BeginSession()
        {
            try
            {
                lock (_gate)
                {
                    _sessionId = Guid.NewGuid().ToString("N");
                    _sessionUtcStart = DateTime.UtcNow;
                    _active = true;
                    _stereoFrames = 0;
                    _framesWithoutAcceptedTriangulation = 0;
                    _framesWithAnyIdAsymmetry = 0;
                    _sumUnpairedSlots = 0;
                    _sumBilateralOverlapDenom = 0;
                    _sumStaleRejectedPairs = 0;
                    _acceptedTriangulationSamples = 0;
                    _peakConcurrentAccepted = 0;
                    _valCoord = 0;
                    _valDepth = 0;
                    _valJump = 0;
                    _solveFailApproxSum = 0;
                    _sumZ = 0;
                    _sumZSq = 0;
                    _zSampleCountForMoments = 0;
                    _ordinalZInsertion = 0;
                    _zReservoir.Clear();
                    _uniqueMarkersEverAccepted.Clear();

                    _resolvedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stereo_tracking_session.jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(_resolvedPath)!);

                    AppendLine(new
                    {
                        evt = "stereo_tracking_session_start",
                        t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        sessionId = _sessionId,
                        pid = Environment.ProcessId,
                        file = Path.GetFileName(_resolvedPath),
                        fullPath = _resolvedPath
                    });
                }
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine("[StereoTracking] BeginSession failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void NotifyFrame(StereoTrackingFrameObservation o)
        {
            if (!_active)
                return;

            try
            {
                lock (_gate)
                {
                    if (!_active)
                        return;

                    _stereoFrames++;

                    if (o.UnpairedMarkerIdSlots > 0)
                    {
                        _framesWithAnyIdAsymmetry++;
                        _sumUnpairedSlots += o.UnpairedMarkerIdSlots;
                    }

                    _sumBilateralOverlapDenom += Math.Max(0, o.BilateralOverlapCount);
                    _sumStaleRejectedPairs += Math.Max(0, o.StereoPairsRejectedStale);

                    if (o.TriangulationAcceptedCount == 0)
                        _framesWithoutAcceptedTriangulation++;

                    _solveFailApproxSum += Math.Max(0, o.TriangulationSolveFailuresApprox);
                    _valCoord += Math.Max(0, o.ValidationRejectCoordinates);
                    _valDepth += Math.Max(0, o.ValidationRejectDepth);
                    _valJump += Math.Max(0, o.ValidationRejectJump);

                    if (o.TriangulationAcceptedCount > _peakConcurrentAccepted)
                        _peakConcurrentAccepted = o.TriangulationAcceptedCount;

                    _acceptedTriangulationSamples += Math.Max(0, o.TriangulationAcceptedCount);

                    if (o.AcceptedMarkerIds is { Length: > 0 } ids)
                    {
                        foreach (var id in ids)
                            _uniqueMarkersEverAccepted.Add(id);
                    }

                    if (o.AcceptedZMm is { Length: > 0 } zs)
                    {
                        foreach (var z in zs)
                        {
                            if (double.IsNaN(z) || double.IsInfinity(z))
                                continue;

                            _zSampleCountForMoments++;
                            _sumZ += z;
                            _sumZSq += z * z;

                            ReservoirConsiderZ(z);
                        }
                    }

                    var hb = HeartbeatEveryFrames;
                    if (hb > 0 && _stereoFrames % hb == 0)
                    {
                        AppendLine(new
                        {
                            evt = "stereo_tracking_heartbeat",
                            t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                            sessionId = _sessionId,
                            stereoFramesProcessed = _stereoFrames,
                            markersTriangulationAcceptedSamplesTotalSoFar = _acceptedTriangulationSamples,
                            stereoMeanZmmSoFar = _zSampleCountForMoments > 0 ? _sumZ / _zSampleCountForMoments : (double?)null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine("[StereoTracking] NotifyFrame failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void EndSession()
        {
            if (!_active)
                return;

            try
            {
                lock (_gate)
                {
                    if (!_active)
                        return;

                    _active = false;

                    double? meanZ = _zSampleCountForMoments > 0 ? _sumZ / _zSampleCountForMoments : null;
                    double? stdZ = null;
                    if (_zSampleCountForMoments > 1)
                    {
                        var n = _zSampleCountForMoments;
                        var variance = (_sumZSq - (_sumZ * _sumZ) / n) / n;
                        stdZ = Math.Sqrt(Math.Max(0, variance));
                    }

                    double? medianApprox = null;
                    double? minReservoir = null;
                    double? maxReservoir = null;
                    if (_zReservoir.Count > 0)
                    {
                        var sorted = _zReservoir.OrderBy(static x => x).ToArray();
                        var mid = sorted.Length / 2;
                        medianApprox = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) * 0.5;
                        minReservoir = sorted.First();
                        maxReservoir = sorted.Last();
                    }

                    var wall = DateTime.UtcNow - _sessionUtcStart;

                    AppendLine(new
                    {
                        evt = "stereo_tracking_session_summary",
                        t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        sessionId = _sessionId,
                        wallClockDurationSec = wall.TotalSeconds,

                        stereoFramesProcessed = _stereoFrames,
                        uniqueMarkerIdsAcceptedAtLeastOnce = _uniqueMarkersEverAccepted.Count,

                        markersTriangulationAcceptedSamplesTotal = _acceptedTriangulationSamples,

                        peakConcurrentMarkersAcceptedOneStereoFrame = _peakConcurrentAccepted,

                        fractions = new
                        {
                            stereoFramesWithoutAnyAcceptedTriangulation =
                                _stereoFrames > 0 ? _framesWithoutAcceptedTriangulation / (double)_stereoFrames : (double?)null,
                            stereoFramesAnyIdOnlyOneCameraPresence =
                                _stereoFrames > 0 ? _framesWithAnyIdAsymmetry / (double)_stereoFrames : (double?)null
                        },

                        idAsymmetrySurrogateMarkersPerFrameMean =
                            _stereoFrames > 0 ? _sumUnpairedSlots / _stereoFrames : (double?)null,

                        staleStereoPairRejectionsVsBilateralOverlapSum =
                            _sumBilateralOverlapDenom > 0
                                ? _sumStaleRejectedPairs / (double)_sumBilateralOverlapDenom
                                : (double?)null,

                        aggregates = new
                        {
                            sumBilateralOverlapCountPerFrameBaseline = _sumBilateralOverlapDenom,
                            staleRejectedStereoPairsTotal = _sumStaleRejectedPairs,
                            validationRejectedCoord = _valCoord,
                            validationRejectedDepth = _valDepth,
                            validationRejectedJump = _valJump,
                            triangulationGeometryFailuresApprox = _solveFailApproxSum,
                            zMomentSampleCountPerMarkerCornerCenters = _zSampleCountForMoments
                        },

                        statisticsZmmCamera1Approx = new
                        {
                            mean = meanZ,
                            stdPopulation = stdZ,
                            medianApproxReservoirCapacity = medianApprox,
                            minApproxReservoir = minReservoir,
                            maxApproxReservoir = maxReservoir
                        },

                        definitionNotesRu = new[]
                        {
                            "Длительность — wall-clock от BeginSession до EndSession (останов камер). Коррелирует с видеопотоком, если UI не тормозит.",
                            "Пропуск триангуляции кадра: stereoFramesWithoutAnyAcceptedTriangulation (ни одной принятой 3D-точки в кадре).",
                            "Stale: StereoCameraService память маркера; StereoPairsRejectedStale — отброс пары перед триангуляцией при stale на любой камере; отношение к сумме BilateralOverlapCount по кадрам — грубая доля синхронно видимых ID, утраченных именно через stale-слой.",
                            "Подмены ArUco ID не реконструируются; surrogate — доля кадров с «лишними» одиночными ID (только камера 1 или только камера 2) и среднее их число на кадр.",
                            "Median/min/max по Z — аппроксимация по случайному резерву больших трассировок для медианы при длинном видео."
                        }
                    });

                    AppendLine(new
                    {
                        evt = "stereo_tracking_session_end",
                        t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        sessionId = _sessionId
                    });
                }
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine("[StereoTracking] EndSession failed: " + ex.Message);
            }
        }

        private void ReservoirConsiderZ(double z)
        {
            _ordinalZInsertion++;
            if (_zReservoir.Count < MedianReservoirCapacity)
            {
                _zReservoir.Add(z);
                return;
            }

            var j = _rng.Next((int)Math.Min(int.MaxValue, _ordinalZInsertion));
            if (j < MedianReservoirCapacity)
                _zReservoir[j] = z;
        }

        private void AppendLine(object payload)
        {
            var line = JsonConvert.SerializeObject(payload, SerializerSettings) + Environment.NewLine;
            Directory.CreateDirectory(Path.GetDirectoryName(_resolvedPath)!);
            File.AppendAllText(_resolvedPath, line);
        }
    }
}
