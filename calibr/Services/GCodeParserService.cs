using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Парсер G-кода уровня fdm_standard.
    /// Поддерживает G0/G1, G90/G91, M82/M83, G92, G20/G21 и комментарии.
    /// </summary>
    public sealed class GCodeParserService
    {
        private const double Epsilon = 1e-6;
        private const double DefaultFeedRateMmPerMinute = 1800.0;

        private static readonly Regex CommandRegex = new Regex(
            @"([GM])\s*(-?\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ParameterRegex = new Regex(
            @"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ParenthesesCommentRegex = new Regex(
            @"\([^)]*\)",
            RegexOptions.Compiled);

        public ParsedGCodePath ParseFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Путь к G-коду не задан.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException("Файл G-кода не найден.", filePath);

            return ParseLines(File.ReadLines(filePath), filePath);
        }

        public ParsedGCodePath ParseLines(IEnumerable<string> lines, string sourcePath = "")
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            var moves = new List<GCodeMove>();
            var extrusionMoves = new List<GCodeMove>();

            bool absolutePositioning = true;
            bool absoluteExtruder = true;
            double unitScale = 1.0;

            double currentX = 0;
            double currentY = 0;
            double currentZ = 0;
            double currentE = 0;
            double currentFeedRate = DefaultFeedRateMmPerMinute;

            int lineNumber = 0;
            foreach (var rawLine in lines)
            {
                lineNumber++;
                var cleaned = StripComments(rawLine);
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;

                var upperLine = cleaned.ToUpperInvariant();
                if (!TryParseCommand(upperLine, out var commandLetter, out var commandCode))
                    continue;

                var parameters = ParseParameters(upperLine);

                if (commandLetter == 'G')
                {
                    switch (commandCode)
                    {
                        case 0:
                        case 1:
                        {
                            if (parameters.TryGetValue('F', out var feedValue))
                            {
                                var normalizedFeed = Math.Abs(feedValue) * unitScale;
                                if (normalizedFeed > Epsilon)
                                    currentFeedRate = normalizedFeed;
                            }

                            var start = new Point3D(currentX, currentY, currentZ);
                            var endX = ResolveAxis(parameters, 'X', currentX, absolutePositioning, unitScale);
                            var endY = ResolveAxis(parameters, 'Y', currentY, absolutePositioning, unitScale);
                            var endZ = ResolveAxis(parameters, 'Z', currentZ, absolutePositioning, unitScale);
                            var endE = ResolveAxis(parameters, 'E', currentE, absoluteExtruder, unitScale);

                            currentX = endX;
                            currentY = endY;
                            currentZ = endZ;

                            var end = new Point3D(endX, endY, endZ);
                            var hasMotion = Distance(start, end) > Epsilon;
                            var extrusionDelta = endE - currentE;
                            var isExtrusion = hasMotion && extrusionDelta > Epsilon;
                            var isTravel = hasMotion && !isExtrusion;
                            currentE = endE;

                            if (!hasMotion)
                                break;

                            var move = new GCodeMove(
                                start,
                                end,
                                isExtrusion,
                                isTravel,
                                currentFeedRate,
                                extrusionDelta,
                                lineNumber);

                            moves.Add(move);
                            if (move.IsExtrusion)
                                extrusionMoves.Add(move);
                            break;
                        }
                        case 20:
                            unitScale = 25.4;
                            break;
                        case 21:
                            unitScale = 1.0;
                            break;
                        case 90:
                            absolutePositioning = true;
                            break;
                        case 91:
                            absolutePositioning = false;
                            break;
                        case 92:
                            if (parameters.TryGetValue('X', out var setX))
                                currentX = setX * unitScale;
                            if (parameters.TryGetValue('Y', out var setY))
                                currentY = setY * unitScale;
                            if (parameters.TryGetValue('Z', out var setZ))
                                currentZ = setZ * unitScale;
                            if (parameters.TryGetValue('E', out var setE))
                                currentE = setE * unitScale;
                            break;
                    }
                }
                else if (commandLetter == 'M')
                {
                    switch (commandCode)
                    {
                        case 82:
                            absoluteExtruder = true;
                            break;
                        case 83:
                            absoluteExtruder = false;
                            break;
                    }
                }
            }

            var motionBounds = BuildBounds(moves);
            var extrusionBounds = BuildBounds(extrusionMoves);
            var minZ = moves.Count == 0 ? 0 : moves.Min(move => Math.Min(move.Start.Z, move.End.Z));
            var maxZ = moves.Count == 0 ? 0 : moves.Max(move => Math.Max(move.Start.Z, move.End.Z));

            return new ParsedGCodePath(
                moves,
                extrusionMoves,
                motionBounds,
                extrusionBounds,
                minZ,
                maxZ,
                sourcePath ?? string.Empty);
        }

        private static bool TryParseCommand(string line, out char commandLetter, out int commandCode)
        {
            var match = CommandRegex.Match(line);
            if (!match.Success)
            {
                commandLetter = default;
                commandCode = 0;
                return false;
            }

            commandLetter = char.ToUpperInvariant(match.Groups[1].Value[0]);
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out commandCode))
            {
                commandLetter = default;
                return false;
            }

            return true;
        }

        private static Dictionary<char, double> ParseParameters(string line)
        {
            var result = new Dictionary<char, double>();
            var matches = ParameterRegex.Matches(line);
            foreach (Match match in matches)
            {
                var letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
                if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                result[letter] = value;
            }

            return result;
        }

        private static double ResolveAxis(
            IReadOnlyDictionary<char, double> parameters,
            char axis,
            double currentValue,
            bool absoluteMode,
            double unitScale)
        {
            if (!parameters.TryGetValue(axis, out var rawValue))
                return currentValue;

            var value = rawValue * unitScale;
            return absoluteMode ? value : currentValue + value;
        }

        private static string StripComments(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return string.Empty;

            var withoutParentheses = ParenthesesCommentRegex.Replace(rawLine, string.Empty);
            var semicolonIndex = withoutParentheses.IndexOf(';');
            var line = semicolonIndex >= 0
                ? withoutParentheses.Substring(0, semicolonIndex)
                : withoutParentheses;

            return line.Trim();
        }

        private static PathBounds2D BuildBounds(IReadOnlyList<GCodeMove> moves)
        {
            if (moves.Count == 0)
                return new PathBounds2D(0, 0, 0, 0, false);

            var minX = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var minY = double.PositiveInfinity;
            var maxY = double.NegativeInfinity;

            foreach (var move in moves)
            {
                ExtendBounds(move.Start);
                ExtendBounds(move.End);
            }

            return new PathBounds2D(minX, maxX, minY, maxY, true);

            void ExtendBounds(Point3D point)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
