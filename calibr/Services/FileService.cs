using OpenCvSharp;
using StereoCalibration.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Реализация сервиса файловых операций
    /// Инкапсулирует всю логику работы с файловой системой
    /// </summary>
    public class FileService : IFileService
    {
        /// <summary>
        /// Сохранение изображения в файл
        /// </summary>
        public async Task<bool> SaveImageAsync(Mat image, string filePath)
        {
            if (image == null || image.Empty() || string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                return await Task.Run(() =>
                {
                    // Создаем директорию если её нет
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Сохраняем изображение
                    return image.SaveImage(filePath);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения изображения {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Загрузка изображения из файла
        /// </summary>
        public async Task<Mat> LoadImageAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                return await Task.Run(() =>
                {
                    var image = Cv2.ImRead(filePath, ImreadModes.Color);
                    return image.Empty() ? null : image;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки изображения {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Создание директории если её нет
        /// </summary>
        public void CreateDirectoryIfNotExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания директории {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Открытие папки в проводнике
        /// </summary>
        public void OpenDirectoryInExplorer(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    Process.Start("explorer.exe", fullPath);
                }
                else
                {
                    Debug.WriteLine($"Директория не найдена: {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка открытия директории {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверка существования файла
        /// </summary>
        public bool FileExists(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                return File.Exists(filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка проверки файла {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Проверка существования директории
        /// </summary>
        public bool DirectoryExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                return false;

            try
            {
                return Directory.Exists(directoryPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка проверки директории {directoryPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Получение безопасного имени файла
        /// </summary>
        public string GetSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "image";

            try
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                foreach (var invalidChar in invalidChars)
                {
                    fileName = fileName.Replace(invalidChar, '_');
                }
                return fileName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обработки имени файла: {ex.Message}");
                return "image";
            }
        }

        /// <summary>
        /// Получение уникального имени файла для избежания перезаписи
        /// </summary>
        public string GetUniqueFilePath(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return basePath;

            try
            {
                if (!File.Exists(basePath))
                    return basePath;

                var directory = Path.GetDirectoryName(basePath);
                var fileName = Path.GetFileNameWithoutExtension(basePath);
                var extension = Path.GetExtension(basePath);

                int counter = 1;
                string newPath;
                
                do
                {
                    newPath = Path.Combine(directory, $"{fileName}_{counter}{extension}");
                    counter++;
                } while (File.Exists(newPath));

                return newPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения уникального пути: {ex.Message}");
                return basePath;
            }
        }
    }
} 