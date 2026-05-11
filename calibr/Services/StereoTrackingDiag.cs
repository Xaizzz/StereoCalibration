using System;

namespace StereoCalibration.Services
{
    public enum TriangulationValidationFailureKind
    {
        None = 0,
        BadCoordinatesSanity = 1,
        DepthRange = 2,
        TemporalJump = 3
    }

    /// <summary>
    /// Снимок одного прохода стереотрекинга (последовательности кадров после старта камер).
    /// </summary>
    public sealed class StereoTrackingFrameObservation
    {
        public int StereoFrameSeq { get; init; }
        public DateTime TimestampUtc { get; init; }
        /// <summary>Были ли на кадре ненулевые списки ArUco (после детектирования).</summary>
        public bool HadRawDetections { get; init; }
        /// <summary>Размеры множества ID с каждой камеры.</summary>
        public int UniqueIdsCam1 { get; init; }
        public int UniqueIdsCam2 { get; init; }
        /// <summary>|ID₁ ∩ ID₂|, по уникальным идентификаторам.</summary>
        public int BilateralOverlapCount { get; init; }
        /// <summary>Число ID, видимых только в одной камере «лишней» суммой (|(S₁\S₂)|+|(S₂\S₁)|).</summary>
        public int UnpairedMarkerIdSlots { get; init; }
        /// <summary>Сколько стереопар одинакового ID были отвергнуты из‑за stale.</summary>
        public int StereoPairsRejectedStale { get; init; }
        /// <summary>Уникальных ID с обеими сторонами после фильтра stale (перед триангуляцией).</summary>
        public int StereoPairsEligibleForTriangulation { get; init; }
        public int StaleMarkersReportedCam1 { get; init; }
        public int StaleMarkersReportedCam2 { get; init; }

        /// <summary>Успешно принятые после всех фильтров 3D-центры.</summary>
        public int TriangulationAcceptedCount { get; init; }

        /// <summary>Разброс отказов <see cref="TriangulationValidationFailureKind"/> после геометрии.</summary>
        public int ValidationRejectCoordinates { get; init; }
        public int ValidationRejectDepth { get; init; }
        public int ValidationRejectJump { get; init; }

        /// <summary>Глубины Z успешных измерений, мм камеры 1.</summary>
        public double[]? AcceptedZMm { get; init; }

        /// <summary>Успешные ID за кадр (для оценки diversity по сеансу).</summary>
        public int[]? AcceptedMarkerIds { get; init; }

        /// <summary>Примитив‑суррогат «невязок»: сколько триангуляций не смогло завершиться в OpenCV-пути за кадр (четыре×углы).</summary>
        public int TriangulationSolveFailuresApprox { get; init; }
    }

    /// <summary>Приёмник телеметрии стереотрекинга; реализации не должны бросать наружу.</summary>
    public interface IStereoTrackingDiagSink
    {
        void BeginSession();
        void NotifyFrame(StereoTrackingFrameObservation observation);
        void EndSession();
    }

}
