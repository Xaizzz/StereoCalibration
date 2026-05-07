# StereoCalibration

Десктопное приложение для Windows: **стереокалибровка пары камер**, **триангуляция ArUco-маркеров** и **контур «цифровая модель поверхности + адаптация траектории печати»** (визуализация в 3D, без отправки G-кода на принтер из описанного сценария).

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download)
[![OpenCV](https://img.shields.io/badge/OpenCV-4.10.0-green.svg)](https://opencv.org/)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Репозиторий: https://github.com/Xaizzz/StereoCalibration  

Подробное инженерно-математическое описание реализации: [`calibr/TECHNICAL_DESCRIPTION_RU.txt`](calibr/TECHNICAL_DESCRIPTION_RU.txt).

## Возможности

### Стерео и измерения

- Обнаружение камер и предпросмотр
- Стереокалибровка по шахматной доске (метод Zhang, OpenCV)
- Детекция ArUco, триангуляция 3D координат маркеров в миллиметрах (СК камеры 1 после калибровки)
- Сохранение и автозагрузка `calibration_result.json`
- Оценка качества калибровки по репроекционной ошибке

### 3D-сцена и модель поверхности (рана / объект)

- Визуализация через **HelixToolkit.Wpf**
- Загрузка **OBJ** с материалами и текстурами (`WoundModelLoaderService`), привязка маркеров CAD к ArUco ID через sidecar **`*.markers.json`**
- Выровнивание модели к измерениям камеры (**подобие**: масштаб, поворот, перенос) и деформация сетки по маркерам (**RBF / thin-plate**, `RbfDeformationService`)
- Дополнительные фильтры и ограничения деформации в `WoundModelService`; опциональная диагностика в JSONL через `IWoundDiagnosticSink` / `WoundDiagnosticsSessionRecorder`

### Траектория из G-кода (визуализация)

- Парсинг подмножества G-кода: перемещения, `G90`/`G91`, `G20`/`G21`, `G92`, экструдер (`M82`/`M83`) — см. ограничения в техническом описании (`GCodeParserService`)
- Проекция пути из плоскости на **поверхность по маркерам** (`SurfaceProjectionService`, Делоне в 2D) или на **mesh** (`WoundMeshProjectionService`)
- Воспроизведение траектории по времени (`PrintTrajectoryService`) и отображение в 3D (`Scene3DUserControl`)

## Стек технологий

| Компонент        | Назначение                                      |
|-----------------|--------------------------------------------------|
| .NET 8 (WinForms + WPF) | UI и хост WPF viewport                  |
| OpenCvSharp 4   | Калибровка, Undistort, Triangulate, Aruco       |
| HelixToolkit.Wpf | Сцена, меши, камера                             |
| Newtonsoft.Json | `calibration_result.json`, журналы              |

Файлы с примерами ассетов (OBJ/GLB-маркеры, текстуры и т.д.) при необходимости копируются в выход каталог по настройке `StereoCalibration.csproj`.

## Структура проекта

```
StereoCalibration/
├── calibr/
│   ├── MainForm.cs, Program.cs
│   ├── Controllers/           # например MainFormController
│   ├── Services/
│   │   ├── StereoCameraService.cs, CameraManager.cs, CameraService.cs
│   │   ├── StereoCalibrationService.cs, CalibrationService.cs
│   │   ├── ImageProcessingService.cs, ArUcoDetectionService.cs
│   │   ├── Scene3DService.cs, TriangulationService.cs
│   │   ├── WoundModelLoaderService.cs, WoundModelService.cs
│   │   ├── RbfDeformationService.cs, SurfaceProjectionService.cs
│   │   ├── WoundMeshProjectionService.cs
│   │   ├── GCodeParserService.cs, PrintTrajectoryService.cs
│   │   ├── WoundDiagnosticsSessionRecorder.cs, IWoundDiagnosticSink.cs
│   │   └── …
│   ├── UI/                    # Scene3DUserControl (WPF)
│   ├── cam1/, cam2/           # пары кадров для офлайн-калибровки (при использовании)
│   └── calibration_result.json
└── README.md
```

Удалённые из репозитория черновики `ARCHITECTURE_DIAGRAM.md` и `REFACTORING_SUMMARY.md` заменены консолидацией в `TECHNICAL_DESCRIPTION_RU.txt`.

## Быстрый старт

### Требования

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Две USB-камеры (для живого режима)
- Шахматная доска для калибровки (по умолчанию в коде часто используется паттерн **9×6** внутренних углов; физический размер клетки задайте в соответствии с реальной доской)

### Сборка и запуск

```bash
git clone https://github.com/Xaizzz/StereoCalibration.git
cd StereoCalibration/calibr
dotnet restore
dotnet build -c Release
dotnet run
```

### Калибровка (кратко)

1. Подключите обе камеры, выберите их в приложении.
2. Снимите **не менее ~10** пар кадров с доской, видимой на **обеих** камерах, с разнообразием положений.
3. Выполните калибровку; при успехе сохранится `calibration_result.json`.

### Измерения и маркеры

После калибровки разместите ArUco-маркеры в общем поле зрения; приложение триангулирует их 3D положение (мм).

### Документ с формулировками для отчёта

В [`calibr/TECHNICAL_DESCRIPTION_RU.txt`](calibr/TECHNICAL_DESCRIPTION_RU.txt) приведены: системы координат, пороговые условия (минимальное число маркеров на режим и т.д.), ограничения (нет онлайнового bundle adjustment, нет отправки G-кода на контроллер в этом контурe), ссылки на литературу.

## Конфигурация

Размер паттерна и клетки задаются в логике формы/контроллера (исторически в `MainForm` / `MainFormController`; проверьте актуальные константы при смене оборудования).

Разрешение захвата по умолчанию для пары задаётся в `StereoCameraService` (типично **640×480** — см. код и техническое описание).

## Зависимости NuGet (`calibr/StereoCalibration.csproj`)

- OpenCvSharp4 / Extensions / `OpenCvSharp4.runtime.win`
- Newtonsoft.Json 13.x
- HelixToolkit.Wpf 2.27.x

## Участие в разработке

1. Fork репозитория
2. Ветка для изменений: `git checkout -b feature/имя`
3. Коммиты и push, затем Pull Request на https://github.com/Xaizzz/StereoCalibration
