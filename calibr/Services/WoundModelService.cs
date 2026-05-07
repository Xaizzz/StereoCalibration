using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Оркестрирует загрузку, первичное выравнивание и RBF-деформацию модели раны.
    /// Сервис не управляет WPF viewport напрямую, а отдаёт готовую MeshGeometry3D.
    /// </summary>
    public sealed class WoundModelService
    {
        private const int UpdateIntervalMs = 100;
        /// <summary>Сглаживание входных позиций активных маркеров перед rigid/RBF (camera1 мм).</summary>
        private const double DeformationMarkerSmoothAlpha = 0.38;
        private const double MarkerUpdateThresholdHighMm = 0.68;
        private const double MarkerUpdateThresholdLowMm = 0.42;
        private const int MinActiveMarkersHighMotion = 2;
        private const double MaxReasonableVertexAbsMm = 50000.0;
        private const double MarkerFitCorrectionThresholdMm = 8.0;
        private const double NonRigidDeformationBlend = 0.44;
        private const double NonRigidNormalBlend = 0.20;
        private const double NonRigidResidualDeadZoneMm = 0.22;
        private const double MaxTangentialResidualMm = 34.0;
        private const double MaxNormalResidualMm = 10.0;
        private const double MaxResidualOutlierMm = 42.0;
        private const double MaxResidualHardOutlierMm = 70.0;
        private const double MaxRigidScaleChange = 0.16;
        private const double MaxRigidScaleHardReject = 0.45;
        private const double MaxRigidRmseMm = 35.0;
        private const double MinSurfaceNormalDot = 0.45;
        private const double MaxVertexResidualNearMarkersMm = 18.0;
        private const double MaxVertexResidualFarMm = 4.0;
        private const double VertexResidualDecayDistanceMm = 140.0;
        private const double MaxResidualStepPerFrameMm = 7.0;
        private const double ResidualTemporalBlend = 0.78;
        private const double RigidInlierThresholdMm = 34.0;
        /// <summary>
        /// Внутренние (ближе к центру раны) маркеры не должны помечаться как rigid-outlier из‑за изгиба,
        /// который периферийный similarity намеренно не объясняет.
        /// </summary>
        private const double RigidInlierInnerMarkerThresholdMm = 95.0;
        private const double CenterResidualFactor = 0.55;
        private const double CenterEdgeProfilePower = 1.5;
        private const double MinMarkerRadiusForProfileMm = 40.0;
        /// <summary>
        /// Для маркеров ближе к центру кольца увеличиваем допустимый остаточный сдвиг — на них ложится изгиб.
        /// </summary>
        private const double InnerMarkerNonRigidBoost = 0.5;
        private const double CenterVertexNormalStiffnessMin = 0.045;
        private const double WoundNormalStiffnessPower = 2.15;
        private const double MarkerBiasCompensation = 0.25;
        private const double MinRigidScaleCandidate = 0.60;
        private const double MaxRigidScaleCandidate = 1.40;
        private const double RigidScalePenaltyWeight = 12.0;
        /// <summary>«Мягкие» границы произведения candidate×similarity.Scale при захвате опоры.</summary>
        private const double CaptureCombinedScalePreferLo = 0.22;
        private const double CaptureCombinedScalePreferHi = 5.0;
        private const double CaptureCombinedScalePenaltyWeight = 6.0;
        private const int StableFramesForReferenceCapture = 12;
        private static readonly double[] AutoScaleCandidates =
        {
            0.001, 0.01, 0.1, 1.0, 10.0, 100.0, 1000.0
        };

        private readonly WoundModelLoaderService _loader = new WoundModelLoaderService();
        private readonly RbfDeformationService _rbf = new RbfDeformationService();
        private readonly Dictionary<int, Point3D> _lastUpdateMarkerSnapshot = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, double> _lastMarkerFitByIdMm = new Dictionary<int, double>();
        private readonly Dictionary<int, Point3D> _lastPredictedMarkerPositionsCamera1 = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, Point3D> _lastObservedMarkerPositionsCamera1 = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, Point3D> _deformationInputSmoothedCamera1 = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, Point3D> _motionCompareSnapshotCamera1 = new Dictionary<int, Point3D>();

        /// <summary>Центр стереопары в СК камеры 1 для ориентации полости после сброса опоры.</summary>
        private Point3D _stereoMidpointCamera1 = new Point3D(double.NaN, double.NaN, double.NaN);

        /// <summary>После ручного сброса опоры выполнить одно отражение геометрии перед выравниванием при захвате.</summary>
        private bool _orientCavityTowardStereoCenterNextCapture;

        private bool _deformationMotionGateOpen;

        private WoundModelData? _model;
        private MeshGeometry3D? _mesh;
        private int[] _activeMarkerIds = Array.Empty<int>();
        private Point3D[] _referenceVerticesAligned = Array.Empty<Point3D>();
        private Point3D[] _referenceObservedControlPoints = Array.Empty<Point3D>();
        private Point3D[] _referenceControlPointsAligned = Array.Empty<Point3D>();
        private Vector3D[] _referenceMarkerBiasVectors = Array.Empty<Vector3D>();
        private double _referenceMarkerBiasRmseMm;
        private Point3D _referenceMarkerCentroidAligned = new Point3D(0, 0, 0);
        private double _referenceMarkerMaxRadiusMm = MinMarkerRadiusForProfileMm;
        private Vector3D _referenceSurfaceNormalCamera1 = new Vector3D(0, 0, 1);
        private Point3D[] _lastValidVertices = Array.Empty<Point3D>();
        private Point3D[] _lastRigidVertices = Array.Empty<Point3D>();
        private WoundPoseState _poseState = WoundPoseState.Empty;
        private DateTime _lastUpdateAtUtc = DateTime.MinValue;
        private bool _referenceCaptured;
        private string _pendingBindingSignature = "";
        private int _pendingStableFrameCount;
        private DateTime _lastReferenceCaptureAtUtc = DateTime.MinValue;
        private double _activeModelScaleMultiplier = 1.0;
        private string _activeModelScaleMode = "auto: ожидание привязки";
        private double _lastCaptureCombinedScale = 1.0;
        private Vector3D _lastCaptureTranslationMm = new Vector3D(0, 0, 0);
        private DateTime _lastDiagDeformationThrottleUtc = DateTime.MinValue;
        private DateTime _lastDiagMotionSkipThrottleUtc = DateTime.MinValue;
        private DateTime _lastDiagMeshFrozenThrottleUtc = DateTime.MinValue;

        public bool HasModel => _model != null;
        public bool HasMesh => _mesh != null;
        public MeshGeometry3D? Mesh => _mesh;
        public string Status { get; private set; } = "Модель раны не загружена.";
        public string LastReferenceReason { get; private set; } = "Ожидание маркеров модели.";
        public int VertexCount => _model?.VertexCount ?? 0;
        public int TriangleCount => _model?.TriangleCount ?? 0;
        public int ModelMarkerCount => _model?.ModelMarkerCentersMm.Count ?? 0;
        public int LinkedMarkerCount => _model?.MarkerBindingsByCameraId.Count ?? 0;
        public int ActiveMarkerCount => _activeMarkerIds.Length;
        public IReadOnlyList<int> ActiveDeformationMarkerIds => _activeMarkerIds;
        public double ActiveModelScaleMultiplier => _activeModelScaleMultiplier;
        public string ActiveModelScaleMode => _activeModelScaleMode;
        public string? ActiveTexturePath => _model?.DiffuseTexturePath;
        public IReadOnlyList<string?> TriangleMaterialNames => _model?.TriangleMaterialNames ?? Array.Empty<string?>();
        public IReadOnlyDictionary<string, string> MaterialTexturePaths => _model?.MaterialTexturePaths ?? new Dictionary<string, string>();
        public double LastUpdateDurationMs { get; private set; }
        public double LastAlignmentRmseMm { get; private set; }
        public double LastMarkerFitRmseMm { get; private set; }
        public double LastMarkerFitMaxMm { get; private set; }
        public int LastMarkerFitWorstMarkerId { get; private set; } = -1;
        public int LastVisibleActiveMarkerCount { get; private set; }
        public int LastFallbackMarkerCount { get; private set; }
        public bool LastGlobalCorrectionApplied { get; private set; }
        public double LastGlobalCorrectionScale { get; private set; } = 1.0;
        public double LastGlobalCorrectionTranslationNormMm { get; private set; }
        public double LastCaptureCombinedScale => _lastCaptureCombinedScale;
        public Vector3D LastCaptureTranslationMm => _lastCaptureTranslationMm;
        public double LastReferenceMarkerBiasRmseMm => _referenceMarkerBiasRmseMm;
        public double LastRigidRmseMm => _poseState.RigidRmseMm;
        public double LastResidualMaxMm => _poseState.ResidualMaxMm;
        public double LastResidualP95Mm => _poseState.ResidualP95Mm;
        public int LastOutlierMarkerCount => _poseState.OutlierMarkerIds.Count;
        public string LastFreezeReason => _poseState.FreezeReason;
        public bool LastFrameFrozen => _poseState.FrameFrozen;
        public Vector3D LastSurfaceNormalCamera1 => _poseState.CurrentSurfaceNormalCamera1;
        public IReadOnlyDictionary<int, double> LastMarkerFitByIdMm => _lastMarkerFitByIdMm;
        public IReadOnlyDictionary<int, Point3D> LastPredictedMarkerPositionsCamera1 => _lastPredictedMarkerPositionsCamera1;
        public IReadOnlyDictionary<int, Point3D> LastObservedMarkerPositionsCamera1 => _lastObservedMarkerPositionsCamera1;
        public string? LoadedFileName => _model == null ? null : Path.GetFileName(_model.SourcePath);

        /// <summary>Необязательный приёмник событий для JSONL-журнала по сеансу.</summary>
        public IWoundDiagnosticSink? DiagnosticSink { get; set; }

        private void Diag(string evt, object? payload = null)
        {
            try
            {
                DiagnosticSink?.Append(evt, payload);
            }
            catch
            {
                /* журнал не должен ломать деформацию */
            }
        }

        private bool DiagThrottle(ref DateTime lastUtc, int minGapMs)
        {
            var now = DateTime.UtcNow;
            if ((now - lastUtc).TotalMilliseconds < minGapMs)
                return false;
            lastUtc = now;
            return true;
        }

        /// <summary>
        /// Задаёт положение «центра стереопары» в миллиметрах камеры 1 для ориентации полости после сброса опоры.
        /// Вызывается каждый кадр перед <see cref="TryUpdate"/>.
        /// </summary>
        public void SetStereoLookTargetCamera1(Point3D stereoMidCamera1Mm)
        {
            _stereoMidpointCamera1 = stereoMidCamera1Mm;
        }

        public bool TryGetReferenceMeshStats(out Point3D center, out Vector3D size, out int count)
        {
            center = default;
            size = default;
            count = 0;
            if (_model == null || _model.ReferenceVerticesMm.Count == 0)
                return false;

            CalculatePointCloudStats(_model.ReferenceVerticesMm, out center, out size, out count);
            return true;
        }

        public bool TryGetModelMarkerStats(out Point3D center, out Vector3D size, out int count)
        {
            center = default;
            size = default;
            count = 0;
            if (_model == null || _model.ModelMarkerCentersMm.Count == 0)
                return false;

            CalculatePointCloudStats(_model.ModelMarkerCentersMm.Values, out center, out size, out count);
            return true;
        }

        /// <summary>
        /// Текущее соответствие имён объектов маркеров в OBJ → ID ArUco (null — не задано).
        /// </summary>
        public Dictionary<string, int?> GetMarkerBindingMapSnapshot()
        {
            if (_model == null)
                return new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

            var nameByCameraId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in _model.MarkerBindingsByCameraId.Values)
                nameByCameraId[binding.ModelMarkerName] = binding.CameraMarkerId;

            var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _model.ModelMarkerCentersMm.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                result[name] = nameByCameraId.TryGetValue(name, out var id) ? id : null;

            return result;
        }

        public Dictionary<string, int?> BuildAutoMarkerBindingMap(
            IReadOnlyDictionary<int, Point3D> currentMarkers,
            out double bestRmseMm)
        {
            bestRmseMm = double.PositiveInfinity;
            if (_model == null)
                throw new InvalidOperationException("Сначала загрузите OBJ-модель раны.");

            if (currentMarkers == null || currentMarkers.Count < 3)
                throw new InvalidOperationException("Для автопривязки нужно минимум 3 видимых ArUco-маркера.");

            var modelMarkers = _model.ModelMarkerCentersMm
                .OrderBy(marker => marker.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modelMarkers.Length < 3)
                throw new InvalidOperationException("OBJ-модель должна содержать минимум 3 marker-объекта.");

            var cameraMarkers = currentMarkers
                .OrderBy(marker => marker.Key)
                .Select(marker => new KeyValuePair<int, Point3D>(marker.Key, marker.Value))
                .ToArray();
            var assignmentCount = Math.Min(modelMarkers.Length, cameraMarkers.Length);
            if (assignmentCount < 3)
                throw new InvalidOperationException("Для автопривязки нужно минимум 3 пары marker-объектов и ArUco.");

            var modelPoints = modelMarkers.Select(marker => marker.Value).ToArray();
            var modelIndexCombinations = new List<int[]>();
            BuildCombinations(
                start: 0,
                depth: 0,
                targetCount: assignmentCount,
                current: new int[assignmentCount],
                result: modelIndexCombinations,
                sourceCount: modelMarkers.Length);
            var assignedModelIndices = Enumerable.Repeat(-1, assignmentCount).ToArray();
            var assignedCameraIndices = Enumerable.Repeat(-1, assignmentCount).ToArray();
            var used = new bool[cameraMarkers.Length];
            var bestAssignment = Array.Empty<int>();
            var bestModelAssignment = Array.Empty<int>();
            var modelDistances = BuildNormalizedDistanceMatrix(modelPoints);
            var cameraDistances = BuildNormalizedDistanceMatrix(cameraMarkers.Select(marker => marker.Value).ToArray());
            var bestRmseLocal = double.PositiveInfinity;
            var bestPairCost = double.PositiveInfinity;

            foreach (var modelCombination in modelIndexCombinations)
            {
                Array.Copy(modelCombination, assignedModelIndices, assignmentCount);
                SearchCameraAssignment(modelCombination, depth: 0, pairCost: 0.0);
            }

            if (bestAssignment.Length != assignmentCount || bestModelAssignment.Length != assignmentCount)
                throw new InvalidOperationException("Не удалось подобрать соответствия marker-объектов и ArUco.");

            bestRmseMm = bestRmseLocal;
            var result = GetMarkerBindingMapSnapshot();
            foreach (var modelMarker in modelMarkers)
            {
                result[modelMarker.Key] = null;
            }

            for (var i = 0; i < assignmentCount; i++)
            {
                result[modelMarkers[bestModelAssignment[i]].Key] = cameraMarkers[bestAssignment[i]].Key;
            }

            return result;

            void SearchCameraAssignment(IReadOnlyList<int> modelCombination, int depth, double pairCost)
            {
                if (depth == assignmentCount)
                {
                    var selectedModelPoints = modelCombination
                        .Select(index => modelMarkers[index].Value)
                        .ToArray();
                    var currentPoints = assignedCameraIndices
                        .Select(index => cameraMarkers[index].Value)
                        .ToArray();

                    double rmse;
                    try
                    {
                        var transform = SimilarityTransform.Estimate(selectedModelPoints, currentPoints);
                        var aligned = selectedModelPoints.Select(transform.Transform).ToArray();
                        rmse = CalculateRmse(aligned, currentPoints);
                    }
                    catch
                    {
                        return;
                    }

                    if (rmse + 1e-9 < bestRmseLocal ||
                        (Math.Abs(rmse - bestRmseLocal) <= 1e-9 && pairCost < bestPairCost))
                    {
                        bestRmseLocal = rmse;
                        bestPairCost = pairCost;
                        bestAssignment = assignedCameraIndices.ToArray();
                        bestModelAssignment = modelCombination.ToArray();
                    }

                    return;
                }

                var modelIndex = modelCombination[depth];
                for (var cameraIndex = 0; cameraIndex < cameraMarkers.Length; cameraIndex++)
                {
                    if (used[cameraIndex])
                        continue;

                    var nextPairCost = pairCost;
                    for (var previous = 0; previous < depth; previous++)
                    {
                        var previousModelIndex = modelCombination[previous];
                        var previousCameraIndex = assignedCameraIndices[previous];
                        if (previousCameraIndex < 0)
                            continue;

                        var diff = modelDistances[modelIndex, previousModelIndex] - cameraDistances[cameraIndex, previousCameraIndex];
                        nextPairCost += diff * diff;
                    }

                    used[cameraIndex] = true;
                    assignedCameraIndices[depth] = cameraIndex;
                    SearchCameraAssignment(modelCombination, depth + 1, nextPairCost);
                    assignedCameraIndices[depth] = -1;
                    used[cameraIndex] = false;
                }
            }
        }

        private static void BuildCombinations(
            int start,
            int depth,
            int targetCount,
            int[] current,
            List<int[]> result,
            int sourceCount)
        {
            if (depth == targetCount)
            {
                result.Add(current.ToArray());
                return;
            }

            var remaining = targetCount - depth;
            for (var index = start; index <= sourceCount - remaining; index++)
            {
                current[depth] = index;
                BuildCombinations(index + 1, depth + 1, targetCount, current, result, sourceCount);
            }
        }

        private static double[,] BuildNormalizedDistanceMatrix(IReadOnlyList<Point3D> points)
        {
            var matrix = new double[points.Count, points.Count];
            var distances = new List<double>(points.Count * points.Count);
            for (var row = 0; row < points.Count; row++)
            {
                for (var col = row + 1; col < points.Count; col++)
                {
                    var distance = Distance(points[row], points[col]);
                    matrix[row, col] = distance;
                    matrix[col, row] = distance;
                    if (distance > 1e-9)
                        distances.Add(distance);
                }
            }

            var scale = distances.Count == 0
                ? 1.0
                : distances.Average();
            if (scale <= 1e-9 || double.IsNaN(scale) || double.IsInfinity(scale))
                scale = 1.0;

            for (var row = 0; row < points.Count; row++)
            {
                for (var col = 0; col < points.Count; col++)
                {
                    matrix[row, col] /= scale;
                }
            }

            return matrix;
        }

        /// <summary>
        /// Применяет новую таблицу соответствий без перезагрузки OBJ; сбрасывает выравнивание/RBF до следующего кадра с маркерами.
        /// </summary>
        public WoundModelLoadResult ApplyMarkerBindings(IReadOnlyDictionary<string, int?> modelToCameraMarkerIds)
        {
            if (_model == null || _mesh == null)
                throw new InvalidOperationException("Сначала загрузите OBJ-модель раны.");

            _model = _loader.WithUpdatedMarkerBindings(_model, modelToCameraMarkerIds);
            _referenceCaptured = false;
            _activeMarkerIds = Array.Empty<int>();
            _referenceVerticesAligned = Array.Empty<Point3D>();
            _referenceObservedControlPoints = Array.Empty<Point3D>();
            _referenceControlPointsAligned = Array.Empty<Point3D>();
            _referenceMarkerBiasVectors = Array.Empty<Vector3D>();
            _referenceMarkerBiasRmseMm = 0;
            _referenceMarkerCentroidAligned = new Point3D(0, 0, 0);
            _referenceMarkerMaxRadiusMm = MinMarkerRadiusForProfileMm;
            _lastValidVertices = Array.Empty<Point3D>();
            _lastRigidVertices = Array.Empty<Point3D>();
            _poseState = WoundPoseState.Empty;
            _lastUpdateMarkerSnapshot.Clear();
            ClearCaptureStability();
            LastUpdateDurationMs = 0;
            LastAlignmentRmseMm = 0;
            _lastReferenceCaptureAtUtc = DateTime.MinValue;
            _activeModelScaleMultiplier = 1.0;
            _activeModelScaleMode = "auto: ожидание привязки";
            LastReferenceReason = "Опора сброшена после изменения соответствий.";
            ResetMarkerFitDiagnostics();
            ClearLastCaptureDiagnostics();
            ResetDeformationInputSmoothingState();

            _mesh.Positions = new Point3DCollection(_model.ReferenceVerticesMm);
            _mesh.TriangleIndices = new Int32Collection(_model.TriangleIndices);
            _mesh.TextureCoordinates = new PointCollection(_model.TextureCoordinates);

            Status = BuildLoadedStatus(_model);
            return new WoundModelLoadResult(
                _model.SourcePath,
                _model.SidecarPath,
                _model.VertexCount,
                _model.TriangleCount,
                _model.ModelMarkerCentersMm.Count,
                _model.MarkerBindingsByCameraId.Count,
                _model.UnmappedModelMarkers);
        }

        /// <summary>
        /// Записывает текущие соответствия в файл <c>.markers.json</c> рядом с OBJ.
        /// </summary>
        public void SaveMarkerBindingsToSidecar(IReadOnlyDictionary<string, int?> modelToCameraMarkerIds)
        {
            if (_model == null)
                throw new InvalidOperationException("Модель раны не загружена.");

            _loader.SaveMarkerSidecar(_model.SidecarPath, modelToCameraMarkerIds, unitsOverride: null);
        }

        /// <summary>
        /// Сбрасывает опору деформации: меш в исходную сетку, ожидание нового стабильного набора маркеров.
        /// </summary>
        public void ResetDeformationReference()
        {
            if (_model == null || _mesh == null)
                return;

            ResetDeformationInputSmoothingState();
            _orientCavityTowardStereoCenterNextCapture = true;
            _referenceCaptured = false;
            _activeMarkerIds = Array.Empty<int>();
            _referenceVerticesAligned = Array.Empty<Point3D>();
            _referenceObservedControlPoints = Array.Empty<Point3D>();
            _referenceControlPointsAligned = Array.Empty<Point3D>();
            _referenceMarkerBiasVectors = Array.Empty<Vector3D>();
            _referenceMarkerBiasRmseMm = 0;
            _referenceMarkerCentroidAligned = new Point3D(0, 0, 0);
            _referenceMarkerMaxRadiusMm = MinMarkerRadiusForProfileMm;
            _lastValidVertices = Array.Empty<Point3D>();
            _lastRigidVertices = Array.Empty<Point3D>();
            _poseState = WoundPoseState.Empty;
            _lastUpdateMarkerSnapshot.Clear();
            ClearCaptureStability();
            LastUpdateDurationMs = 0;
            LastAlignmentRmseMm = 0;
            _lastReferenceCaptureAtUtc = DateTime.MinValue;
            _activeModelScaleMultiplier = 1.0;
            _activeModelScaleMode = "auto: ожидание привязки";
            LastReferenceReason = "Опора деформации сброшена вручную.";
            ResetMarkerFitDiagnostics();
            ClearLastCaptureDiagnostics();

            _mesh.Positions = new Point3DCollection(_model.ReferenceVerticesMm);
            _mesh.TriangleIndices = new Int32Collection(_model.TriangleIndices);
            _mesh.TextureCoordinates = new PointCollection(_model.TextureCoordinates);
            Status = "Опора деформации сброшена. Держите набор маркеров стабильным для новой привязки.";
            Diag("reference_reset_manual", new { meshVertices = _model.ReferenceVerticesMm.Count });
        }

        public WoundModelLoadResult Load(string objPath)
        {
            _model = _loader.Load(objPath);
            _referenceCaptured = false;
            _activeMarkerIds = Array.Empty<int>();
            _referenceVerticesAligned = Array.Empty<Point3D>();
            _referenceObservedControlPoints = Array.Empty<Point3D>();
            _referenceControlPointsAligned = Array.Empty<Point3D>();
            _referenceMarkerBiasVectors = Array.Empty<Vector3D>();
            _referenceMarkerBiasRmseMm = 0;
            _referenceMarkerCentroidAligned = new Point3D(0, 0, 0);
            _referenceMarkerMaxRadiusMm = MinMarkerRadiusForProfileMm;
            _lastValidVertices = Array.Empty<Point3D>();
            _lastRigidVertices = Array.Empty<Point3D>();
            _poseState = WoundPoseState.Empty;
            _lastUpdateMarkerSnapshot.Clear();
            ClearCaptureStability();
            LastUpdateDurationMs = 0;
            LastAlignmentRmseMm = 0;
            _lastReferenceCaptureAtUtc = DateTime.MinValue;
            _activeModelScaleMultiplier = 1.0;
            _activeModelScaleMode = "auto: ожидание привязки";
            LastReferenceReason = "Ожидание стабильного набора маркеров.";
            ResetMarkerFitDiagnostics();
            ClearLastCaptureDiagnostics();
            ResetDeformationInputSmoothingState();
            _orientCavityTowardStereoCenterNextCapture = false;

            _mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(_model.ReferenceVerticesMm),
                TriangleIndices = new Int32Collection(_model.TriangleIndices),
                TextureCoordinates = new PointCollection(_model.TextureCoordinates)
            };

            Status = BuildLoadedStatus(_model);
            var loaded = new WoundModelLoadResult(
                _model.SourcePath,
                _model.SidecarPath,
                _model.VertexCount,
                _model.TriangleCount,
                _model.ModelMarkerCentersMm.Count,
                _model.MarkerBindingsByCameraId.Count,
                _model.UnmappedModelMarkers);
            Diag("model_loaded", new
            {
                loaded.SourcePath,
                loaded.SidecarPath,
                loaded.VertexCount,
                loaded.TriangleCount,
                loaded.ModelMarkerCount,
                loaded.LinkedMarkerCount,
                unmapped = loaded.UnmappedModelMarkers
            });
            return loaded;
        }

        public bool TryUpdate(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (_model == null || _mesh == null)
                return false;

            if (!_model.HasUsableMarkerBindings)
            {
                Status = "Заполните минимум 3 соответствия marker1.XXX -> ArUco ID в sidecar JSON.";
                LastReferenceReason = "Недостаточно связей model-marker -> ArUco.";
                return false;
            }

            var visibleBindings = GetVisibleBindings(currentMarkers);
            if (visibleBindings.Count < 3)
            {
                ClearCaptureStability();
                Status = $"Ожидание маркеров модели: видно {visibleBindings.Count}/3+ связанных ArUco.";
                LastReferenceReason = "Недостаточно видимых связанных маркеров.";
                LastVisibleActiveMarkerCount = visibleBindings.Count;
                LastFallbackMarkerCount = 0;
                return false;
            }

            if (!_referenceCaptured)
            {
                FeedCaptureStability(visibleBindings);
                if (_pendingStableFrameCount < StableFramesForReferenceCapture)
                {
                    Status =
                        $"Стабилизация набора для привязки: {_pendingStableFrameCount}/{StableFramesForReferenceCapture}";
                    LastReferenceReason = "Стабилизация перед первым захватом опоры.";
                    LastVisibleActiveMarkerCount = visibleBindings.Count;
                    LastFallbackMarkerCount = 0;
                    return false;
                }

                CaptureReference(visibleBindings, currentMarkers);
                return true;
            }

            var markersForDynamics = BuildSmoothedMarkersForDeformation(currentMarkers);

            if (!TryBuildCurrentControlPoints(
                    markersForDynamics,
                    out var currentControlPoints,
                    out var visibleMask,
                    out var visibleActiveCount,
                    out var fallbackCount))
            {
                Status = "Ожидание активных маркеров опорного набора: нужно минимум 3.";
                LastReferenceReason = "Слишком мало активных маркеров из опорного набора.";
                LastVisibleActiveMarkerCount = 0;
                LastFallbackMarkerCount = 0;
                LastGlobalCorrectionApplied = false;
                return false;
            }

            if (!ShouldUpdateDeformationMotion(markersForDynamics))
            {
                LastReferenceReason = "Изменения маркеров ниже порога обновления.";
                if (DiagThrottle(ref _lastDiagMotionSkipThrottleUtc, 350))
                {
                    Diag("deformation_skipped_motion_gate", new
                    {
                        gateOpen = _deformationMotionGateOpen,
                        markerInputs = markersForDynamics.Count,
                        activeMarkerCount = _activeMarkerIds.Length
                    });
                }

                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            LastVisibleActiveMarkerCount = visibleActiveCount;
            LastFallbackMarkerCount = fallbackCount;
            if (!TryBuildPhysicallyFilteredControlPoints(
                    currentControlPoints,
                    visibleMask,
                    out var physicallyFilteredControlPoints,
                    out var poseState,
                    out var globalMotion))
            {
                FreezeMesh(poseState);
                return false;
            }

            var rawDeformedVertices = _rbf.Apply(physicallyFilteredControlPoints);
            if (rawDeformedVertices.Length == 0 || !AreVerticesValid(rawDeformedVertices))
            {
                FreezeMesh(poseState with { FrameFrozen = true, FreezeReason = "RBF-деформация дала невалидную геометрию." });
                return false;
            }

            var rigidVertices = _referenceVerticesAligned
                .Select(globalMotion.Transform)
                .ToArray();
            var deformedVertices = ApplyPhysicalVertexConstraints(
                rigidVertices,
                rawDeformedVertices,
                physicallyFilteredControlPoints,
                globalMotion);
            if (deformedVertices.Length == 0 || !AreVerticesValid(deformedVertices))
            {
                FreezeMesh(poseState with { FrameFrozen = true, FreezeReason = "Физические ограничения дали невалидную геометрию." });
                return false;
            }

            var predictedControlPoints = _rbf.ApplyToPoints(_referenceControlPointsAligned, physicallyFilteredControlPoints);
            LastGlobalCorrectionApplied = false;
            LastGlobalCorrectionScale = 1.0;
            LastGlobalCorrectionTranslationNormMm = 0;

            _mesh.Positions = new Point3DCollection(deformedVertices);
            _lastValidVertices = deformedVertices.ToArray();
            _lastRigidVertices = rigidVertices.ToArray();
            _poseState = poseState;
            UpdateMarkerSnapshot(BuildSnapshotControlPoints(
                currentControlPoints,
                physicallyFilteredControlPoints,
                visibleMask,
                poseState.OutlierMarkerIds));
            UpdateMarkerFitDiagnostics(predictedControlPoints, currentMarkers, fallbackCount);

            RecordDeformationMotionBaseline(markersForDynamics);

            if (DiagThrottle(ref _lastDiagDeformationThrottleUtc, 430))
            {
                var rigidBow = BowMetrics(
                    rigidVertices,
                    physicallyFilteredControlPoints,
                    globalMotion,
                    _referenceSurfaceNormalCamera1);
                var deformedBow = BowMetrics(
                    deformedVertices,
                    physicallyFilteredControlPoints,
                    globalMotion,
                    _referenceSurfaceNormalCamera1);
                Diag("deformation_tick", new
                {
                    rigidRmseMm = poseState.RigidRmseMm,
                    residualMaxMm = poseState.ResidualMaxMm,
                    fitRmseMm = LastMarkerFitRmseMm,
                    fitMaxMm = LastMarkerFitMaxMm,
                    nonRigidBlend = NonRigidDeformationBlend,
                    outlierMarkers = poseState.OutlierMarkerIds.Count,
                    frameFrozen = poseState.FrameFrozen,
                    rigidMeshVsControlAlongNormalMm = rigidBow.AlongNormalMm,
                    rigidVertexPlaneRmsMm = rigidBow.VertexPlaneRmsMm,
                    rigidControlsPlaneRmsMm = rigidBow.ControlPlaneRmsMm,
                    deformedMeshVsControlAlongNormalMm = deformedBow.AlongNormalMm,
                    deformedVertexPlaneRmsMm = deformedBow.VertexPlaneRmsMm,
                    deformedControlsPlaneRmsMm = deformedBow.ControlPlaneRmsMm,
                    durationMs = stopwatch.Elapsed.TotalMilliseconds,
                    fallbackMarkers = fallbackCount
                });
            }

            stopwatch.Stop();
            LastUpdateDurationMs = stopwatch.Elapsed.TotalMilliseconds;
            LastVisibleActiveMarkerCount = visibleActiveCount;
            LastFallbackMarkerCount = fallbackCount;
            Status =
                $"Модель раны синхронизирована: активных {visibleActiveCount}/{_activeMarkerIds.Length}, " +
                $"fallback {fallbackCount}, rigidRMSE {LastRigidRmseMm:F1} мм, residual {LastResidualMaxMm:F1} мм, " +
                $"fitRMSE {LastMarkerFitRmseMm:F2} мм, fitMax {LastMarkerFitMaxMm:F2} мм, " +
                $"localBlend {NonRigidDeformationBlend:F2}, RBF {LastUpdateDurationMs:F1} мс.";
            LastReferenceReason = fallbackCount > 0
                ? "Деформация обновлена с fallback по пропавшим маркерам."
                : "Деформация обновлена.";
            return true;
        }

        private List<WoundMarkerBinding> GetVisibleBindings(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            return _model!.MarkerBindingsByCameraId
                .Where(binding => currentMarkers.ContainsKey(binding.Key))
                .OrderBy(binding => binding.Key)
                .Select(binding => binding.Value)
                .ToList();
        }

        private void CaptureReference(
            IReadOnlyList<WoundMarkerBinding> visibleBindings,
            IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            var stopwatch = Stopwatch.StartNew();
            _activeMarkerIds = visibleBindings.Select(binding => binding.CameraMarkerId).ToArray();
            var modelControlPoints = visibleBindings.Select(binding => binding.ModelPointMm).ToArray();
            var vertsForScale = _model!.ReferenceVerticesMm.ToArray();

            var orientRequested = _orientCavityTowardStereoCenterNextCapture &&
                                   !double.IsNaN(_stereoMidpointCamera1.X);
            var cavityReflectApplied = false;
            if (orientRequested)
            {
                cavityReflectApplied = TryFlipModelCavityTowardStereoMid(
                    ref modelControlPoints,
                    ref vertsForScale,
                    currentMarkers,
                    visibleBindings);
            }

            _orientCavityTowardStereoCenterNextCapture = false;

            var currentControlPoints = _activeMarkerIds.Select(id => currentMarkers[id]).ToArray();
            var (scaleMultiplier, transform, alignedControlPoints) =
                EstimateBestScaleAndTransform(modelControlPoints, currentControlPoints);
            var scaledReferenceVertices = vertsForScale
                .Select(point => ScalePoint(point, scaleMultiplier))
                .ToArray();
            var alignedVertices = scaledReferenceVertices
                .Select(transform.Transform)
                .ToArray();

            // The physical hand-made phantom is only approximately equal to the OBJ.
            // Keep the first frame as the deformation reference instead of forcing
            // every model marker to exactly hit its ArUco pair immediately.
            _referenceVerticesAligned = alignedVertices.ToArray();
            _referenceObservedControlPoints = currentControlPoints.ToArray();
            _rbf.Prepare(alignedVertices, alignedControlPoints);
            _referenceControlPointsAligned = alignedControlPoints.ToArray();
            _referenceMarkerBiasVectors = _referenceObservedControlPoints
                .Zip(_referenceControlPointsAligned, (observed, aligned) => observed - aligned)
                .ToArray();
            _referenceMarkerBiasRmseMm = _referenceMarkerBiasVectors.Length == 0
                ? 0
                : Math.Sqrt(_referenceMarkerBiasVectors.Average(vector => vector.Length * vector.Length));
            _referenceMarkerCentroidAligned = CalculateCentroid(_referenceControlPointsAligned);
            _referenceMarkerMaxRadiusMm = Math.Max(
                MinMarkerRadiusForProfileMm,
                _referenceControlPointsAligned.Length == 0
                    ? MinMarkerRadiusForProfileMm
                    : _referenceControlPointsAligned.Max(point => Distance(point, _referenceMarkerCentroidAligned)));
            _referenceSurfaceNormalCamera1 = EstimateSurfaceNormal(currentControlPoints);
            var initialVertices = alignedVertices;

            _mesh!.Positions = new Point3DCollection(initialVertices);
            _mesh.TriangleIndices = new Int32Collection(_model.TriangleIndices);
            _lastValidVertices = initialVertices.ToArray();
            _lastRigidVertices = initialVertices.ToArray();
            _lastUpdateMarkerSnapshot.Clear();
            UpdateMarkerSnapshot(currentControlPoints);
            UpdateMarkerFitDiagnostics(_referenceControlPointsAligned, currentMarkers, fallbackCount: 0);

            LastAlignmentRmseMm = CalculateRmse(alignedControlPoints, currentControlPoints);
            stopwatch.Stop();
            LastUpdateDurationMs = stopwatch.Elapsed.TotalMilliseconds;
            _lastUpdateAtUtc = DateTime.UtcNow;
            _lastReferenceCaptureAtUtc = _lastUpdateAtUtc;
            _referenceCaptured = true;
            _activeModelScaleMultiplier = scaleMultiplier;
            _activeModelScaleMode = $"auto x{_activeModelScaleMultiplier:F4}";
            _lastCaptureCombinedScale = scaleMultiplier * transform.Scale;
            _lastCaptureTranslationMm = transform.Translation;
            _poseState = new WoundPoseState(
                IsCaptured: true,
                FrameFrozen: false,
                FreezeReason: "",
                RigidRmseMm: LastAlignmentRmseMm,
                ResidualMaxMm: 0,
                ResidualP95Mm: 0,
                OutlierMarkerIds: Array.Empty<int>(),
                CurrentSurfaceNormalCamera1: _referenceSurfaceNormalCamera1,
                RigidScale: transform.Scale);
            LastGlobalCorrectionApplied = false;
            LastGlobalCorrectionScale = 1.0;
            LastGlobalCorrectionTranslationNormMm = 0;
            ResetMarkerFitDiagnostics();
            Status =
                $"Модель раны выровнена: маркеров {_activeMarkerIds.Length}, " +
                $"RMSE {LastAlignmentRmseMm:F1} мм, базовый offset {_referenceMarkerBiasRmseMm:F1} мм, " +
                $"масштаб {_activeModelScaleMode}, подготовка {LastUpdateDurationMs:F1} мс.";
            LastReferenceReason = "Опора деформации успешно захвачена.";
            SealCaptureStability(visibleBindings);
            SeedDeformationMotionBaseline(currentMarkers);

            var rot = transform.RotationMatrixClone();
            Diag("capture_reference", new
            {
                orientRequested,
                cavityReflectApplied,
                stereoMidCamera1Mm = new
                {
                    x = _stereoMidpointCamera1.X,
                    y = _stereoMidpointCamera1.Y,
                    z = _stereoMidpointCamera1.Z
                },
                activeMarkerIds = _activeMarkerIds.ToArray(),
                scaleMultiplier,
                combinedScale = _lastCaptureCombinedScale,
                similarityRigidScale = transform.Scale,
                translationMm = new { x = transform.Translation.X, y = transform.Translation.Y, z = transform.Translation.Z },
                rotationRowMajor = new[]
                {
                    rot[0, 0], rot[0, 1], rot[0, 2],
                    rot[1, 0], rot[1, 1], rot[1, 2],
                    rot[2, 0], rot[2, 1], rot[2, 2]
                },
                alignmentRmseMm = LastAlignmentRmseMm,
                referenceBiasRmseMm = _referenceMarkerBiasRmseMm,
                alignedVertexCount = alignedVertices.Length,
                estimatedSurfaceNormalCamera1 = new
                {
                    x = _referenceSurfaceNormalCamera1.X,
                    y = _referenceSurfaceNormalCamera1.Y,
                    z = _referenceSurfaceNormalCamera1.Z
                }
            });
        }

        private void ResetDeformationInputSmoothingState()
        {
            _deformationInputSmoothedCamera1.Clear();
            _motionCompareSnapshotCamera1.Clear();
            _deformationMotionGateOpen = false;
        }

        private void SeedDeformationMotionBaseline(IReadOnlyDictionary<int, Point3D> rawCamera1Markers)
        {
            _motionCompareSnapshotCamera1.Clear();
            foreach (var id in _activeMarkerIds)
            {
                if (!rawCamera1Markers.TryGetValue(id, out var raw))
                    continue;
                _deformationInputSmoothedCamera1[id] = raw;
                _motionCompareSnapshotCamera1[id] = raw;
            }

            _deformationMotionGateOpen = true;
        }

        private void RecordDeformationMotionBaseline(IReadOnlyDictionary<int, Point3D> smoothedMarkers)
        {
            foreach (var id in _activeMarkerIds)
            {
                if (!smoothedMarkers.TryGetValue(id, out var p))
                    continue;
                _motionCompareSnapshotCamera1[id] = p;
            }
        }

        private Dictionary<int, Point3D> BuildSmoothedMarkersForDeformation(
            IReadOnlyDictionary<int, Point3D> rawCamera1Markers)
        {
            var merged = new Dictionary<int, Point3D>();
            foreach (var kv in rawCamera1Markers)
                merged[kv.Key] = kv.Value;

            foreach (var id in _activeMarkerIds)
            {
                if (!rawCamera1Markers.TryGetValue(id, out var raw))
                    continue;
                if (!_deformationInputSmoothedCamera1.TryGetValue(id, out var prev))
                    prev = raw;

                var next = new Point3D(
                    prev.X + DeformationMarkerSmoothAlpha * (raw.X - prev.X),
                    prev.Y + DeformationMarkerSmoothAlpha * (raw.Y - prev.Y),
                    prev.Z + DeformationMarkerSmoothAlpha * (raw.Z - prev.Z));
                _deformationInputSmoothedCamera1[id] = next;
                merged[id] = next;
            }

            return merged;
        }

        private bool ShouldUpdateDeformationMotion(IReadOnlyDictionary<int, Point3D> smoothedMarkers)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUpdateAtUtc).TotalMilliseconds < UpdateIntervalMs)
                return false;

            var maxJumpMm = 0.0;
            var highJumpCount = 0;
            foreach (var markerId in _activeMarkerIds)
            {
                if (!smoothedMarkers.TryGetValue(markerId, out var current))
                    continue;

                if (!_motionCompareSnapshotCamera1.TryGetValue(markerId, out var baseline))
                    return true;

                var jump = Distance(baseline, current);
                maxJumpMm = Math.Max(maxJumpMm, jump);
                if (jump >= MarkerUpdateThresholdHighMm)
                    highJumpCount++;
            }

            if (!_deformationMotionGateOpen)
            {
                var openStrong = maxJumpMm >= MarkerUpdateThresholdHighMm * 1.35;
                var openConsensus = maxJumpMm >= MarkerUpdateThresholdHighMm &&
                                     highJumpCount >= MinActiveMarkersHighMotion;

                if (openStrong || openConsensus)
                {
                    _deformationMotionGateOpen = true;
                    return true;
                }

                return false;
            }

            if (maxJumpMm < MarkerUpdateThresholdLowMm)
            {
                _deformationMotionGateOpen = false;
                return false;
            }

            return true;
        }

        private bool TryFlipModelCavityTowardStereoMid(
            ref Point3D[] modelControlPoints,
            ref Point3D[] verticesModelMm,
            IReadOnlyDictionary<int, Point3D> currentMarkersCamera1,
            IReadOnlyList<WoundMarkerBinding> visibleBindings)
        {
            try
            {
                var trialCameraPts = visibleBindings.Select(b => currentMarkersCamera1[b.CameraMarkerId]).ToArray();
                var ringCentroidModel = CalculateCentroid(modelControlPoints);
                var meshCentroidModel = CalculateCentroid(verticesModelMm);
                var inwardModelGuess = meshCentroidModel - ringCentroidModel;
                if (inwardModelGuess.Length < 1e-4)
                    return false;

                inwardModelGuess.Normalize();
                var (_, preTransform, _) = EstimateBestScaleAndTransform(modelControlPoints, trialCameraPts);
                var inwardCamGuess = preTransform.TransformDirection(inwardModelGuess);

                var markerMidCam = CalculateCentroid(trialCameraPts);
                var towardStereo = _stereoMidpointCamera1 - markerMidCam;
                if (inwardCamGuess.Length < 1e-4 || towardStereo.Length < 1e-4)
                    return false;

                towardStereo.Normalize();
                inwardCamGuess.Normalize();
                var inwardStereoDot = Vector3D.DotProduct(inwardCamGuess, towardStereo);
                if (inwardStereoDot >= 0.02)
                    return false;

                ReflectPointArrayThroughPlane(verticesModelMm, ringCentroidModel, inwardModelGuess);
                ReflectPointArrayThroughPlane(modelControlPoints, ringCentroidModel, inwardModelGuess);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ReflectPointArrayThroughPlane(Point3D[] points, Point3D planePoint, Vector3D unitPlaneNormal)
        {
            if (points.Length == 0)
                return;

            unitPlaneNormal.Normalize();
            if (unitPlaneNormal.Length < 1e-6)
                return;

            for (var i = 0; i < points.Length; i++)
            {
                var v = points[i] - planePoint;
                var vn = Vector3D.DotProduct(v, unitPlaneNormal);
                points[i] = planePoint + (v - 2 * vn * unitPlaneNormal);
            }
        }

        private static Point3D ScalePoint(Point3D point, double scale)
        {
            return new Point3D(point.X * scale, point.Y * scale, point.Z * scale);
        }

        private static (double ScaleMultiplier, SimilarityTransform Transform, Point3D[] AlignedControlPoints)
            EstimateBestScaleAndTransform(
                IReadOnlyList<Point3D> modelControlPoints,
                IReadOnlyList<Point3D> currentControlPoints)
        {
            var bestScale = 1.0;
            var bestTransform = SimilarityTransform.Estimate(modelControlPoints, currentControlPoints);
            var bestAlignedControls = modelControlPoints.Select(bestTransform.Transform).ToArray();
            var bestRmse = CalculateRmse(bestAlignedControls, currentControlPoints);
            var bestScore = bestRmse + CaptureCombinedScalePreferencePenalty(
                Math.Abs(bestTransform.Scale));

            foreach (var candidate in AutoScaleCandidates)
            {
                SimilarityTransform candidateTransform;
                Point3D[] candidateAlignedControls;
                try
                {
                    var scaledControls = modelControlPoints.Select(point => ScalePoint(point, candidate)).ToArray();
                    candidateTransform = SimilarityTransform.Estimate(scaledControls, currentControlPoints);
                    candidateAlignedControls = scaledControls.Select(candidateTransform.Transform).ToArray();
                }
                catch
                {
                    continue;
                }

                var candidateRmse = CalculateRmse(candidateAlignedControls, currentControlPoints);
                var combinedAbs = Math.Abs(candidate * candidateTransform.Scale);
                var score = candidateRmse + CaptureCombinedScalePreferencePenalty(combinedAbs);
                if (score > bestScore + 1e-9)
                    continue;
                if (Math.Abs(score - bestScore) < 1e-9 && candidateRmse + 1e-9 >= bestRmse)
                    continue;

                bestScale = candidate;
                bestTransform = candidateTransform;
                bestAlignedControls = candidateAlignedControls;
                bestRmse = candidateRmse;
                bestScore = score;
            }

            return (bestScale, bestTransform, bestAlignedControls);
        }

        private static double CaptureCombinedScalePreferencePenalty(double candidateTimesSimilarityAbs)
        {
            if (double.IsNaN(candidateTimesSimilarityAbs) ||
                double.IsInfinity(candidateTimesSimilarityAbs) ||
                candidateTimesSimilarityAbs < 1e-12)
                return CaptureCombinedScalePenaltyWeight * 4.0;

            var c = candidateTimesSimilarityAbs;
            if (c < CaptureCombinedScalePreferLo)
                return CaptureCombinedScalePenaltyWeight *
                       Math.Log(CaptureCombinedScalePreferLo / c);

            if (c > CaptureCombinedScalePreferHi)
                return CaptureCombinedScalePenaltyWeight *
                       Math.Log(c / CaptureCombinedScalePreferHi);

            return 0.0;
        }

        private static string GetBindingSetSignature(IReadOnlyList<WoundMarkerBinding> visibleBindings)
        {
            return string.Join(",", visibleBindings.Select(b => b.CameraMarkerId));
        }

        private void FeedCaptureStability(IReadOnlyList<WoundMarkerBinding> visibleBindings)
        {
            var signature = GetBindingSetSignature(visibleBindings);
            if (signature != _pendingBindingSignature)
            {
                _pendingBindingSignature = signature;
                _pendingStableFrameCount = 1;
            }
            else
            {
                _pendingStableFrameCount++;
            }
        }

        private void ClearCaptureStability()
        {
            _pendingBindingSignature = "";
            _pendingStableFrameCount = 0;
        }

        private void SealCaptureStability(IReadOnlyList<WoundMarkerBinding> visibleBindings)
        {
            _pendingBindingSignature = GetBindingSetSignature(visibleBindings);
            _pendingStableFrameCount = StableFramesForReferenceCapture;
        }

        private bool TryBuildCurrentControlPoints(
            IReadOnlyDictionary<int, Point3D> currentMarkers,
            out Point3D[] controlPoints,
            out bool[] visibleMask,
            out int visibleActiveCount,
            out int fallbackCount)
        {
            controlPoints = new Point3D[_activeMarkerIds.Length];
            visibleMask = new bool[_activeMarkerIds.Length];
            visibleActiveCount = 0;
            fallbackCount = 0;

            for (var i = 0; i < _activeMarkerIds.Length; i++)
            {
                var markerId = _activeMarkerIds[i];
                if (currentMarkers.TryGetValue(markerId, out var currentPoint))
                {
                    controlPoints[i] = currentPoint;
                    visibleMask[i] = true;
                    visibleActiveCount++;
                    continue;
                }

                if (_lastUpdateMarkerSnapshot.TryGetValue(markerId, out var fallbackPoint))
                {
                    controlPoints[i] = fallbackPoint;
                    fallbackCount++;
                    continue;
                }

                if (_referenceObservedControlPoints.Length > i)
                {
                    controlPoints[i] = _referenceObservedControlPoints[i];
                    fallbackCount++;
                    continue;
                }

                return false;
            }

            return visibleActiveCount >= 3;
        }

        private bool TryBuildPhysicallyFilteredControlPoints(
            IReadOnlyList<Point3D> currentControlPoints,
            IReadOnlyList<bool> visibleMask,
            out Point3D[] filtered,
            out WoundPoseState poseState,
            out SimilarityTransform globalMotion)
        {
            filtered = currentControlPoints.ToArray();
            poseState = _poseState;
            globalMotion = SimilarityTransform.Identity;
            if (_referenceObservedControlPoints.Length != currentControlPoints.Count ||
                _referenceControlPointsAligned.Length != currentControlPoints.Count ||
                _referenceObservedControlPoints.Length < 3)
            {
                poseState = WoundPoseState.Frozen("Опорное состояние деформации неполное.");
                return false;
            }

            if (visibleMask.Count != currentControlPoints.Count)
            {
                poseState = WoundPoseState.Frozen("Внутренняя ошибка видимости маркеров.");
                return false;
            }

            Point3D[] rigidObservedPoints;
            HashSet<int> rigidOutlierMarkerIds;
            try
            {
                if (!TryEstimateRobustGlobalMotion(
                        currentControlPoints,
                        visibleMask,
                        out globalMotion,
                        out rigidOutlierMarkerIds,
                        out var fitFailureReason))
                {
                    poseState = WoundPoseState.Frozen(fitFailureReason);
                    return false;
                }
            }
            catch (Exception ex)
            {
                poseState = WoundPoseState.Frozen($"Rigid fit не построен: {ex.Message}");
                return false;
            }

            var visibleIndices = Enumerable
                .Range(0, currentControlPoints.Count)
                .Where(i => visibleMask[i])
                .ToArray();
            if (visibleIndices.Length < 3)
            {
                poseState = WoundPoseState.Frozen("Недостаточно видимых маркеров для rigid fit.");
                return false;
            }

            var rawScaleDeviation = Math.Abs(globalMotion.Scale - 1.0);
            if (globalMotion.Scale < 0)
            {
                globalMotion = globalMotion.WithScale(Math.Abs(globalMotion.Scale));
                rawScaleDeviation = Math.Abs(globalMotion.Scale - 1.0);
            }

            if (rawScaleDeviation > MaxRigidScaleHardReject)
            {
                poseState = WoundPoseState.Frozen(
                    $"Отклонён кадр: scale={globalMotion.Scale:F3}.",
                    currentSurfaceNormalCamera1: EstimateSurfaceNormal(visibleIndices.Select(index => currentControlPoints[index]).ToArray()),
                    rigidScale: globalMotion.Scale);
                return false;
            }

            if (rawScaleDeviation > MaxRigidScaleChange)
            {
                globalMotion = globalMotion.WithScale(Clamp(
                    globalMotion.Scale,
                    1.0 - MaxRigidScaleChange,
                    1.0 + MaxRigidScaleChange));
            }

            rigidObservedPoints = _referenceObservedControlPoints
                .Select(globalMotion.Transform)
                .ToArray();
            var rigidStatsIndices = BuildPeripheralMarkerIndicesForRigidFit(visibleIndices);
            var rigidRmse = Math.Sqrt(rigidStatsIndices
                .Select(index => Math.Pow(Distance(rigidObservedPoints[index], currentControlPoints[index]), 2))
                .Average());
            var currentNormal = EstimateSurfaceNormal(visibleIndices.Select(index => currentControlPoints[index]).ToArray());
            var expectedNormal = globalMotion.TransformDirection(_referenceSurfaceNormalCamera1);
            if (Vector3D.DotProduct(currentNormal, expectedNormal) < 0)
                currentNormal = -currentNormal;
            var normalDot = Vector3D.DotProduct(expectedNormal, currentNormal);
            if (normalDot < MinSurfaceNormalDot)
            {
                poseState = WoundPoseState.Frozen(
                    $"Отклонён кадр: flip нормали dot={normalDot:F2}.",
                    rigidRmse,
                    currentNormal,
                    globalMotion.Scale);
                return false;
            }

            if (rigidRmse > MaxRigidRmseMm)
            {
                poseState = WoundPoseState.Frozen(
                    $"Отклонён кадр: rigid RMSE={rigidRmse:F1} мм.",
                    rigidRmse,
                    currentNormal,
                    globalMotion.Scale);
                return false;
            }

            var residualLengths = new List<double>(currentControlPoints.Count);
            var outliers = new List<int>();
            filtered = new Point3D[currentControlPoints.Count];
            for (var i = 0; i < currentControlPoints.Count; i++)
            {
                var rigidObservedPoint = rigidObservedPoints[i];
                var biasCompensatedReferenceControl = GetBiasCompensatedReferenceControlPoint(i);
                var rigidModelPoint = globalMotion.Transform(biasCompensatedReferenceControl);
                var markerId = _activeMarkerIds.Length > i ? _activeMarkerIds[i] : -1;
                var markerVisible = visibleMask[i];
                if (!markerVisible)
                {
                    filtered[i] = rigidModelPoint;
                    continue;
                }

                var residual = currentControlPoints[i] - rigidObservedPoint;
                var residualLength = residual.Length;
                residualLengths.Add(residualLength);
                var hardOutlier = residualLength > MaxResidualHardOutlierMm;
                var robustOutlier = markerId >= 0 &&
                                    rigidOutlierMarkerIds.Contains(markerId) &&
                                    residualLength > MaxResidualOutlierMm;
                if (markerId >= 0 && (hardOutlier || robustOutlier))
                {
                    outliers.Add(markerId);
                    var clampedResidual = ClampVectorLength(residual, MaxResidualOutlierMm);
                    filtered[i] = rigidModelPoint + clampedResidual * 0.35;
                    continue;
                }

                var markerNormR = _referenceMarkerMaxRadiusMm > 1e-6
                    ? Clamp(
                        Distance(_referenceControlPointsAligned[i], _referenceMarkerCentroidAligned) /
                        _referenceMarkerMaxRadiusMm,
                        0,
                        1)
                    : 1.0;
                var innerBoost = 1.0 + InnerMarkerNonRigidBoost * (1.0 - markerNormR);
                var maxTan = MaxTangentialResidualMm * innerBoost;
                var maxNorm = MaxNormalResidualMm * innerBoost;

                if (residualLength <= NonRigidResidualDeadZoneMm)
                {
                    filtered[i] = rigidModelPoint;
                    continue;
                }

                var normalPartLength = Vector3D.DotProduct(residual, expectedNormal);
                var normalPart = expectedNormal * Clamp(
                    normalPartLength,
                    -maxNorm,
                    maxNorm);
                var tangentPart = residual - expectedNormal * normalPartLength;
                var tangentLength = tangentPart.Length;
                if (tangentLength > maxTan)
                {
                    tangentPart.Normalize();
                    tangentPart *= maxTan;
                }

                filtered[i] = rigidModelPoint +
                              tangentPart * NonRigidDeformationBlend +
                              normalPart * NonRigidNormalBlend;
            }

            poseState = new WoundPoseState(
                IsCaptured: true,
                FrameFrozen: false,
                FreezeReason: "",
                RigidRmseMm: rigidRmse,
                ResidualMaxMm: residualLengths.Count == 0 ? 0 : residualLengths.Max(),
                ResidualP95Mm: Percentile(residualLengths, 0.95),
                OutlierMarkerIds: outliers
                    .Concat(rigidOutlierMarkerIds)
                    .Where(id => id >= 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray(),
                CurrentSurfaceNormalCamera1: currentNormal,
                RigidScale: globalMotion.Scale);
            return true;
        }

        /// <summary>
        /// Жёсткую позу оцениваем по периферии маркерного кольца: иначе similarity «съедает»
        /// изгиб, и RBF не получает остаточного сигнала (особенно при точной привязке маркеров).
        /// </summary>
        private IReadOnlyList<int> BuildPeripheralMarkerIndicesForRigidFit(IReadOnlyList<int> visibleIndices)
        {
            if (visibleIndices.Count <= 3)
                return visibleIndices;

            var centroid = CalculateCentroid(
                visibleIndices.Select(index => _referenceObservedControlPoints[index]).ToList());
            var radii = visibleIndices
                .Select(index => Distance(_referenceObservedControlPoints[index], centroid))
                .ToList();
            var medianRadius = Percentile(radii, 0.5);
            var peripheral = visibleIndices
                .Where(index => Distance(_referenceObservedControlPoints[index], centroid) >= medianRadius - 1e-6)
                .ToList();
            return peripheral.Count >= 3 ? peripheral : visibleIndices.ToList();
        }

        private void MarkRigidRejectedFromPeripheralFit(
            SimilarityTransform transform,
            IReadOnlyList<int> activeVisibleIndices,
            HashSet<int> peripheralRigidFitIndices,
            IReadOnlyList<Point3D> currentControlPoints,
            HashSet<int> rejectedMarkerIds)
        {
            foreach (var index in activeVisibleIndices)
            {
                var residualMm = Distance(
                    transform.Transform(_referenceObservedControlPoints[index]),
                    currentControlPoints[index]);
                var threshold = peripheralRigidFitIndices.Contains(index)
                    ? RigidInlierThresholdMm
                    : RigidInlierInnerMarkerThresholdMm;
                if (residualMm <= threshold)
                    continue;

                if (_activeMarkerIds.Length > index)
                    rejectedMarkerIds.Add(_activeMarkerIds[index]);
            }
        }

        private bool TryEstimateRobustGlobalMotion(
            IReadOnlyList<Point3D> currentControlPoints,
            IReadOnlyList<bool> visibleMask,
            out SimilarityTransform transform,
            out HashSet<int> rejectedMarkerIds,
            out string failureReason)
        {
            transform = SimilarityTransform.Identity;
            rejectedMarkerIds = new HashSet<int>();
            failureReason = "";

            var activeVisibleIndices = Enumerable
                .Range(0, currentControlPoints.Count)
                .Where(i => visibleMask[i])
                .ToList();
            if (activeVisibleIndices.Count < 3)
            {
                failureReason = "Недостаточно видимых маркеров для оценки rigid pose.";
                return false;
            }

            var rigidFitIndices = BuildPeripheralMarkerIndicesForRigidFit(activeVisibleIndices);
            var peripheralRigidFitSet = new HashSet<int>(rigidFitIndices);

            if (rigidFitIndices.Count == 3)
            {
                try
                {
                    transform = SimilarityTransform.Estimate(
                        rigidFitIndices.Select(index => _referenceObservedControlPoints[index]).ToArray(),
                        rigidFitIndices.Select(index => currentControlPoints[index]).ToArray());
                }
                catch (Exception ex)
                {
                    failureReason = $"Rigid fit не построен: {ex.Message}";
                    return false;
                }

                MarkRigidRejectedFromPeripheralFit(
                    transform,
                    activeVisibleIndices,
                    peripheralRigidFitSet,
                    currentControlPoints,
                    rejectedMarkerIds);
                return true;
            }

            var localTriplets = new List<int[]>();
            BuildCombinations(
                start: 0,
                depth: 0,
                targetCount: 3,
                current: new int[3],
                result: localTriplets,
                sourceCount: rigidFitIndices.Count);

            var bestInlierIndices = Array.Empty<int>();
            var bestInlierCount = -1;
            var bestInlierRmse = double.PositiveInfinity;
            var bestScaleDeviation = double.PositiveInfinity;
            var bestScore = double.PositiveInfinity;
            var bestCandidate = SimilarityTransform.Identity;

            foreach (var localTriplet in localTriplets)
            {
                var triplet = localTriplet
                    .Select(localIndex => rigidFitIndices[localIndex])
                    .ToArray();
                SimilarityTransform candidate;
                try
                {
                    candidate = SimilarityTransform.Estimate(
                        triplet.Select(index => _referenceObservedControlPoints[index]).ToArray(),
                        triplet.Select(index => currentControlPoints[index]).ToArray());
                }
                catch
                {
                    continue;
                }

                var candidateScale = Math.Abs(candidate.Scale);
                if (candidateScale < MinRigidScaleCandidate || candidateScale > MaxRigidScaleCandidate)
                    continue;

                var inliers = new List<int>(rigidFitIndices.Count);
                var inlierSumSquared = 0.0;
                foreach (var index in rigidFitIndices)
                {
                    var residualMm = Distance(
                        candidate.Transform(_referenceObservedControlPoints[index]),
                        currentControlPoints[index]);
                    if (residualMm > RigidInlierThresholdMm)
                        continue;

                    inliers.Add(index);
                    inlierSumSquared += residualMm * residualMm;
                }

                if (inliers.Count < 3)
                    continue;

                var inlierRmse = Math.Sqrt(inlierSumSquared / inliers.Count);
                var scaleDeviation = Math.Abs(candidate.Scale - 1.0);
                var score = inlierRmse + scaleDeviation * RigidScalePenaltyWeight;
                var isBetter = inliers.Count > bestInlierCount;
                if (!isBetter && inliers.Count == bestInlierCount)
                {
                    if (score + 1e-9 < bestScore)
                    {
                        isBetter = true;
                    }
                    else if (Math.Abs(score - bestScore) <= 1e-9)
                    {
                        if (inlierRmse + 1e-9 < bestInlierRmse)
                        {
                            isBetter = true;
                        }
                        else if (Math.Abs(inlierRmse - bestInlierRmse) <= 1e-9 &&
                                 scaleDeviation + 1e-9 < bestScaleDeviation)
                        {
                            isBetter = true;
                        }
                    }
                }
                if (!isBetter)
                    continue;

                bestInlierCount = inliers.Count;
                bestInlierRmse = inlierRmse;
                bestScaleDeviation = scaleDeviation;
                bestScore = score;
                bestInlierIndices = inliers.ToArray();
                bestCandidate = candidate;
            }

            if (bestInlierCount < 3 || bestInlierIndices.Length < 3)
            {
                failureReason = "Rigid pose неустойчив: недостаточно согласованных inlier-маркеров.";
                return false;
            }

            try
            {
                transform = SimilarityTransform.Estimate(
                    bestInlierIndices.Select(index => _referenceObservedControlPoints[index]).ToArray(),
                    bestInlierIndices.Select(index => currentControlPoints[index]).ToArray());
            }
            catch (Exception ex)
            {
                transform = bestCandidate;
                System.Diagnostics.Debug.WriteLine($"Rigid fit refine fallback: {ex.Message}");
            }

            if (transform.Scale <= 0)
            {
                transform = bestCandidate;
                if (transform.Scale <= 0)
                    transform = transform.WithScale(1.0);
            }

            MarkRigidRejectedFromPeripheralFit(
                transform,
                activeVisibleIndices,
                peripheralRigidFitSet,
                currentControlPoints,
                rejectedMarkerIds);

            return true;
        }

        private static double RmsNormalDeviationMm(
            IReadOnlyList<Point3D> points,
            Point3D planeOrigin,
            Vector3D unitNormal)
        {
            if (points.Count == 0)
                return 0;

            var nx = unitNormal.X;
            var ny = unitNormal.Y;
            var nz = unitNormal.Z;
            double sumSq = 0;
            foreach (var p in points)
            {
                var vx = p.X - planeOrigin.X;
                var vy = p.Y - planeOrigin.Y;
                var vz = p.Z - planeOrigin.Z;
                var d = vx * nx + vy * ny + vz * nz;
                sumSq += d * d;
            }

            return Math.Sqrt(sumSq / points.Count);
        }

        private static (double AlongNormalMm, double VertexPlaneRmsMm, double ControlPlaneRmsMm) BowMetrics(
            IReadOnlyList<Point3D> meshVerticesCamera1,
            IReadOnlyList<Point3D> controlsCamera1,
            SimilarityTransform globalMotion,
            Vector3D referenceNormalCamera1)
        {
            var n = globalMotion.TransformDirection(referenceNormalCamera1);
            if (n.LengthSquared < 1e-18)
                n = new Vector3D(0, 0, 1);
            n.Normalize();

            var ctrlCentroid = CalculateCentroid(controlsCamera1);
            var stride = Math.Max(1, meshVerticesCamera1.Count / 2000);
            var samples = new List<Point3D>(Math.Min(2200, meshVerticesCamera1.Count));
            for (var i = 0; i < meshVerticesCamera1.Count; i += stride)
                samples.Add(meshVerticesCamera1[i]);

            if (samples.Count == 0)
                return (0, 0, 0);

            var meshCentroid = CalculateCentroid(samples);
            var along = Vector3D.DotProduct((Vector3D)(meshCentroid - ctrlCentroid), n);
            var vRms = RmsNormalDeviationMm(samples, ctrlCentroid, n);
            var cRms = RmsNormalDeviationMm(controlsCamera1, ctrlCentroid, n);
            return (along, vRms, cRms);
        }

        private Point3D[] ApplyPhysicalVertexConstraints(
            IReadOnlyList<Point3D> rigidVertices,
            IReadOnlyList<Point3D> candidateVertices,
            IReadOnlyList<Point3D> controlPoints,
            SimilarityTransform globalMotion)
        {
            if (rigidVertices.Count != candidateVertices.Count || rigidVertices.Count == 0)
                return Array.Empty<Point3D>();

            var n = globalMotion.TransformDirection(_referenceSurfaceNormalCamera1);
            if (n.LengthSquared > 1e-18)
                n.Normalize();
            else
                n = new Vector3D(0, 0, 1);

            var rigidMarkerCentroid = globalMotion.Transform(_referenceMarkerCentroidAligned);
            var markerRadius = Math.Max(
                MinMarkerRadiusForProfileMm,
                _referenceMarkerMaxRadiusMm * Math.Max(0.75, Math.Abs(globalMotion.Scale)));
            var result = new Point3D[candidateVertices.Count];
            for (var i = 0; i < candidateVertices.Count; i++)
            {
                var rigidPoint = rigidVertices[i];
                var candidatePoint = candidateVertices[i];
                var residual = candidatePoint - rigidPoint;
                var nearestDistance = double.PositiveInfinity;
                for (var c = 0; c < controlPoints.Count; c++)
                {
                    var distance = Distance(rigidPoint, controlPoints[c]);
                    if (distance < nearestDistance)
                        nearestDistance = distance;
                }

                var influence = double.IsFinite(nearestDistance)
                    ? Math.Exp(-nearestDistance / VertexResidualDecayDistanceMm)
                    : 0.0;
                var normalizedRadius = Clamp(Distance(rigidPoint, rigidMarkerCentroid) / markerRadius, 0, 1);
                var centerToEdgeFactor = CenterResidualFactor +
                                         (1.0 - CenterResidualFactor) *
                                         Math.Pow(normalizedRadius, CenterEdgeProfilePower);
                var maxResidual = MaxVertexResidualFarMm +
                                  (MaxVertexResidualNearMarkersMm - MaxVertexResidualFarMm) * influence;
                maxResidual *= centerToEdgeFactor;
                var normalCapScale =
                    CenterVertexNormalStiffnessMin +
                    (1.0 - CenterVertexNormalStiffnessMin) *
                    Math.Pow(normalizedRadius, WoundNormalStiffnessPower);
                var maxNormal = maxResidual * normalCapScale;
                var maxTangent = maxResidual;
                var normalCoord = Vector3D.DotProduct(residual, n);
                var tangential = residual - n * normalCoord;
                var clampedNormal = Clamp(normalCoord, -maxNormal, maxNormal);
                var tangentialClamped = ClampVectorLength(tangential, maxTangent);
                var constrainedResidual = tangentialClamped + n * clampedNormal;

                if (_lastValidVertices.Length == candidateVertices.Count &&
                    _lastRigidVertices.Length == candidateVertices.Count)
                {
                    var previousResidual = _lastValidVertices[i] - _lastRigidVertices[i];
                    var residualStep = constrainedResidual - previousResidual;
                    var clampedResidualStep = ClampVectorLength(residualStep, MaxResidualStepPerFrameMm);
                    var updatedResidual = previousResidual + clampedResidualStep;
                    constrainedResidual = previousResidual + (updatedResidual - previousResidual) * ResidualTemporalBlend;
                }

                result[i] = rigidPoint + constrainedResidual;
            }

            return result;
        }

        private void FreezeMesh(WoundPoseState poseState)
        {
            if (DiagThrottle(ref _lastDiagMeshFrozenThrottleUtc, 650))
            {
                Diag("mesh_frozen", new
                {
                    poseState.FrameFrozen,
                    poseState.FreezeReason,
                    rigidRmseMm = poseState.RigidRmseMm,
                    outlierMarkers = poseState.OutlierMarkerIds.Count
                });
            }
            _poseState = poseState;
            if (_lastValidVertices.Length > 0 && _mesh != null)
                _mesh.Positions = new Point3DCollection(_lastValidVertices);

            Status = $"Геометрия заморожена: {poseState.FreezeReason}";
            LastReferenceReason = poseState.FreezeReason;
        }

        private void ResetMarkerFitDiagnostics()
        {
            _lastMarkerFitByIdMm.Clear();
            _lastPredictedMarkerPositionsCamera1.Clear();
            _lastObservedMarkerPositionsCamera1.Clear();
            LastMarkerFitRmseMm = 0;
            LastMarkerFitMaxMm = 0;
            LastMarkerFitWorstMarkerId = -1;
            LastVisibleActiveMarkerCount = 0;
            LastFallbackMarkerCount = 0;
            LastGlobalCorrectionApplied = false;
            LastGlobalCorrectionScale = 1.0;
            LastGlobalCorrectionTranslationNormMm = 0;
        }

        private void ClearLastCaptureDiagnostics()
        {
            _lastCaptureCombinedScale = 1.0;
            _lastCaptureTranslationMm = new Vector3D(0, 0, 0);
        }

        private void UpdateMarkerFitDiagnostics(
            IReadOnlyList<Point3D> predictedControlPoints,
            IReadOnlyDictionary<int, Point3D> currentMarkers,
            int fallbackCount)
        {
            _lastMarkerFitByIdMm.Clear();
            _lastPredictedMarkerPositionsCamera1.Clear();
            _lastObservedMarkerPositionsCamera1.Clear();
            LastFallbackMarkerCount = fallbackCount;

            if (predictedControlPoints.Count != _activeMarkerIds.Length)
            {
                LastMarkerFitRmseMm = 0;
                LastMarkerFitMaxMm = 0;
                LastMarkerFitWorstMarkerId = -1;
                LastVisibleActiveMarkerCount = 0;
                return;
            }

            var visibleCount = 0;
            var sumSquared = 0.0;
            var maxResidual = 0.0;
            var worstMarkerId = -1;
            for (var i = 0; i < _activeMarkerIds.Length; i++)
            {
                var markerId = _activeMarkerIds[i];
                _lastPredictedMarkerPositionsCamera1[markerId] = predictedControlPoints[i];
                if (!currentMarkers.TryGetValue(markerId, out var observedPoint))
                    continue;

                _lastObservedMarkerPositionsCamera1[markerId] = observedPoint;
                var residual = Distance(predictedControlPoints[i], observedPoint);
                _lastMarkerFitByIdMm[markerId] = residual;
                sumSquared += residual * residual;
                visibleCount++;
                if (residual > maxResidual)
                {
                    maxResidual = residual;
                    worstMarkerId = markerId;
                }
            }

            LastVisibleActiveMarkerCount = visibleCount;
            LastMarkerFitRmseMm = visibleCount > 0
                ? Math.Sqrt(sumSquared / visibleCount)
                : 0;
            LastMarkerFitMaxMm = maxResidual;
            LastMarkerFitWorstMarkerId = worstMarkerId;
        }

        private bool TryApplyGlobalMarkerCorrection(
            Point3D[] deformedVertices,
            Point3D[] predictedControlPoints,
            IReadOnlyDictionary<int, Point3D> currentMarkers,
            out Point3D[] correctedVertices,
            out Point3D[] correctedControlPoints)
        {
            correctedVertices = deformedVertices;
            correctedControlPoints = predictedControlPoints;
            LastGlobalCorrectionApplied = false;
            LastGlobalCorrectionScale = 1.0;
            LastGlobalCorrectionTranslationNormMm = 0;

            if (predictedControlPoints.Length != _activeMarkerIds.Length)
                return false;

            var source = new List<Point3D>(_activeMarkerIds.Length);
            var target = new List<Point3D>(_activeMarkerIds.Length);
            for (var i = 0; i < _activeMarkerIds.Length; i++)
            {
                var markerId = _activeMarkerIds[i];
                if (!currentMarkers.TryGetValue(markerId, out var observedPoint))
                    continue;

                source.Add(predictedControlPoints[i]);
                target.Add(observedPoint);
            }

            if (source.Count < 3)
                return false;

            var beforeRmse = CalculateRmse(source, target);
            if (beforeRmse < MarkerFitCorrectionThresholdMm)
                return false;

            SimilarityTransform correction;
            try
            {
                correction = SimilarityTransform.Estimate(source, target);
            }
            catch
            {
                return false;
            }

            var correctedSource = source.Select(correction.Transform).ToArray();
            var afterRmse = CalculateRmse(correctedSource, target);
            if (afterRmse + 1e-6 >= beforeRmse)
                return false;

            correctedVertices = deformedVertices
                .Select(correction.Transform)
                .ToArray();
            correctedControlPoints = predictedControlPoints
                .Select(correction.Transform)
                .ToArray();
            LastGlobalCorrectionApplied = true;
            LastGlobalCorrectionScale = correction.Scale;
            LastGlobalCorrectionTranslationNormMm = correction.Translation.Length;
            return true;
        }

        private void UpdateMarkerSnapshot(IReadOnlyList<Point3D> points)
        {
            _lastUpdateAtUtc = DateTime.UtcNow;
            for (var i = 0; i < _activeMarkerIds.Length; i++)
            {
                _lastUpdateMarkerSnapshot[_activeMarkerIds[i]] = points[i];
            }
        }

        private Point3D[] BuildSnapshotControlPoints(
            IReadOnlyList<Point3D> observedControlPoints,
            IReadOnlyList<Point3D> filteredControlPoints,
            IReadOnlyList<bool> visibleMask,
            IReadOnlyList<int> outlierMarkerIds)
        {
            var snapshot = new Point3D[_activeMarkerIds.Length];
            var outlierSet = new HashSet<int>(outlierMarkerIds);
            for (var i = 0; i < _activeMarkerIds.Length; i++)
            {
                var markerId = _activeMarkerIds[i];
                var markerVisible = i < visibleMask.Count && visibleMask[i];
                if (!markerVisible || outlierSet.Contains(markerId))
                {
                    snapshot[i] = filteredControlPoints[i];
                    continue;
                }

                snapshot[i] = observedControlPoints[i];
            }

            return snapshot;
        }

        private Point3D GetBiasCompensatedReferenceControlPoint(int index)
        {
            var point = _referenceControlPointsAligned[index];
            if (_referenceMarkerBiasVectors.Length != _referenceControlPointsAligned.Length ||
                index < 0 ||
                index >= _referenceMarkerBiasVectors.Length)
            {
                return point;
            }

            return point + _referenceMarkerBiasVectors[index] * MarkerBiasCompensation;
        }

        private static bool AreVerticesValid(IReadOnlyList<Point3D> vertices)
        {
            foreach (var vertex in vertices)
            {
                if (double.IsNaN(vertex.X) || double.IsNaN(vertex.Y) || double.IsNaN(vertex.Z) ||
                    double.IsInfinity(vertex.X) || double.IsInfinity(vertex.Y) || double.IsInfinity(vertex.Z) ||
                    Math.Abs(vertex.X) > MaxReasonableVertexAbsMm ||
                    Math.Abs(vertex.Y) > MaxReasonableVertexAbsMm ||
                    Math.Abs(vertex.Z) > MaxReasonableVertexAbsMm)
                {
                    return false;
                }
            }

            return true;
        }

        private static double CalculateRmse(IReadOnlyList<Point3D> expected, IReadOnlyList<Point3D> actual)
        {
            if (expected.Count == 0 || expected.Count != actual.Count)
                return 0;

            var sumSquared = 0.0;
            for (var i = 0; i < expected.Count; i++)
            {
                var distance = Distance(expected[i], actual[i]);
                sumSquared += distance * distance;
            }

            return Math.Sqrt(sumSquared / expected.Count);
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Vector3D ClampVectorLength(Vector3D vector, double maxLength)
        {
            if (maxLength <= 0)
                return new Vector3D(0, 0, 0);

            var length = vector.Length;
            if (length <= maxLength || length < 1e-9)
                return vector;

            var scale = maxLength / length;
            return vector * scale;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            if (values.Count == 0)
                return 0;

            var sorted = values.OrderBy(value => value).ToArray();
            var index = (sorted.Length - 1) * Clamp(percentile, 0, 1);
            var lower = (int)Math.Floor(index);
            var upper = (int)Math.Ceiling(index);
            if (lower == upper)
                return sorted[lower];

            var alpha = index - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * alpha;
        }

        private static Vector3D EstimateSurfaceNormal(IReadOnlyList<Point3D> points)
        {
            if (points.Count < 3)
                return new Vector3D(0, 0, 1);

            var centerX = points.Average(point => point.X);
            var centerY = points.Average(point => point.Y);
            var centerZ = points.Average(point => point.Z);
            var xx = 0.0;
            var xy = 0.0;
            var xz = 0.0;
            var yy = 0.0;
            var yz = 0.0;
            var zz = 0.0;
            foreach (var point in points)
            {
                var dx = point.X - centerX;
                var dy = point.Y - centerY;
                var dz = point.Z - centerZ;
                xx += dx * dx;
                xy += dx * dy;
                xz += dx * dz;
                yy += dy * dy;
                yz += dy * dz;
                zz += dz * dz;
            }

            var detX = yy * zz - yz * yz;
            var detY = xx * zz - xz * xz;
            var detZ = xx * yy - xy * xy;
            Vector3D normal;
            if (detX >= detY && detX >= detZ)
            {
                normal = new Vector3D(detX, xz * yz - xy * zz, xy * yz - xz * yy);
            }
            else if (detY >= detX && detY >= detZ)
            {
                normal = new Vector3D(xz * yz - xy * zz, detY, xy * xz - yz * xx);
            }
            else
            {
                normal = new Vector3D(xy * yz - xz * yy, xy * xz - yz * xx, detZ);
            }

            if (normal.Length < 1e-6)
            {
                for (var a = 0; a < points.Count - 2; a++)
                {
                    for (var b = a + 1; b < points.Count - 1; b++)
                    {
                        for (var c = b + 1; c < points.Count; c++)
                        {
                            normal = Vector3D.CrossProduct(points[b] - points[a], points[c] - points[a]);
                            if (normal.Length >= 1e-6)
                            {
                                normal.Normalize();
                                return normal;
                            }
                        }
                    }
                }

                return new Vector3D(0, 0, 1);
            }

            normal.Normalize();
            return normal;
        }

        private static Point3D CalculateCentroid(IReadOnlyList<Point3D> points)
        {
            if (points.Count == 0)
                return new Point3D(0, 0, 0);

            return new Point3D(
                points.Average(point => point.X),
                points.Average(point => point.Y),
                points.Average(point => point.Z));
        }

        private static void CalculatePointCloudStats(
            IEnumerable<Point3D> points,
            out Point3D center,
            out Vector3D size,
            out int count)
        {
            var pointList = points.ToList();
            count = pointList.Count;
            if (pointList.Count == 0)
            {
                center = default;
                size = default;
                return;
            }

            var minX = pointList.Min(point => point.X);
            var maxX = pointList.Max(point => point.X);
            var minY = pointList.Min(point => point.Y);
            var maxY = pointList.Max(point => point.Y);
            var minZ = pointList.Min(point => point.Z);
            var maxZ = pointList.Max(point => point.Z);

            center = new Point3D(
                (minX + maxX) / 2.0,
                (minY + maxY) / 2.0,
                (minZ + maxZ) / 2.0);
            size = new Vector3D(maxX - minX, maxY - minY, maxZ - minZ);
        }

        private static string BuildLoadedStatus(WoundModelData model)
        {
            if (!model.HasUsableMarkerBindings)
            {
                return
                    $"Модель загружена: {model.VertexCount} вершин, {model.ModelMarkerCentersMm.Count} модельных маркеров. " +
                    "Заполните минимум 3 ArUco-ID в sidecar JSON. Масштаб: auto (ожидание привязки).";
            }

            return
                $"Модель загружена: {model.VertexCount} вершин, {model.TriangleCount} треугольников, " +
                $"связанных маркеров {model.MarkerBindingsByCameraId.Count}. Масштаб: auto (ожидание привязки).";
        }

        private readonly struct SimilarityTransform
        {
            private readonly double _scale;
            private readonly double[,] _rotation;
            private readonly Vector3D _translation;

            private SimilarityTransform(double scale, double[,] rotation, Vector3D translation)
            {
                _scale = scale;
                _rotation = rotation;
                _translation = translation;
            }

            public double Scale => _scale;
            public Vector3D Translation => _translation;

            /// <summary>Копия 3×3 вращения (строки × столбцы) для отладки.</summary>
            public double[,] RotationMatrixClone() => (double[,])_rotation.Clone();

            public static SimilarityTransform Identity { get; } = new SimilarityTransform(
                1.0,
                new[,]
                {
                    { 1.0, 0.0, 0.0 },
                    { 0.0, 1.0, 0.0 },
                    { 0.0, 0.0, 1.0 }
                },
                new Vector3D(0, 0, 0));

            public Vector3D TransformDirection(Vector3D direction)
            {
                var rotated = Rotate(direction, _rotation);
                if (rotated.Length < 1e-9)
                    return new Vector3D(0, 0, 1);

                rotated.Normalize();
                return rotated;
            }

            public SimilarityTransform WithScale(double scale)
            {
                var nextScale = scale;
                if (double.IsNaN(nextScale) || double.IsInfinity(nextScale) || Math.Abs(nextScale) < 1e-9)
                    nextScale = 1.0;

                return new SimilarityTransform(nextScale, _rotation, _translation);
            }

            public Point3D Transform(Point3D point)
            {
                var x = _scale * (_rotation[0, 0] * point.X + _rotation[0, 1] * point.Y + _rotation[0, 2] * point.Z) + _translation.X;
                var y = _scale * (_rotation[1, 0] * point.X + _rotation[1, 1] * point.Y + _rotation[1, 2] * point.Z) + _translation.Y;
                var z = _scale * (_rotation[2, 0] * point.X + _rotation[2, 1] * point.Y + _rotation[2, 2] * point.Z) + _translation.Z;
                return new Point3D(x, y, z);
            }

            public static SimilarityTransform Estimate(IReadOnlyList<Point3D> source, IReadOnlyList<Point3D> target)
            {
                if (source.Count != target.Count || source.Count < 3)
                    throw new ArgumentException("Для Procrustes-выравнивания нужны минимум 3 пары точек.");

                var sourceCentroid = Centroid(source);
                var targetCentroid = Centroid(target);
                var covariance = BuildCovariance(source, target, sourceCentroid, targetCentroid);
                var quaternion = EstimateRotationQuaternion(covariance);
                var rotation = QuaternionToMatrix(quaternion);
                var sourceVariance = 0.0;
                var scaleNumerator = 0.0;

                for (var i = 0; i < source.Count; i++)
                {
                    var sourceCentered = source[i] - sourceCentroid;
                    var targetCentered = target[i] - targetCentroid;
                    var rotated = Rotate(sourceCentered, rotation);
                    sourceVariance += Vector3D.DotProduct(sourceCentered, sourceCentered);
                    scaleNumerator += Vector3D.DotProduct(targetCentered, rotated);
                }

                var scale = sourceVariance > 1e-9 ? scaleNumerator / sourceVariance : 1.0;
                if (double.IsNaN(scale) || double.IsInfinity(scale) || Math.Abs(scale) < 1e-9)
                    scale = 1.0;
                else
                    scale = Math.Abs(scale);

                var rotatedCentroid = Rotate(sourceCentroid - new Point3D(0, 0, 0), rotation);
                var translation = targetCentroid - new Point3D(
                    rotatedCentroid.X * scale,
                    rotatedCentroid.Y * scale,
                    rotatedCentroid.Z * scale);

                return new SimilarityTransform(scale, rotation, translation);
            }

            private static Point3D Centroid(IReadOnlyList<Point3D> points)
            {
                var x = 0.0;
                var y = 0.0;
                var z = 0.0;
                foreach (var point in points)
                {
                    x += point.X;
                    y += point.Y;
                    z += point.Z;
                }

                return new Point3D(x / points.Count, y / points.Count, z / points.Count);
            }

            private static double[,] BuildCovariance(
                IReadOnlyList<Point3D> source,
                IReadOnlyList<Point3D> target,
                Point3D sourceCentroid,
                Point3D targetCentroid)
            {
                var covariance = new double[3, 3];
                for (var i = 0; i < source.Count; i++)
                {
                    var x = source[i] - sourceCentroid;
                    var y = target[i] - targetCentroid;

                    covariance[0, 0] += x.X * y.X;
                    covariance[0, 1] += x.X * y.Y;
                    covariance[0, 2] += x.X * y.Z;
                    covariance[1, 0] += x.Y * y.X;
                    covariance[1, 1] += x.Y * y.Y;
                    covariance[1, 2] += x.Y * y.Z;
                    covariance[2, 0] += x.Z * y.X;
                    covariance[2, 1] += x.Z * y.Y;
                    covariance[2, 2] += x.Z * y.Z;
                }

                return covariance;
            }

            private static double[] EstimateRotationQuaternion(double[,] s)
            {
                var n = new double[4, 4];
                var trace = s[0, 0] + s[1, 1] + s[2, 2];
                n[0, 0] = trace;
                n[0, 1] = s[1, 2] - s[2, 1];
                n[0, 2] = s[2, 0] - s[0, 2];
                n[0, 3] = s[0, 1] - s[1, 0];

                n[1, 0] = n[0, 1];
                n[1, 1] = s[0, 0] - s[1, 1] - s[2, 2];
                n[1, 2] = s[0, 1] + s[1, 0];
                n[1, 3] = s[2, 0] + s[0, 2];

                n[2, 0] = n[0, 2];
                n[2, 1] = n[1, 2];
                n[2, 2] = -s[0, 0] + s[1, 1] - s[2, 2];
                n[2, 3] = s[1, 2] + s[2, 1];

                n[3, 0] = n[0, 3];
                n[3, 1] = n[1, 3];
                n[3, 2] = n[2, 3];
                n[3, 3] = -s[0, 0] - s[1, 1] + s[2, 2];

                var q = new[] { 1.0, 0.0, 0.0, 0.0 };
                for (var iteration = 0; iteration < 32; iteration++)
                {
                    var next = new double[4];
                    for (var row = 0; row < 4; row++)
                    {
                        for (var col = 0; col < 4; col++)
                        {
                            next[row] += n[row, col] * q[col];
                        }
                    }

                    var length = Math.Sqrt(next[0] * next[0] + next[1] * next[1] + next[2] * next[2] + next[3] * next[3]);
                    if (length < 1e-12)
                        return q;

                    for (var i = 0; i < 4; i++)
                    {
                        q[i] = next[i] / length;
                    }
                }

                return q;
            }

            private static double[,] QuaternionToMatrix(double[] q)
            {
                var w = q[0];
                var x = q[1];
                var y = q[2];
                var z = q[3];

                return new[,]
                {
                    { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
                    { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
                    { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) }
                };
            }

            private static Vector3D Rotate(Vector3D vector, double[,] rotation)
            {
                return new Vector3D(
                    rotation[0, 0] * vector.X + rotation[0, 1] * vector.Y + rotation[0, 2] * vector.Z,
                    rotation[1, 0] * vector.X + rotation[1, 1] * vector.Y + rotation[1, 2] * vector.Z,
                    rotation[2, 0] * vector.X + rotation[2, 1] * vector.Y + rotation[2, 2] * vector.Z);
            }
        }

        private sealed record WoundPoseState(
            bool IsCaptured,
            bool FrameFrozen,
            string FreezeReason,
            double RigidRmseMm,
            double ResidualMaxMm,
            double ResidualP95Mm,
            IReadOnlyList<int> OutlierMarkerIds,
            Vector3D CurrentSurfaceNormalCamera1,
            double RigidScale)
        {
            public static WoundPoseState Empty { get; } = new WoundPoseState(
                IsCaptured: false,
                FrameFrozen: false,
                FreezeReason: "",
                RigidRmseMm: 0,
                ResidualMaxMm: 0,
                ResidualP95Mm: 0,
                OutlierMarkerIds: Array.Empty<int>(),
                CurrentSurfaceNormalCamera1: new Vector3D(0, 0, 1),
                RigidScale: 1.0);

            public static WoundPoseState Frozen(
                string reason,
                double rigidRmseMm = 0,
                Vector3D? currentSurfaceNormalCamera1 = null,
                double rigidScale = 1.0)
            {
                return new WoundPoseState(
                    IsCaptured: true,
                    FrameFrozen: true,
                    FreezeReason: reason,
                    RigidRmseMm: rigidRmseMm,
                    ResidualMaxMm: 0,
                    ResidualP95Mm: 0,
                    OutlierMarkerIds: Array.Empty<int>(),
                    CurrentSurfaceNormalCamera1: currentSurfaceNormalCamera1 ?? new Vector3D(0, 0, 1),
                    RigidScale: rigidScale);
            }
        }
    }

    public sealed class WoundModelLoadResult
    {
        public WoundModelLoadResult(
            string sourcePath,
            string sidecarPath,
            int vertexCount,
            int triangleCount,
            int modelMarkerCount,
            int linkedMarkerCount,
            IReadOnlyList<string> unmappedModelMarkers)
        {
            SourcePath = sourcePath;
            SidecarPath = sidecarPath;
            VertexCount = vertexCount;
            TriangleCount = triangleCount;
            ModelMarkerCount = modelMarkerCount;
            LinkedMarkerCount = linkedMarkerCount;
            UnmappedModelMarkers = unmappedModelMarkers;
        }

        public string SourcePath { get; }
        public string SidecarPath { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public int ModelMarkerCount { get; }
        public int LinkedMarkerCount { get; }
        public IReadOnlyList<string> UnmappedModelMarkers { get; }
    }
}
