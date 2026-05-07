using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Движок воспроизведения печати по уже спроецированной траектории.
    /// </summary>
    public sealed class PrintTrajectoryService
    {
        private const double MinMoveDurationSeconds = 1e-4;
        private const double DefaultFeedRateMmPerMinute = 1800.0;
        private const double Epsilon = 1e-9;

        private IReadOnlyList<GCodeMove> _moves = Array.Empty<GCodeMove>();
        private IReadOnlyList<GCodeMove> _extrusionMoves = Array.Empty<GCodeMove>();
        private readonly List<double> _moveStartTimes = new List<double>();
        private readonly List<double> _moveEndTimes = new List<double>();
        private readonly List<int> _moveToExtrusionIndex = new List<int>();
        private readonly List<double> _extrusionEndTimes = new List<double>();

        private double _currentTimeSeconds;
        private double _totalDurationSeconds;
        private double _speedMultiplier = 1.0;

        public bool IsRunning { get; private set; }
        public bool HasTrajectory => _moves.Count > 0;
        public double SpeedMultiplier => _speedMultiplier;
        public int MoveCount => _moves.Count;
        public int ExtrusionMoveCount => _extrusionMoves.Count;
        public double NormalizedProgress => _totalDurationSeconds <= Epsilon
            ? 0
            : Math.Max(0, Math.Min(1, _currentTimeSeconds / _totalDurationSeconds));

        public void LoadTrajectory(ProjectedPrintPath path)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            _moves = path.Moves;
            _extrusionMoves = path.ExtrusionMoves;
            _moveStartTimes.Clear();
            _moveEndTimes.Clear();
            _moveToExtrusionIndex.Clear();
            _extrusionEndTimes.Clear();

            double accumulatedTime = 0;
            int extrusionIndex = 0;
            for (int moveIndex = 0; moveIndex < _moves.Count; moveIndex++)
            {
                var move = _moves[moveIndex];
                var duration = GetMoveDurationSeconds(move);
                _moveStartTimes.Add(accumulatedTime);
                accumulatedTime += duration;
                _moveEndTimes.Add(accumulatedTime);

                if (move.IsExtrusion)
                {
                    _moveToExtrusionIndex.Add(extrusionIndex);
                    _extrusionEndTimes.Add(accumulatedTime);
                    extrusionIndex++;
                }
                else
                {
                    _moveToExtrusionIndex.Add(-1);
                }
            }

            _totalDurationSeconds = accumulatedTime;
            _currentTimeSeconds = 0;
            IsRunning = false;
        }

        public void SetSpeedMultiplier(double speedMultiplier)
        {
            _speedMultiplier = Math.Max(0.05, Math.Min(8.0, speedMultiplier));
        }

        public void Start()
        {
            if (!HasTrajectory)
                return;

            if (_currentTimeSeconds >= _totalDurationSeconds - Epsilon)
                _currentTimeSeconds = 0;

            IsRunning = true;
        }

        public void Pause()
        {
            IsRunning = false;
        }

        public void Stop()
        {
            IsRunning = false;
            _currentTimeSeconds = 0;
        }

        public void SeekNormalized(double normalizedProgress)
        {
            if (!HasTrajectory)
                return;

            var clamped = Math.Max(0, Math.Min(1, normalizedProgress));
            _currentTimeSeconds = _totalDurationSeconds * clamped;
        }

        public PrintPlaybackSnapshot Advance(double deltaSeconds)
        {
            if (IsRunning && HasTrajectory)
            {
                _currentTimeSeconds += Math.Max(0, deltaSeconds) * _speedMultiplier;
                if (_currentTimeSeconds >= _totalDurationSeconds - Epsilon)
                {
                    _currentTimeSeconds = _totalDurationSeconds;
                    IsRunning = false;
                }
            }

            return GetSnapshot();
        }

        public PrintPlaybackSnapshot GetSnapshot()
        {
            if (!HasTrajectory)
            {
                return new PrintPlaybackSnapshot(
                    new Point3D(0, 0, 0),
                    0,
                    -1,
                    0,
                    -1,
                    0,
                    true);
            }

            if (_totalDurationSeconds <= Epsilon)
            {
                var endPoint = _moves[^1].End;
                return new PrintPlaybackSnapshot(
                    endPoint,
                    1,
                    _moves.Count - 1,
                    _extrusionMoves.Count,
                    -1,
                    0,
                    true);
            }

            var moveIndex = FindMoveIndex(_currentTimeSeconds);
            var move = _moves[moveIndex];
            var moveStart = _moveStartTimes[moveIndex];
            var moveEnd = _moveEndTimes[moveIndex];
            var moveDuration = Math.Max(Epsilon, moveEnd - moveStart);
            var localProgress = Math.Max(0, Math.Min(1, (_currentTimeSeconds - moveStart) / moveDuration));

            var nozzlePosition = Lerp(move.Start, move.End, localProgress);
            var completedExtrusionCount = CountCompletedExtrusions(_currentTimeSeconds);
            var activeExtrusionIndex = -1;
            var activeExtrusionProgress = 0.0;

            var mappedExtrusionIndex = _moveToExtrusionIndex[moveIndex];
            if (mappedExtrusionIndex >= 0 &&
                mappedExtrusionIndex < _extrusionEndTimes.Count &&
                _currentTimeSeconds < _extrusionEndTimes[mappedExtrusionIndex] - Epsilon)
            {
                activeExtrusionIndex = mappedExtrusionIndex;
                activeExtrusionProgress = localProgress;
            }

            var isFinished = _currentTimeSeconds >= _totalDurationSeconds - Epsilon;
            return new PrintPlaybackSnapshot(
                nozzlePosition,
                NormalizedProgress,
                moveIndex,
                completedExtrusionCount,
                activeExtrusionIndex,
                activeExtrusionProgress,
                isFinished);
        }

        private int FindMoveIndex(double timeSeconds)
        {
            if (_moveEndTimes.Count == 0)
                return 0;

            if (timeSeconds <= 0)
                return 0;

            if (timeSeconds >= _totalDurationSeconds)
                return _moveEndTimes.Count - 1;

            var index = _moveEndTimes.BinarySearch(timeSeconds);
            if (index >= 0)
            {
                while (index > 0 && _moveEndTimes[index - 1] >= timeSeconds)
                    index--;
                return index;
            }

            index = ~index;
            return Math.Max(0, Math.Min(index, _moveEndTimes.Count - 1));
        }

        private int CountCompletedExtrusions(double timeSeconds)
        {
            if (_extrusionEndTimes.Count == 0)
                return 0;

            var index = _extrusionEndTimes.BinarySearch(timeSeconds);
            if (index >= 0)
            {
                while (index + 1 < _extrusionEndTimes.Count &&
                       _extrusionEndTimes[index + 1] <= timeSeconds + Epsilon)
                {
                    index++;
                }

                return index + 1;
            }

            index = ~index;
            return Math.Max(0, Math.Min(index, _extrusionEndTimes.Count));
        }

        private static double GetMoveDurationSeconds(GCodeMove move)
        {
            var feedRate = move.FeedRateMmPerMinute > Epsilon
                ? move.FeedRateMmPerMinute
                : DefaultFeedRateMmPerMinute;
            var mmPerSecond = Math.Max(Epsilon, feedRate / 60.0);
            var duration = move.LengthMm / mmPerSecond;
            return Math.Max(MinMoveDurationSeconds, duration);
        }

        private static Point3D Lerp(Point3D from, Point3D to, double alpha)
        {
            return new Point3D(
                from.X + (to.X - from.X) * alpha,
                from.Y + (to.Y - from.Y) * alpha,
                from.Z + (to.Z - from.Z) * alpha);
        }
    }
}
