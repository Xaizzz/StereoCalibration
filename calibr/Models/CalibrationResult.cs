namespace StereoCalibration.Models
{
    /// <summary>
    /// Результат стерео калибровки камер
    /// Инкапсулирует все параметры калибровки стерео системы
    /// </summary>
    public class CalibrationResult
    {
        /// <summary>
        /// Матрица внутренних параметров камеры 1 (K1)
        /// 3x3 матрица: [fx 0 cx; 0 fy cy; 0 0 1]
        /// </summary>
        public double[,] CameraMatrix1 { get; set; }

        /// <summary>
        /// Коэффициенты искажения камеры 1
        /// [k1, k2, p1, p2, k3] - радиальные и тангенциальные искажения
        /// </summary>
        public double[] DistCoeffs1 { get; set; }

        /// <summary>
        /// Матрица внутренних параметров камеры 2 (K2)
        /// 3x3 матрица: [fx 0 cx; 0 fy cy; 0 0 1]
        /// </summary>
        public double[,] CameraMatrix2 { get; set; }

        /// <summary>
        /// Коэффициенты искажения камеры 2
        /// [k1, k2, p1, p2, k3] - радиальные и тангенциальные искажения
        /// </summary>
        public double[] DistCoeffs2 { get; set; }

        /// <summary>
        /// Матрица поворота между камерами (R)
        /// 3x3 матрица вращения, описывающая ориентацию камеры 2 относительно камеры 1
        /// </summary>
        public double[,] R { get; set; }

        /// <summary>
        /// Вектор трансляции между камерами (T)
        /// 3D вектор [Tx, Ty, Tz], описывающий смещение камеры 2 относительно камеры 1
        /// </summary>
        public double[] T { get; set; }

        /// <summary>
        /// Существенная матрица (E)
        /// 3x3 матрица, кодирующая эпиполярную геометрию в калиброванных камерах
        /// </summary>
        public double[,] E { get; set; }

        /// <summary>
        /// Фундаментальная матрица (F)
        /// 3x3 матрица, кодирующая эпиполярную геометрию в некалиброванных камерах
        /// </summary>
        public double[,] F { get; set; }

        /// <summary>
        /// Средне-квадратичная переотображенческая ошибка стереокалибровки OpenCV
        /// <c>StereoCalibrate</c> (после монокалибровки, флаг FixIntrinsic). Величина в пикселях.
        /// </summary>
        public double Error { get; set; }

        /// <summary>
        /// RMS ошибка переотображения монокалибровки камеры 1 (<c>Cv2.CalibrateCamera</c>), пиксели.
        /// </summary>
        public double MeanReprojectionErrorMonoCamera1Px { get; set; }

        /// <summary>
        /// RMS переотображения монокалибровки камеры 2 (<c>Cv2.CalibrateCamera</c>), пиксели.
        /// </summary>
        public double MeanReprojectionErrorMonoCamera2Px { get; set; }

        /// <summary>
        /// Длина базовой линии (норма вектора трансляции T) между камерами; при калибровке в миллиметрах — миллиметры.
        /// </summary>
        public double BaselineNormMm { get; set; }

        /// <summary>
        /// Выборочное стандартное отклонение по парам кадров: для каждой пары считается
        /// отдельный RMS по наблюдаемым–спроектированным углам, затем <c>s</c> по этому набору (камера 1).
        /// </summary>
        public double StdDevMonoReprojectionRmsePerPairCamera1Px { get; set; }

        /// <summary>
        /// То же, что <see cref="StdDevMonoReprojectionRmsePerPairCamera1Px"/>, для камеры 2.
        /// </summary>
        public double StdDevMonoReprojectionRmsePerPairCamera2Px { get; set; }

        /// <summary>
        /// Количество использованных пар изображений для калибровки
        /// </summary>
        public int ImagePairsCount { get; set; }

        /// <summary>
        /// Дата и время проведения калибровки
        /// </summary>
        public System.DateTime CalibrationDate { get; set; }

        /// <summary>
        /// Конструктор по умолчанию
        /// </summary>
        public CalibrationResult()
        {
            CalibrationDate = System.DateTime.Now;
        }

        /// <summary>
        /// Проверка корректности данных калибровки
        /// </summary>
        /// <returns>True если все необходимые данные присутствуют</returns>
        public bool IsValid()
        {
            return CameraMatrix1 != null && CameraMatrix1.GetLength(0) == 3 && CameraMatrix1.GetLength(1) == 3 &&
                   CameraMatrix2 != null && CameraMatrix2.GetLength(0) == 3 && CameraMatrix2.GetLength(1) == 3 &&
                   DistCoeffs1 != null && DistCoeffs1.Length == 5 &&
                   DistCoeffs2 != null && DistCoeffs2.Length == 5 &&
                   R != null && R.GetLength(0) == 3 && R.GetLength(1) == 3 &&
                   T != null && T.Length == 3 &&
                   E != null && E.GetLength(0) == 3 && E.GetLength(1) == 3 &&
                   F != null && F.GetLength(0) == 3 && F.GetLength(1) == 3;
        }

        /// <summary>
        /// Получение качества калибровки в текстовом виде
        /// </summary>
        /// <returns>Описание качества калибровки</returns>
        public string GetQualityDescription()
        {
            if (Error < 0.5)
                return "Отличное";
            else if (Error < 1.0)
                return "Хорошее";
            else if (Error < 2.0)
                return "Удовлетворительное";
            else
                return "Плохое";
        }
    }
} 