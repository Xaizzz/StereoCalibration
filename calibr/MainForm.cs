using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using OpenCvSharp.Internal.Vectors;
using System.Data;

namespace StereoCalibration
{
    public partial class MainForm : Form
    {
        private VideoCapture capture1, capture2;
        private Mat frame1, frame2;
        private bool isRunning;
        private List<Mat> pairImagePointsList1, pairImagePointsList2;
        private List<Mat> pairObjectPointsList;
        private OpenCvSharp.Size patternSize = new OpenCvSharp.Size(9, 6); // Размер шахматной доски (10x7)
        private const float squareSize = 9f; // Размер квадрата на доске в мм

        private System.Windows.Forms.Button StartButton;
        private System.Windows.Forms.Button CapturePairButton;
        private System.Windows.Forms.Button StereoCalibrateButton;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.PictureBox pictureBox2;

        private System.Windows.Forms.PictureBox pictureBox1a;
        private System.Windows.Forms.PictureBox pictureBox2a;
        string cur_folder = "0204_1a";
        public MainForm()
        {
            InitializeComponent();
            InitializeCamera();
            pairImagePointsList1 = new List<Mat>();
            pairImagePointsList2 = new List<Mat>();
            pairObjectPointsList = new List<Mat>();
            isRunning = false;
        }

        private void InitializeComponent()
        {
            StartButton = new Button();
            CapturePairButton = new Button();
            StereoCalibrateButton = new Button();

            pictureBox1 = new PictureBox();
            pictureBox2 = new PictureBox();

            pictureBox1a = new PictureBox();
            pictureBox2a = new PictureBox();

            StartButton.Text = "Начать";
            CapturePairButton.Text = "Сохранить пару";
            StereoCalibrateButton.Text = "Стереокалибровка";

            StartButton.Location = new System.Drawing.Point(10, 10);
            StartButton.Size = new System.Drawing.Size(100, 30);
            CapturePairButton.Location = new System.Drawing.Point(120, 10);
            CapturePairButton.Size = new System.Drawing.Size(100, 30);
            StereoCalibrateButton.Location = new System.Drawing.Point(230, 10);
            StereoCalibrateButton.Size = new System.Drawing.Size(100, 30);

            pictureBox1.Location = new System.Drawing.Point(10, 50);
            pictureBox1.Size = new System.Drawing.Size(640, 480);
            pictureBox2.Location = new System.Drawing.Point(660, 50);
            pictureBox2.Size = new System.Drawing.Size(640, 480);

            pictureBox1a.Location = new System.Drawing.Point(10, 540);
            pictureBox1a.Size = new System.Drawing.Size(640, 480);
            pictureBox2a.Location = new System.Drawing.Point(660, 540);
            pictureBox2a.Size = new System.Drawing.Size(640, 480);

            Controls.Add(StartButton);
            Controls.Add(CapturePairButton);
            Controls.Add(StereoCalibrateButton);
            Controls.Add(pictureBox1);
            Controls.Add(pictureBox2);

            StartButton.Click += StartButton_Click;
            CapturePairButton.Click += CapturePairButton_Click;
            StereoCalibrateButton.Click += StereoCalibrateButton_Click;

            this.ClientSize = new System.Drawing.Size(1310, 1040);
            this.Text = "Stereo Calibration";
        }

        private List<Point3f> GenerateObjectPoints()
        {
            List<Point3f> points = new List<Point3f>();
            for (int y = 0; y < patternSize.Height; y++)
            {
                for (int x = 0; x < patternSize.Width; x++)
                {
                    points.Add(new Point3f(x * squareSize, y * squareSize, 0f));
                }
            }
            return points;
        }

        private void InitializeCamera()
        {
            capture1 = new VideoCapture(0); // Первая камера
            capture2 = new VideoCapture(1); // Вторая камера
            if (!capture1.IsOpened())
            {
                MessageBox.Show("Не удалось открыть камеру 1 (индекс 1). Проверьте подключение или индекс.");
                return;
            }
            if (!capture2.IsOpened())
            {
                MessageBox.Show("Не удалось открыть камеру 2 (индекс 2). Проверьте подключение или индекс.");
                return;
            }
            capture1.Set(VideoCaptureProperties.FrameWidth, 640);
            capture1.Set(VideoCaptureProperties.FrameHeight, 480);
            capture2.Set(VideoCaptureProperties.FrameWidth, 640);
            capture2.Set(VideoCaptureProperties.FrameHeight, 480);
            frame1 = new Mat();
            frame2 = new Mat();
            Directory.CreateDirectory("cam1\\" + cur_folder + "\\");
            Directory.CreateDirectory("cam2\\" + cur_folder + "\\");
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                isRunning = true;
                StartButton.Text = "Остановить";
                Application.Idle += ProcessFrame;
            }
            else
            {
                isRunning = false;
                StartButton.Text = "Начать";
                Application.Idle -= ProcessFrame;
            }
        }

        private void ProcessFrame(object sender, EventArgs e)
        {
            capture1.Retrieve(frame1);
            capture2.Retrieve(frame2);

            if (frame1 != null && frame2 != null)
            {
                var fr1 = new Mat();
                var fr2 = new Mat();
                fr1 = frame1.Clone();
                fr2 = frame2.Clone();
                Point2f[] corners1, corners2;
                bool found1 = Cv2.FindChessboardCorners(frame1, patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(frame2, patternSize, out corners2, ChessboardFlags.FastCheck);

                if (found1)
                {
                    using (Mat gray1 = new Mat())
                    {
                        Cv2.CvtColor(frame1, gray1, ColorConversionCodes.BGR2GRAY);
                        Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                        Cv2.DrawChessboardCorners(fr1, patternSize, corners1, found1);

                    }
                }
                if (found2)
                {
                    using (Mat gray2 = new Mat())
                    {
                        Cv2.CvtColor(frame2, gray2, ColorConversionCodes.BGR2GRAY);
                        Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                        Cv2.DrawChessboardCorners(fr2, patternSize, corners2, found2);
                    }
                }

                {
                    if (pictureBox1.InvokeRequired)
                    {
                        pictureBox1.Invoke(new Action(() => pictureBox1.Image = BitmapConverter.ToBitmap(fr1)));
                    }
                    else
                    {
                        pictureBox1.Image = BitmapConverter.ToBitmap(fr1);
                    }
                }

                {
                    if (pictureBox2.InvokeRequired)
                    {
                        pictureBox2.Invoke(new Action(() => pictureBox2.Image = BitmapConverter.ToBitmap(fr2)));
                    }
                    else
                    {
                        pictureBox2.Image = BitmapConverter.ToBitmap(fr2);
                    }
                }
            }
            else
            {
                MessageBox.Show("Не удалось прочитать кадры с камер. Проверьте подключение.");
            }
        }

        private void CapturePairButton_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                MessageBox.Show("Сначала начните видеопоток.");
                return;
            }

            Mat snapshot1 = frame1.Clone();
            Mat snapshot2 = frame2.Clone();



            Point2f[] corners1, corners2;
            bool found1 = Cv2.FindChessboardCorners(snapshot1, patternSize, out corners1, ChessboardFlags.FastCheck);
            bool found2 = Cv2.FindChessboardCorners(snapshot2, patternSize, out corners2, ChessboardFlags.FastCheck);

            frame1.SaveImage("cam1\\" + cur_folder + "\\" + pairImagePointsList1.Count + ".png");
            frame2.SaveImage("cam2\\" + cur_folder + "\\" + pairImagePointsList1.Count + ".png");
            if (found1 && found2)
            {
                using (Mat gray1 = new Mat())
                using (Mat gray2 = new Mat())
                {
                    Cv2.CvtColor(snapshot1, gray1, ColorConversionCodes.BGR2GRAY);
                    Cv2.CvtColor(snapshot2, gray2, ColorConversionCodes.BGR2GRAY);

                    Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                    Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                    // Создание матриц с явным указанием типа и размера
                    Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                    Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                    Point3f[] objPoints = GenerateObjectPoints().ToArray();
                    Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);

                    // Заполнение матриц данными
                    for (int i = 0; i < corners1.Length; i++)
                    {
                        imagePoints1.Set(i, 0, corners1[i]);
                        imagePoints2.Set(i, 0, corners2[i]);
                    }
                    for (int i = 0; i < objPoints.Length; i++)
                    {
                        objectPoints.Set(i, 0, objPoints[i]);
                    }

                    pairImagePointsList1.Add(imagePoints1);
                    pairImagePointsList2.Add(imagePoints2);
                    pairObjectPointsList.Add(objectPoints);


                    MessageBox.Show($"Парный снимок {pairImagePointsList1.Count} сохранен.");
                }
            }
            else
            {
                MessageBox.Show("Шахматная доска не обнаружена на одном из снимков.");
            }
        }

        private void StereoCalibrateButton_Click(object sender, EventArgs e)
        {
            pairImagePointsList1 = new List<Mat>();
            pairImagePointsList2 = new List<Mat>();
            pairObjectPointsList = new List<Mat>();

            var mats1 = new List<Mat>();
            var mats2 = new List<Mat>();

            var names1 = Directory.GetFiles("cam1\\" + cur_folder);
            var names2 = Directory.GetFiles("cam2\\" + cur_folder);

            for (int i = 0; i < names1.Length; i++)
            {
                mats1.Add(new Mat(names1[i]));
                mats2.Add(new Mat(names2[i]));
            }

            for (int j = 0; j < mats1.Count; j++)
            {
                Point2f[] corners1, corners2;


                bool found1 = Cv2.FindChessboardCorners(mats1[j], patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(mats2[j], patternSize, out corners2, ChessboardFlags.FastCheck);
                using (Mat gray1 = new Mat())
                using (Mat gray2 = new Mat())
                {
                    Cv2.CvtColor(mats1[j], gray1, ColorConversionCodes.BGR2GRAY);
                    Cv2.CvtColor(mats2[j], gray2, ColorConversionCodes.BGR2GRAY);

                    Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                    Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                    // Создание матриц с явным указанием типа и размера
                    Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                    Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                    Point3f[] objPoints = GenerateObjectPoints().ToArray();
                    Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);

                    // Заполнение матриц данными
                    for (int i = 0; i < corners1.Length; i++)
                    {
                        imagePoints1.Set(i, 0, corners1[i]);
                        imagePoints2.Set(i, 0, corners2[i]);
                    }
                    for (int i = 0; i < objPoints.Length; i++)
                    {
                        objectPoints.Set(i, 0, objPoints[i]);
                    }

                    Cv2.DrawChessboardCorners(gray1, patternSize, corners1, found1);
                    Cv2.DrawChessboardCorners(gray2, patternSize, corners2, found2);
                    Cv2.ImShow("asd", gray1);
                    Cv2.ImShow("asd2", gray2);
                    //Cv2.WaitKey();

                    pairImagePointsList1.Add(imagePoints1);
                    pairImagePointsList2.Add(imagePoints2);
                    pairObjectPointsList.Add(objectPoints);




                    // MessageBox.Show($"Парный снимок {pairImagePointsList1.Count} сохранен.");
                }
            }
            // Проверка количества снимков
            if (pairObjectPointsList.Count != 10 && pairImagePointsList1.Count != 10 && pairImagePointsList2.Count != 10)
            {
                MessageBox.Show("Нужно 10 снимков!");
                return;
            }
            var ps_3d_all = new List<List<Point3f>>();
            var ps_2d_all_1 = new List<List<Point2f>>();
            var ps_2d_all_2 = new List<List<Point2f>>();
            // Вывод информации о точках
            for (int i = 0; i < pairObjectPointsList.Count; i++)
            {
                var objMat = pairObjectPointsList[i];
                var imgMat1 = pairImagePointsList1[i];
                var imgMat2 = pairImagePointsList2[i];
                Debug.WriteLine($"Снимок {i + 1}:");
                Debug.WriteLine($"objectPoints: Type={objMat.Type()}, Rows={objMat.Rows}, Cols={objMat.Cols}");
                Debug.WriteLine($"imagePoints1: Type={imgMat1.Type()}, Rows={imgMat1.Rows}, Cols={imgMat1.Cols}");
                Debug.WriteLine($"imagePoints2: Type={imgMat2.Type()}, Rows={imgMat2.Rows}, Cols={imgMat2.Cols}");
                var ps_3d = new List<Point3f>();
                var ps_2d_1 = new List<Point2f>();
                var ps_2d_2 = new List<Point2f>();
                // Проверка первых трёх точек
                for (int j = 0; j < objMat.Rows; j++)
                {
                    var objPt = objMat.Get<Vec3f>(j, 0); // 3D-точка

                    var imgPt1 = imgMat1.Get<Vec2f>(j, 0); // 2D-точка камеры 1
                    var imgPt2 = imgMat2.Get<Vec2f>(j, 0); // 2D-точка камеры 2

                    ps_3d.Add(new Point3f(objPt.Item0, objPt.Item1, objPt.Item2));

                    ps_2d_1.Add(new Point2f(imgPt1.Item0, imgPt1.Item1));
                    ps_2d_2.Add(new Point2f(imgPt2.Item0, imgPt2.Item1));

                    //Debug.WriteLine($"  Точка {   j}: object=({objPt.Item0}, {objPt.Item1}, {objPt.Item2}), " +  $"img1=({imgPt1.Item0}, {imgPt1.Item1}), img2=({imgPt2.Item0}, {imgPt2.Item1})");

                }
                ps_3d_all.Add(ps_3d);
                ps_2d_all_1.Add(ps_2d_1);
                ps_2d_all_2.Add(ps_2d_2);
            }

            // Выполнение стереокалибровки
            var cameraMatrix1 = new double[3, 3];
            var distCoeffs1 = new double[5];
            var cameraMatrix2 = new double[3, 3];
            var distCoeffs2 = new double[5];
            Mat R = new Mat();
            Mat T = new Mat();
            Mat E = new Mat();
            Mat F = new Mat();
            var tvecs1 = new Vec3d[10];
            var tvecs2 = new Vec3d[10];
            var rvecs1 = new Vec3d[10];
            var rvecs2 = new Vec3d[10];
            //print_mat(pairObjectPointsList[0]);
            //print_mat(pairImagePointsList1[0]);
            /* var cameraMatrix1_double =(double[,]) to_double(cameraMatrix1);
             var cameraMatrix2_double = (double[,])to_double(cameraMatrix2);


             var distCoeffs1_double = (double[])to_double(distCoeffs1);
             var distCoeffs2_double = (double[])to_double(distCoeffs2);*/

            var err1 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_1, new OpenCvSharp.Size(640, 480), cameraMatrix1, distCoeffs1, out rvecs1, out tvecs1, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Count, 100, 0.1));
            var err2 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_2, new OpenCvSharp.Size(640, 480), cameraMatrix2, distCoeffs2, out rvecs2, out tvecs2, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Count, 100, 0.1));
            print_double(cameraMatrix1);
            print_double(distCoeffs1);
            print_double(cameraMatrix2);
            print_double(distCoeffs2);
            Console.WriteLine("errors" + err1 + " " + err2);

            //try
            {
                double error = Cv2.StereoCalibrate(
                    ps_3d_all,
                    ps_2d_all_1,
                    ps_2d_all_2,
                    cameraMatrix1, distCoeffs1, cameraMatrix2, distCoeffs2,
                     new OpenCvSharp.Size(640, 480), R, T, E, F,
                    CalibrationFlags.RationalModel
                );

                print_mat(R);
                print_mat(T);
                print_mat(E);
                print_mat(F);
                // MessageBox.Show($"Калибровка успешна! Ошибка: {error}");
            }
            //catch (OpenCvSharp.OpenCVException ex)
            {
                // MessageBox.Show($"Ошибка стереокалибровки: {ex.Message}");
            }
        }

        void print_mat(Mat mat)
        {
            Console.WriteLine(mat.Rows + " " + mat.Cols);
            for (var rowIndex = 0; rowIndex < mat.Rows; rowIndex++)
            {
                for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                {
                    Console.Write($"{mat.At<double>(rowIndex, colIndex)} ");
                }
                Console.WriteLine("");
            }
        }
        void print_double(double[,] mat)
        {
            Console.WriteLine(mat.GetLength(0) + " " + mat.GetLength(1));
            for (var rowIndex = 0; rowIndex < mat.GetLength(0); rowIndex++)
            {
                for (var colIndex = 0; colIndex < mat.GetLength(1); colIndex++)
                {
                    Console.Write(Math.Round(mat[rowIndex, colIndex], 4) + " ");
                }
                Console.WriteLine("");
            }
        }

        void print_double(double[] mat)
        {
            Console.WriteLine(mat.GetLength(0));
            for (var rowIndex = 0; rowIndex < mat.GetLength(0); rowIndex++)
            {
                Console.Write(Math.Round(mat[rowIndex], 4) + " ");
            }
            Console.WriteLine("");
        }
        object to_double(Mat mat)
        {

            if (mat.Rows == 1)
            {
                Console.WriteLine(mat.Rows + " " + mat.Cols);
                var data = new double[mat.Cols];
                for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                {
                    data[colIndex] = mat.At<double>(0, colIndex);
                }
                return (object)data;
            }
            else
            {
                Console.WriteLine(mat.Rows + " " + mat.Cols);
                var data = new double[mat.Rows, mat.Cols];

                for (var rowIndex = 0; rowIndex < mat.Rows; rowIndex++)
                {
                    for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                    {
                        data[rowIndex, colIndex] = mat.At<double>(0, colIndex);
                    }
                }
                return (object)data;
            }


        }
    }
}