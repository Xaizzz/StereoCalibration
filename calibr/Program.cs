using StereoCalibration.Forms;
using StereoCalibration.Interfaces;
using StereoCalibration.Presenters;
using StereoCalibration.Services;
using System;
using System.Windows.Forms;

namespace StereoCalibration
{
    /// <summary>
    /// Единственная точка входа WinForms-приложения.
    /// 
    /// Здесь включаются стандартные настройки WinForms, показывается загрузочный
    /// экран и создаётся главное окно. Если пользователь отменил выбор камер
    /// во время старта, приложение завершает работу без открытия MainForm.
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var loadingForm = new LoadingForm())
            {
                loadingForm.Show();
                loadingForm.SetProgress(10, "Подготовка приложения...");

                var mainForm = new MainForm(loadingForm);
                if (mainForm.StartupCancelled)
                {
                    loadingForm.Close();
                    mainForm.Dispose();
                    return;
                }

                loadingForm.SetProgress(100, "Готово");
                loadingForm.Close();

                Application.Run(mainForm);
            }
        }
    }
}