using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StereoCalibration.Models;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using Newtonsoft.Json;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Сервис стереокалибровки камер по изображениям шахматной доски.
    /// 
    /// Основной сценарий: пользователь сохраняет пары изображений в папки
    /// `cam1/{folder}` и `cam2/{folder}`, сервис перечитывает эти файлы,
    /// ищет углы шахматной доски, выполняет индивидуальную калибровку каждой камеры,
    /// затем стереокалибровку и сохраняет результат в <see cref="CalibrationResult"/>.
    /// 
    /// Все физические размеры хранятся в миллиметрах, поэтому вектор T и будущие
    /// 3D-измерения также интерпретируются как миллиметры.
    /// </summary>
    public class StereoCalibrationService
    {
        #region Параметры калибровки
        /// <summary>Размер шахматной доски в углах (ширина x высота)</summary>
        private readonly OpenCvSharp.Size _patternSize;
        /// <summary>Физический размер квадрата шахматной доски в миллиметрах</summary>
        private readonly float _squareSize;
        #endregion
        
        #region Данные для калибровки
        /// <summary>Точки изображения для первой камеры</summary>
        private List<Mat> _pairImagePointsList1;
        /// <summary>Точки изображения для второй камеры</summary>
        private List<Mat> _pairImagePointsList2;
        /// <summary>3D координаты объектных точек</summary>
        private List<Mat> _pairObjectPointsList;
        #endregion
        
        public StereoCalibrationService(OpenCvSharp.Size patternSize, float squareSize)
        {
            _patternSize = patternSize;
            _squareSize = squareSize;
            
            _pairImagePointsList1 = new List<Mat>();
            _pairImagePointsList2 = new List<Mat>();
            _pairObjectPointsList = new List<Mat>();
        }
        
        /// <summary>
        /// Генерирует 3D-точки углов шахматной доски в её собственной плоскости.
        /// 
        /// Z всегда равен 0, потому что калибровочная доска считается плоской.
        /// X/Y задаются индексами углов, умноженными на физический размер клетки.
        /// </summary>
        public List<Point3f> GenerateObjectPoints()
        {
            var objectPoints = new List<Point3f>();
            for (int i = 0; i < _patternSize.Height; i++)
            {
                for (int j = 0; j < _patternSize.Width; j++)
                {
                    objectPoints.Add(new Point3f(j * _squareSize, i * _squareSize, 0));
                }
            }
            return objectPoints;
        }
        
        /// <summary>
        /// Выполняет полный цикл калибровки по уже сохранённым изображениям.
        /// 
        /// Метод специально работает с файлами, а не с кадрами из памяти: это
        /// позволяет повторить калибровку по одному и тому же набору изображений
        /// и проверить результат без повторного захвата с камер.
        /// </summary>
        public CalibrationResult CalibrateFromImages(string folder, out List<Point3f> ps3dAllOut)
        {
            ps3dAllOut = new List<Point3f>();
            
            _pairImagePointsList1 = new List<Mat>();
            _pairImagePointsList2 = new List<Mat>();
            _pairObjectPointsList = new List<Mat>();
            
            var mats1 = new List<Mat>();
            var mats2 = new List<Mat>();
            
            // Папки должны содержать синхронные пары изображений с одинаковыми
            // индексами. Минимум 10 пар выбран как практический порог качества
            // для устойчивой оценки внутренних и внешних параметров камер.
            var cam1Path = Path.Combine("cam1", folder);
            var cam2Path = Path.Combine("cam2", folder);
            
            if (!Directory.Exists(cam1Path) || !Directory.Exists(cam2Path))
            {
                throw new DirectoryNotFoundException($"Папки {cam1Path} или {cam2Path} не существуют");
            }
            
            var names1 = Directory.GetFiles(cam1Path);
            var names2 = Directory.GetFiles(cam2Path);
            
            if (names1.Length < 10 || names2.Length < 10 || names1.Length != names2.Length)
            {
                throw new InvalidOperationException("В каждой папке должно быть не менее 10 изображений, и их количество должно совпадать!");
            }
            
            // Загрузка изображений
            for (int i = 0; i < names1.Length; i++)
            {
                mats1.Add(new Mat(names1[i]));
                mats2.Add(new Mat(names2[i]));
            }
            
            // Обработка изображений для поиска углов шахматной доски
            ProcessChessboardImages(mats1, mats2);
            
            // Проверка количества пар
            if (_pairObjectPointsList.Count < 10 || _pairImagePointsList1.Count < 10 || _pairImagePointsList2.Count < 10 ||
                _pairObjectPointsList.Count != _pairImagePointsList1.Count || _pairObjectPointsList.Count != _pairImagePointsList2.Count)
            {
                throw new InvalidOperationException("Нужно не менее 10 пар изображений с обнаруженной шахматной доской!");
            }
            
            // Подготовка данных для калибровки
            var ps3dAll = new List<List<Point3f>>();
            var ps2dAll1 = new List<List<Point2f>>();
            var ps2dAll2 = new List<List<Point2f>>();
            
            ConvertMatListsToPointLists(ps3dAll, ps2dAll1, ps2dAll2);
            
            if (ps3dAll.Count > 0)
            {
                ps3dAllOut = ps3dAll[0];
            }
            
            // Выполнение стереокалибровки
            return PerformStereoCalibration(ps3dAll, ps2dAll1, ps2dAll2, mats1[0].Size());
        }
        
        /// <summary>
        /// Ищет шахматную доску на каждой паре изображений и сохраняет только те
        /// пары, где доска найдена одновременно в обеих камерах.
        /// 
        /// После грубого `FindChessboardCorners` применяется `CornerSubPix`, чтобы
        /// уточнить координаты углов до субпиксельной точности. Это напрямую влияет
        /// на ошибку калибровки.
        /// </summary>
        private void ProcessChessboardImages(List<Mat> mats1, List<Mat> mats2)
        {
            for (int j = 0; j < mats1.Count; j++)
            {
                Point2f[] corners1, corners2;
                bool found1 = Cv2.FindChessboardCorners(mats1[j], _patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(mats2[j], _patternSize, out corners2, ChessboardFlags.FastCheck);
                
                if (found1 && found2)
                {
                    using (Mat gray1 = new Mat())
                    using (Mat gray2 = new Mat())
                    {
                        Cv2.CvtColor(mats1[j], gray1, ColorConversionCodes.BGR2GRAY);
                        Cv2.CvtColor(mats2[j], gray2, ColorConversionCodes.BGR2GRAY);
                        
                        Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        
                        Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                        Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                        Point3f[] objPoints = GenerateObjectPoints().ToArray();
                        Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);
                        
                        for (int i = 0; i < corners1.Length; i++)
                        {
                            imagePoints1.Set(i, 0, corners1[i]);
                            imagePoints2.Set(i, 0, corners2[i]);
                        }
                        for (int i = 0; i < objPoints.Length; i++)
                        {
                            objectPoints.Set(i, 0, objPoints[i]);
                        }
                        
                        _pairImagePointsList1.Add(imagePoints1);
                        _pairImagePointsList2.Add(imagePoints2);
                        _pairObjectPointsList.Add(objectPoints);
                    }
                }
            }
        }
        
        /// <summary>
        /// Конвертирует внутренние Mat-таблицы точек в списки Point3f/Point2f,
        /// которые ожидают методы OpenCV `CalibrateCamera` и `StereoCalibrate`.
        /// </summary>
        private void ConvertMatListsToPointLists(List<List<Point3f>> ps3dAll, List<List<Point2f>> ps2dAll1, List<List<Point2f>> ps2dAll2)
        {
            for (int i = 0; i < _pairObjectPointsList.Count; i++)
            {
                var objMat = _pairObjectPointsList[i];
                var imgMat1 = _pairImagePointsList1[i];
                var imgMat2 = _pairImagePointsList2[i];
                var ps3d = new List<Point3f>();
                var ps2d1 = new List<Point2f>();
                var ps2d2 = new List<Point2f>();
                
                for (int j = 0; j < objMat.Rows; j++)
                {
                    var objPt = objMat.Get<Vec3f>(j, 0);
                    var imgPt1 = imgMat1.Get<Vec2f>(j, 0);
                    var imgPt2 = imgMat2.Get<Vec2f>(j, 0);
                    
                    ps3d.Add(new Point3f(objPt.Item0, objPt.Item1, objPt.Item2));
                    ps2d1.Add(new Point2f(imgPt1.Item0, imgPt1.Item1));
                    ps2d2.Add(new Point2f(imgPt2.Item0, imgPt2.Item1));
                }
                ps3dAll.Add(ps3d);
                ps2dAll1.Add(ps2d1);
                ps2dAll2.Add(ps2d2);
            }
        }
        
        /// <summary>
        /// Выполняет индивидуальную и стереокалибровку OpenCV.
        /// 
        /// Сначала оцениваются матрицы камер и коэффициенты дисторсии отдельно.
        /// Затем `StereoCalibrate` с флагом `FixIntrinsic` оценивает взаимное
        /// расположение камер: R, T, E и F. Эти данные дальше используются
        /// для триангуляции маркеров и построения 3D-сцены.
        /// </summary>
        private CalibrationResult PerformStereoCalibration(List<List<Point3f>> ps3dAll, List<List<Point2f>> ps2dAll1, 
            List<List<Point2f>> ps2dAll2, OpenCvSharp.Size imageSize)
        {
            // Калибровка камеры 1 (используем оригинальную логику)
            var cameraMatrix1 = new double[3, 3];
            var distCoeffs1 = new double[5];
            var cameraMatrix2 = new double[3, 3];
            var distCoeffs2 = new double[5];
            Mat R = new Mat();
            Mat T = new Mat();
            Mat E = new Mat();
            Mat F = new Mat();

            var tvecs1 = new Vec3d[ps3dAll.Count];
            var tvecs2 = new Vec3d[ps3dAll.Count];
            var rvecs1 = new Vec3d[ps3dAll.Count];
            var rvecs2 = new Vec3d[ps3dAll.Count];
            
            // Индивидуальная калибровка камер (точно как в оригинале)
            var err1 = Cv2.CalibrateCamera(ps3dAll, ps2dAll1, imageSize, cameraMatrix1, distCoeffs1, out rvecs1, out tvecs1, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 100, 1e-6));
            var err2 = Cv2.CalibrateCamera(ps3dAll, ps2dAll2, imageSize, cameraMatrix2, distCoeffs2, out rvecs2, out tvecs2, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 100, 1e-6));
            
            Debug.WriteLine("Ошибки индивидуальной калибровки: " + err1 + " " + err2);
            
            // Проверяем качество индивидуальной калибровки
            if (err1 > 1.0 || err2 > 1.0)
            {
                Debug.WriteLine($"Предупреждение: Высокая ошибка индивидуальной калибровки. Камера 1: {err1:F3}, Камера 2: {err2:F3}");
            }
            
            try
            {
                // Стереокалибровка с теми же флагами что в оригинале
                double error = Cv2.StereoCalibrate(
                    ps3dAll,
                    ps2dAll1,
                    ps2dAll2,
                    cameraMatrix1, 
                    distCoeffs1, 
                    cameraMatrix2, 
                    distCoeffs2,
                    imageSize,
                    R, 
                    T,
                    E, 
                    F,
                    CalibrationFlags.FixIntrinsic
                );

                // Преобразование Mat в массивы (точно как в оригинале)
                var result = new CalibrationResult
                {
                    CameraMatrix1 = cameraMatrix1,
                    DistCoeffs1 = distCoeffs1,
                    CameraMatrix2 = cameraMatrix2,
                    DistCoeffs2 = distCoeffs2,
                    R = MatToArray2D(R),
                    T = MatToArray1D(T),
                    E = MatToArray2D(E),
                    F = MatToArray2D(F),
                    Error = error
                };

                // Дополнительная диагностика
                FillCalibrationDiagnostics(ps3dAll, ps2dAll1, ps2dAll2, err1, err2,
                    cameraMatrix1, distCoeffs1, cameraMatrix2, distCoeffs2,
                    rvecs1, tvecs1, rvecs2, tvecs2,
                    result);

                Debug.WriteLine($"Стереокалибровка завершена. Stereo RMS (px): {error:F6}; mono RMS: {result.MeanReprojectionErrorMonoCamera1Px:F4}, {result.MeanReprojectionErrorMonoCamera2Px:F4}; baseline: {result.BaselineNormMm:F2} мм");
                Debug.WriteLine($"Sigma per-pair mono RMS — cam1 {result.StdDevMonoReprojectionRmsePerPairCamera1Px:F4}, cam2 {result.StdDevMonoReprojectionRmsePerPairCamera2Px:F4} px (n={result.ImagePairsCount} пар)");

                return result;
            }
            catch (OpenCvSharp.OpenCVException ex)
            {
                Debug.WriteLine($"Ошибка стереокалибровки: {ex.Message}");
                throw;
            }
        }

        private static void FillCalibrationDiagnostics(
            IReadOnlyList<List<Point3f>> ps3dAll,
            IReadOnlyList<List<Point2f>> ps2dAll1,
            IReadOnlyList<List<Point2f>> ps2dAll2,
            double err1Px,
            double err2Px,
            double[,] cameraMatrix1,
            double[] distCoeffs1,
            double[,] cameraMatrix2,
            double[] distCoeffs2,
            IReadOnlyList<Vec3d> rvecs1,
            IReadOnlyList<Vec3d> tvecs1,
            IReadOnlyList<Vec3d> rvecs2,
            IReadOnlyList<Vec3d> tvecs2,
            CalibrationResult destination)
        {
            destination.ImagePairsCount = ps3dAll.Count;
            destination.MeanReprojectionErrorMonoCamera1Px = err1Px;
            destination.MeanReprojectionErrorMonoCamera2Px = err2Px;
            destination.CalibrationDate = DateTime.UtcNow;
            destination.BaselineNormMm = BaselineMmFromTranslation(destination.T);

            var perRmseCam1 = new List<double>(ps3dAll.Count);
            var perRmseCam2 = new List<double>(ps3dAll.Count);

            using var cm1Mat = CameraMatrixToMat(cameraMatrix1);
            using var cm2Mat = CameraMatrixToMat(cameraMatrix2);
            using var d1Mat = DistCoeffsToMat(distCoeffs1);
            using var d2Mat = DistCoeffsToMat(distCoeffs2);

            for (var i = 0; i < ps3dAll.Count; i++)
            {
                perRmseCam1.Add(ComputeViewMonoRmsePx(
                    ps3dAll[i],
                    ps2dAll1[i],
                    cm1Mat,
                    d1Mat,
                    rvecs1[i],
                    tvecs1[i]));

                perRmseCam2.Add(ComputeViewMonoRmsePx(
                    ps3dAll[i],
                    ps2dAll2[i],
                    cm2Mat,
                    d2Mat,
                    rvecs2[i],
                    tvecs2[i]));
            }

            destination.StdDevMonoReprojectionRmsePerPairCamera1Px = SampleStdDeviation(perRmseCam1);
            destination.StdDevMonoReprojectionRmsePerPairCamera2Px = SampleStdDeviation(perRmseCam2);
        }

        /// <remarks>Стандартное отклонение выборки; при числе наблюдений &lt; 2 возвращает 0.</remarks>
        private static double SampleStdDeviation(IReadOnlyCollection<double> values)
        {
            if (values == null || values.Count == 0)
                return 0;

            var v = values.Where(static x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            if (v.Count < 2)
                return 0;

            var mean = v.Average();
            var sumSq = v.Sum(x =>
            {
                var d = x - mean;
                return d * d;
            });
            return Math.Sqrt(sumSq / (v.Count - 1));
        }

        private static double BaselineMmFromTranslation(double[]? tMm)
        {
            if (tMm == null || tMm.Length < 3)
                return 0;
            var vx = tMm[0];
            var vy = tMm[1];
            var vz = tMm[2];
            return Math.Sqrt(vx * vx + vy * vy + vz * vz);
        }

        private static Mat CameraMatrixToMat(double[,] matrix)
        {
            var m = new Mat(3, 3, MatType.CV_64FC1);
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                    m.At<double>(r, c) = matrix[r, c];
            }
            return m;
        }

        private static Mat DistCoeffsToMat(double[] coeffs)
        {
            var n = coeffs?.Length ?? 0;
            var m = new Mat(n <= 0 ? 1 : n, 1, MatType.CV_64FC1);
            for (var i = 0; i < n; i++)
                m.At<double>(i, 0) = coeffs![i];
            return m;
        }

        private static Mat RodriguesVec3ToColumnMat(Vec3d rvec)
        {
            var m = new Mat(3, 1, MatType.CV_64FC1);
            m.At<double>(0, 0) = rvec.Item0;
            m.At<double>(1, 0) = rvec.Item1;
            m.At<double>(2, 0) = rvec.Item2;
            return m;
        }

        private static Mat TranslationVec3ToColumnMat(Vec3d tvec)
        {
            var m = new Mat(3, 1, MatType.CV_64FC1);
            m.At<double>(0, 0) = tvec.Item0;
            m.At<double>(1, 0) = tvec.Item1;
            m.At<double>(2, 0) = tvec.Item2;
            return m;
        }

        private static bool TryReadProjectedPixel(Mat projected, int rowIndex, out Point2d p)
        {
            p = default;
            var t = projected.Type();
            if (t == MatType.CV_64FC2)
            {
                var v = projected.At<Vec2d>(rowIndex, 0);
                p = new Point2d(v.Item0, v.Item1);
                return true;
            }

            if (t == MatType.CV_32FC2)
            {
                var v = projected.At<Vec2f>(rowIndex, 0);
                p = new Point2d(v.Item0, v.Item1);
                return true;
            }

            if (projected.Cols >= 2 && projected.Channels() == 1)
            {
                p = new Point2d(projected.At<double>(rowIndex, 0), projected.At<double>(rowIndex, 1));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Одна доска-пара: RMS по наблюдаемым и заново спроектированным пиксельным координатам.
        /// </summary>
        private static double ComputeViewMonoRmsePx(
            IReadOnlyList<Point3f> objectMm,
            IReadOnlyList<Point2f> imagePxObserved,
            Mat cameraMatrix,
            Mat distCoeffs,
            Vec3d rvec,
            Vec3d tvec)
        {
            using var projected = new Mat();
            using var objectMat = Mat.FromArray(objectMm.ToArray());
            using var rotationMat = RodriguesVec3ToColumnMat(rvec);
            using var translationMat = TranslationVec3ToColumnMat(tvec);

            Cv2.ProjectPoints(objectMat, rotationMat, translationMat, cameraMatrix, distCoeffs, projected);

            var n = objectMm.Count;
            if (imagePxObserved.Count != n || n == 0)
                return double.NaN;

            if (projected.Rows != n)
                return double.NaN;

            double sse = 0;
            for (var j = 0; j < n; j++)
            {
                if (!TryReadProjectedPixel(projected, j, out var pred))
                    return double.NaN;

                var o = imagePxObserved[j];
                var dx = pred.X - o.X;
                var dy = pred.Y - o.Y;
                sse += dx * dx + dy * dy;
            }

            return Math.Sqrt(sse / n);
        }
        
        /// <summary>Копирует OpenCV Mat в двумерный массив, пригодный для JSON-сериализации.</summary>
        private double[,] MatToArray2D(Mat mat)
        {
            var array = new double[mat.Rows, mat.Cols];
            for (int i = 0; i < mat.Rows; i++)
            {
                for (int j = 0; j < mat.Cols; j++)
                {
                    array[i, j] = mat.At<double>(i, j);
                }
            }
            return array;
        }
        
        /// <summary>Копирует OpenCV Mat-вектор в одномерный массив для JSON-сериализации.</summary>
        private double[] MatToArray1D(Mat mat)
        {
            var array = new double[Math.Max(mat.Rows, mat.Cols)];
            if (mat.Rows > mat.Cols)
            {
                for (int i = 0; i < mat.Rows; i++)
                {
                    array[i] = mat.At<double>(i, 0);
                }
            }
            else
            {
                for (int i = 0; i < mat.Cols; i++)
                {
                    array[i] = mat.At<double>(0, i);
                }
            }
            return array;
        }
        
        /// <summary>
        /// Сохранение результатов калибровки в файл
        /// </summary>
        public void SaveCalibrationResult(CalibrationResult result, string filename)
        {
            var json = JsonConvert.SerializeObject(result, Formatting.Indented);
            File.WriteAllText(filename, json);
        }
        
        /// <summary>
        /// Загрузка результатов калибровки из файла
        /// </summary>
        public CalibrationResult LoadCalibrationResult(string filename)
        {
            if (!File.Exists(filename))
                return null;
            
            var json = File.ReadAllText(filename);
            return JsonConvert.DeserializeObject<CalibrationResult>(json);
        }
    }
}