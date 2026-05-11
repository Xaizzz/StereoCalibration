using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Media3D;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Дописывает JSONL строки в файл на весь срок работы приложения — не заменяя файл «поснимком».
    /// Потокобезопасно; полезная нагрузка сериализуется через Newtonsoft.
    /// </summary>
    public sealed class WoundDiagnosticsSessionRecorder : IWoundDiagnosticSink
    {
        private static readonly Lazy<WoundDiagnosticsSessionRecorder> Lazy =
            new Lazy<WoundDiagnosticsSessionRecorder>(() => new WoundDiagnosticsSessionRecorder());

        public static WoundDiagnosticsSessionRecorder Instance => Lazy.Value;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            FloatFormatHandling = FloatFormatHandling.String,
            Culture = CultureInfo.InvariantCulture,
            Formatting = Formatting.None
        };

        private readonly object _fileLock = new object();
        private string? _resolvedPath;
        private long _seq;
        private bool _sessionBannerWritten;

        private WoundDiagnosticsSessionRecorder()
        {
        }

        public void Append(string eventType, object? payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(eventType))
                    eventType = "unknown";

                var envelope = new
                {
                    t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    seq = Interlocked.Increment(ref _seq),
                    pid = Environment.ProcessId,
                    evt = eventType,
                    d = payload
                };

                var line = JsonConvert.SerializeObject(envelope, SerializerSettings) + Environment.NewLine;
                lock (_fileLock)
                {
                    AppendMainTraceLineUnlocked(line);
                    if (string.Equals(eventType, "capture_reference", StringComparison.OrdinalIgnoreCase))
                        AppendReferenceCaptureMetricsFileUnlocked(payload);
                }

                Trace.WriteLine("[WoundDiag] " + eventType);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WoundDiag] Append failed: {ex.Message}");
            }
        }

        /// <summary>Матрица 4×4 камера1→сцена, порядок M11…M44 WPF Matrix3D.</summary>
        public void LogCalibration(string source, Scene3DService scene3D)
        {
            if (scene3D == null || !scene3D.IsCalibrated)
                return;

            var m = scene3D.Camera1ToSceneMatrix;
            Append("scene_calibration", new
            {
                source,
                camera1Scene = new { x = scene3D.Camera1Position.X, y = scene3D.Camera1Position.Y, z = scene3D.Camera1Position.Z },
                camera2Scene = new { x = scene3D.Camera2Position.X, y = scene3D.Camera2Position.Y, z = scene3D.Camera2Position.Z },
                stereoCenterScene = new { x = scene3D.StereoCenter.X, y = scene3D.StereoCenter.Y, z = scene3D.StereoCenter.Z },
                camera1ToSceneRowMajor4x4 = new[]
                {
                    m.M11, m.M12, m.M13, m.M14,
                    m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34,
                    m.OffsetX, m.OffsetY, m.OffsetZ, m.M44
                }
            });
        }

        /// <summary>Согласование отображаемых маркеров с потоком в деформацию (сцена vs Camera1→Scene).</summary>
        public void LogViewportMarkerParity(
            Scene3DService scene3D,
            IReadOnlyDictionary<int, Point3D> markerPositionsScene,
            IReadOnlyDictionary<int, Point3D> markerCamera1Mm,
            IReadOnlyDictionary<int, Point3D> markerCamera1RawMm,
            IReadOnlyList<int>? activeWoundMarkerIds)
        {
            if (scene3D == null || !scene3D.IsCalibrated)
                return;

            double maxErr = 0;
            var worstId = -1;
            foreach (var kv in markerCamera1Mm)
            {
                if (!markerPositionsScene.TryGetValue(kv.Key, out var scenePt))
                    continue;

                var expected = scene3D.ConvertCamera1PointToScene(kv.Value);
                var err = DistanceMm(expected, scenePt);
                if (err > maxErr)
                {
                    maxErr = err;
                    worstId = kv.Key;
                }
            }

            var keysOnlyInRawThisFrameCount = markerCamera1RawMm.Keys.Count(id => !markerCamera1Mm.ContainsKey(id));
            var keysInMmMissingFromRawThisFrameCount =
                markerCamera1Mm.Keys.Count(id => !markerCamera1RawMm.ContainsKey(id));

            Append("viewport_marker_parity", new
            {
                markerSceneCount = markerPositionsScene.Count,
                camera1MmCount = markerCamera1Mm.Count,
                camera1RawCount = markerCamera1RawMm.Count,
                keysOnlyInRawThisFrameCount,
                keysInMmMissingFromRawThisFrameCount,
                maxSceneMismatchFromMmMm = maxErr,
                worstMismatchArucoId = worstId,
                activeWoundMarkerIdsCount = activeWoundMarkerIds?.Count ?? 0,
                hint = "Поток деформации должен использовать те же ключи что MarkerPositionsCamera1Mm (TTL + сглаживание как во вьюпорте)."
            });
        }

        private static double DistanceMm(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <remarks>Вызывать только под <see cref="_fileLock"/>.</remarks>
        private void AppendMainTraceLineUnlocked(string line)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = _resolvedPath ?? Path.Combine(baseDir, "wound_deformation_live_trace.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? baseDir);

            if (!_sessionBannerWritten)
            {
                var bannerObj = new
                {
                    t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    seq = 0L,
                    pid = Environment.ProcessId,
                    evt = "session_start",
                    d = new
                    {
                        file = Path.GetFileName(path),
                        fullPath = path,
                        companionMetricsPath = Path.Combine(baseDir, "reference_capture_metrics.jsonl"),
                        machine = Environment.MachineName,
                        dotnet = Environment.Version.ToString(),
                        product = nameof(StereoCalibration)
                    }
                };
                File.AppendAllText(
                    path,
                    JsonConvert.SerializeObject(bannerObj, SerializerSettings) + Environment.NewLine);
                _resolvedPath ??= path;
                _sessionBannerWritten = true;
            }

            File.AppendAllText(_resolvedPath!, line);
        }

        /// <summary>
        /// Ключевые метрики каждого CaptureReference без полного дампа события
        /// (удобно для отчётов и серий испытаний).
        /// </summary>
        /// <remarks>Вызывать только под <see cref="_fileLock"/>.</remarks>
        private void AppendReferenceCaptureMetricsFileUnlocked(object? payload)
        {
            if (payload == null)
                return;

            var jo = JObject.FromObject(payload);
            var slim = new JObject
            {
                ["t"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["evt"] = "reference_capture_metrics",
                ["pid"] = Environment.ProcessId,
                ["alignmentRmseMm"] = jo["alignmentRmseMm"],
                ["referenceBiasRmseMm"] = jo["referenceBiasRmseMm"],
                ["referenceResidualMaxMm"] = jo["referenceResidualMaxMm"],
                ["activeMarkerIds"] = jo["activeMarkerIds"]
            };

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(baseDir, "reference_capture_metrics.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? baseDir);
            File.AppendAllText(path, JsonConvert.SerializeObject(slim, SerializerSettings) + Environment.NewLine);
        }
    }
}
