using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using HelixToolkit.Wpf;
using Newtonsoft.Json;
using StereoCalibration.Services;

namespace StereoCalibration.UI
{
    /// <summary>
    /// Класс для данных таблицы координат маркеров с поддержкой обновления UI
    /// </summary>
    public class MarkerCoordinate : INotifyPropertyChanged
    {
        private string _x = "";
        private string _y = "";
        private string _z = "";
        private string _distance = "";
        private string _name = "";
        private int _displayIndex;

        public int ID { get; set; }

        public int DisplayIndex
        {
            get => _displayIndex;
            set
            {
                if (_displayIndex != value)
                {
                    _displayIndex = value;
                    OnPropertyChanged(nameof(DisplayIndex));
                }
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string X
        {
            get => _x;
            set
            {
                if (_x != value)
                {
                    _x = value;
                    OnPropertyChanged(nameof(X));
                }
            }
        }

        public string Y
        {
            get => _y;
            set
            {
                if (_y != value)
                {
                    _y = value;
                    OnPropertyChanged(nameof(Y));
                }
            }
        }

        public string Z
        {
            get => _z;
            set
            {
                if (_z != value)
                {
                    _z = value;
                    OnPropertyChanged(nameof(Z));
                }
            }
        }

        public string Distance
        {
            get => _distance;
            set
            {
                if (_distance != value)
                {
                    _distance = value;
                    OnPropertyChanged(nameof(Distance));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Строка таблицы: имя объекта маркера в OBJ и вводимый ArUco ID с камеры.
    /// </summary>
    public sealed class WoundMarkerBindingRow : INotifyPropertyChanged
    {
        private string _arucoIdText = "";

        public WoundMarkerBindingRow(string modelObjectName, int? arucoId)
        {
            ModelObjectName = modelObjectName;
            _arucoIdText = arucoId.HasValue
                ? arucoId.Value.ToString(CultureInfo.InvariantCulture)
                : "";
        }

        public string ModelObjectName { get; }

        public string ArucoIdText
        {
            get => _arucoIdText;
            set
            {
                if (_arucoIdText == value)
                    return;

                _arucoIdText = value ?? "";
                OnPropertyChanged(nameof(ArucoIdText));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// WPF UserControl для отображения 3D-сцены стереокалибровки.
    /// 
    /// Несмотря на имя файла `.xaml.cs`, визуальная часть создаётся полностью
    /// программно: внутри строится Grid, HelixViewport3D, таблица координат и
    /// вспомогательные панели. Контрол используется из WinForms через ElementHost.
    /// 
    /// Основные визуальные объекты: две камеры, центр стереопары, базовая линия,
    /// тонкие чип-маркеры, подписи, линии от объективов к маркерам и полупрозрачная
    /// деформируемая поверхность по маркерам.
    /// </summary>
    public class Scene3DUserControl : UserControl
    {
        #region Поля
        private Scene3DService? _scene3DService;
        private readonly Dictionary<int, BoxVisual3D> _markerVisuals = new Dictionary<int, BoxVisual3D>();
        private readonly Dictionary<int, TextVisual3D> _markerTexts = new Dictionary<int, TextVisual3D>();
        private readonly Dictionary<int, LinesVisual3D> _woundPredictedGizmoVisuals = new Dictionary<int, LinesVisual3D>();
        
        // 3D элементы интерфейса
        private HelixViewport3D _viewport3D;
        private BoxVisual3D _camera1Visual;
        private BoxVisual3D _camera2Visual;
        private SphereVisual3D _camera1LensVisual;
        private SphereVisual3D _camera2LensVisual;
        private TruncatedConeVisual3D _stereoCenterVisual;
        private LinesVisual3D _cameraBaselineVisual;
        private LinesVisual3D _stereoAxisVisual;
        private TextVisual3D _baselineText;
        private TextVisual3D _camera1Text;
        private TextVisual3D _camera2Text;
        private TextVisual3D _centerText;
        private TextBlock _infoText;
        private LinesVisual3D _markerGuideLinesVisual;
        private LinesVisual3D _plannedPrintPathVisual;
        private LinesVisual3D _printedPrintPathVisual;
        private LinesVisual3D _printDebugNormalVisual;
        private LinesVisual3D _woundMarkerFitDebugVisual;
        private TruncatedConeVisual3D _printNozzleVisual;
        /// <summary>
        /// Единый визуальный объект поверхности по маркерам. Поверхность обновляется
        /// заменой геометрии MeshGeometry3D, а не созданием множества новых объектов,
        /// чтобы снизить нагрузку на WPF/Helix.
        /// </summary>
        private ModelVisual3D _markerSurfaceVisual;
        private GeometryModel3D _markerSurfaceModel;
        private MeshGeometry3D _markerSurfaceMesh;
        private ModelVisual3D _woundModelVisual;
        private Model3DGroup _woundModelGroup;
        private GeometryModel3D _woundModelModel;
        private MeshGeometry3D _woundModelMesh;
        private readonly Dictionary<string, Material> _woundMaterialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private Material _woundFallbackMaterial;
        private Material _woundFallbackBackMaterial;
        private string? _activeWoundTexturePath;
        private readonly Dictionary<int, Point3D> _lastSurfaceMarkerSnapshot = new Dictionary<int, Point3D>();
        private readonly HashSet<int> _lastSurfaceMarkerIds = new HashSet<int>();
        private readonly Dictionary<int, string> _markerTextCache = new Dictionary<int, string>();
        private readonly Dictionary<int, Point3D> _lastTrajectoryMarkerSnapshot = new Dictionary<int, Point3D>();
        private readonly Point3DCollection _printedTrajectoryPoints = new Point3DCollection();
        private DateTime _lastSurfaceUpdateTime = DateTime.MinValue;
        private DateTime _surfaceTopologyChangeDetectedAt = DateTime.MinValue;
        private DateTime _lastTrajectoryRebuildTime = DateTime.MinValue;
        private DateTime _lastPrintTimerTickTime = DateTime.UtcNow;
        private DateTime _lastMarkerTableUpdateTime = DateTime.MinValue;
        private DateTime _lastMarkerTextUpdateTime = DateTime.MinValue;
        private DateTime _lastGuideLinesUpdateTime = DateTime.MinValue;
        private DateTime _lastInfoPanelUpdateTime = DateTime.MinValue;
        private DateTime _lastWoundDiagnosticsWriteTime = DateTime.MinValue;
        private DateTime _lastViewportParityLogUtc = DateTime.MinValue;
        private bool _cameraVisualsInitialized = false;
        private bool _lastCameraCalibrationState = false;
        private Point3D _lastCamera1Position = new Point3D(double.NaN, double.NaN, double.NaN);
        private Point3D _lastCamera2Position = new Point3D(double.NaN, double.NaN, double.NaN);
        private Point3D _lastStereoCenterPosition = new Point3D(double.NaN, double.NaN, double.NaN);
        private int _lastRenderedCompletedExtrusionCount = 0;
        private int _lastRenderedActiveExtrusionIndex = -1;
        private bool _isInternalScrubUpdate = false;
        private bool _isScrubbing = false;
        private bool _resumePlaybackAfterScrub = false;
        private bool _trajectoryRebuildInProgress = false;
        private bool _startPlaybackAfterProjection = false;
        private bool _isPausedByInvalidSurface = false;
        private bool _resumeAfterSurfaceRecovery = false;
        private bool _showDeformationDebugOverlay = true;
        private int _lastDeformationMarkerCount = 0;
        private string _deformationStatus = "Ожидание mesh-референса.";
        private int _trajectoryRebuildCount = 0;
        private int _trajectoryRebuildFailureCount = 0;
        private double _lastTrajectoryAvgDisplacementMm = 0.0;
        private double _lastTrajectoryMaxDisplacementMm = 0.0;
        private double _lastTrajectoryRebuildDurationMs = 0.0;
        private bool _lastTrajectoryRebuildSucceeded = false;
        private DateTime _lastTrajectoryRebuildCompletedAt = DateTime.MinValue;
        private string _lastTrajectoryRebuildReason = "Ожидание перестроения.";
        private List<KeyValuePair<int, Point3D>>? _pendingTrajectoryMarkers;
        
        // UI элементы для таблицы координат
        private DataGrid _coordinatesTable;
        private Button _loadGCodeButton;
        private Button _startPrintButton;
        private Button _pausePrintButton;
        private Button _stopPrintButton;
        private Button _loadWoundModelButton;
        private Slider _speedSlider;
        private Slider _scrubSlider;
        private CheckBox _debugOverlayCheckBox;
        private CheckBox _showWoundModelCheckBox;
        private TextBlock _speedValueText;
        private TextBlock _gCodeStatusText;
        private TextBlock _trajectoryDiagnosticsText;
        private TextBlock _woundModelStatusText;
        private DataGrid _woundMarkerBindingsGrid;
        private Button _autoWoundMarkerBindingsButton;
        private Button _applyWoundMarkerBindingsButton;
        private Button _saveWoundMarkerBindingsButton;
        private Button _resetWoundDeformationReferenceButton;
        private ComboBox _printSurfaceModeCombo;
        private System.Collections.ObjectModel.ObservableCollection<WoundMarkerBindingRow> _woundMarkerBindingRows;
        private System.Collections.ObjectModel.ObservableCollection<MarkerCoordinate> _markersData;
        private readonly DispatcherTimer _printTimer;
        private readonly GCodeParserService _gCodeParserService = new GCodeParserService();
        private readonly WoundMeshProjectionService _woundMeshProjectionService = new WoundMeshProjectionService();
        private readonly SurfaceProjectionService _surfaceProjectionService = new SurfaceProjectionService();
        private readonly PrintTrajectoryService _printTrajectoryService = new PrintTrajectoryService();
        private readonly WoundModelService _woundModelService = new WoundModelService();
        private ParsedGCodePath? _parsedGCodePath;
        private ProjectedPrintPath? _projectedPrintPath;
        private WoundMeshPrintReference? _woundMeshPrintReference;
        private SurfacePrintReference? _surfacePrintReference;
        private PrintProjectionMode _printProjectionMode = PrintProjectionMode.WoundMesh;
        private string _loadedGCodeFileName = string.Empty;

        /// <summary>Режим привязки G-code при старте печати.</summary>
        private enum PrintProjectionMode
        {
            WoundMesh,
            MarkerSurface
        }
        #endregion

        private const double CameraBodyHalfWidth = 7.5;
        private const double CameraLensOffset = CameraBodyHalfWidth + 2.0;
        private const double StereoAxisLength = 350.0;
        private const int SurfaceUpdateIntervalMs = 300;
        private const int SurfaceTopologyStabilizationMs = 250;
        private const int MarkerTableUpdateIntervalMs = 200;
        private const int MarkerTextUpdateIntervalMs = 200;
        private const int GuideLinesUpdateIntervalMs = 100;
        private const int InfoPanelUpdateIntervalMs = 500;
        private const int WoundDiagnosticsFileWriteIntervalMs = 1000;
        private const int ViewportMarkerParityLogIntervalMs = 750;
        private const int PrintTimerIntervalMs = 50;
        private const int TrajectoryRebuildIntervalMs = 80;
        private const int MinMarkersForWoundMeshDeformation = WoundMeshProjectionService.MinMarkersForDeformation;
        private const int MaxSurfaceMarkers = 24;
        private const double SurfaceUpdateThresholdMm = 8.0;
        private const double TrajectoryRebuildThresholdMm = 1.5;
        private const double TrajectoryRunningRebuildThresholdMm = 0.8;
        private const double CameraPositionUpdateThresholdMm = 0.1;
        private const double NozzleHeight = 10.0;
        private const double NozzleBaseRadius = 3.0;
        private const double NozzleTopRadius = 0.35;
        private const double MinTriangleArea = 1e-3;

        /// <summary>Плоские «диски» маркера в миллиметрах сцены (тонкий блок).</summary>
        private const double MarkerChipExtentMm = 2.85;
        private const double MarkerChipThicknessMm = 0.42;
        private const byte MarkerChipFillAlpha = 88;

        private const double PredictedGizmoHalfExtentMm = 2.0;

        /// <summary>
        /// Создаёт WPF-разметку и начальную 3D-сцену.
        /// </summary>
        public Scene3DUserControl()
        {
            _printTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(PrintTimerIntervalMs)
            };
            _printTimer.Tick += PrintTimer_Tick;
            InitializeComponent();
            InitializeScene();
            InitializePrintSubsystem();
            _woundModelService.DiagnosticSink = WoundDiagnosticsSessionRecorder.Instance;
        }

        /// <summary>
        /// Создаёт интерфейс UserControl без XAML-файла.
        /// 
        /// Левая колонка содержит HelixViewport3D, правая — таблицу координат.
        /// Отдельная информационная панель накладывается поверх viewport для
        /// краткого состояния калибровки и управления.
        /// </summary>
        private void InitializeComponent()
        {
            // Создаем Grid как основной контейнер с двумя колонками
            var mainGrid = new Grid();
            
            // Определяем колонки: 3D сцена (75%) и таблица (25%)
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            // Инициализируем данные для таблицы маркеров
            _markersData = new System.Collections.ObjectModel.ObservableCollection<MarkerCoordinate>();
            _woundMarkerBindingRows = new System.Collections.ObjectModel.ObservableCollection<WoundMarkerBindingRow>();
            
            // Создаем HelixViewport3D с максимально совместимыми настройками рендеринга
            _viewport3D = new HelixViewport3D
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)), // Светло-серый
                CameraRotationMode = CameraRotationMode.Trackball,
                ShowCoordinateSystem = true,
                ShowViewCube = false,
                ShowFrameRate = false,
                IsHitTestVisible = true,
                ZoomExtentsWhenLoaded = false
            };
            
            // ЭКСПЕРИМЕНТАЛЬНО: Настройки рендеринга (без SetProcessRenderMode для совместимости)
            // Используем доступные настройки рендеринга
            try
            {
                var processRenderModeProperty = typeof(System.Windows.Media.RenderOptions).GetProperty("ProcessRenderMode");
                if (processRenderModeProperty != null)
                {
                    processRenderModeProperty.SetValue(null, 0); // Default
                    System.Diagnostics.Debug.WriteLine("Установлен режим рендеринга: Default");
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("ProcessRenderMode недоступен в данной версии .NET");
            }
            
            // Применяем настройки рендеринга для исправления полос
            ApplyAntiInterlaceSettings();
            
            // Дополнительные настройки для устранения проблем рендеринга
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(_viewport3D, BitmapScalingMode.HighQuality);
            System.Windows.Media.RenderOptions.SetEdgeMode(_viewport3D, EdgeMode.Aliased);
            
            // Принудительно устанавливаем настройки качества
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CachingHintProperty, CachingHint.Cache);
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CacheInvalidationThresholdMinimumProperty, 0.5);
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CacheInvalidationThresholdMaximumProperty, 2.0);

            // Настройка камеры
            _viewport3D.Camera = new PerspectiveCamera
            {
                Position = new Point3D(200, 200, 200),
                LookDirection = new Vector3D(-1, -1, -1),
                UpDirection = new Vector3D(0, 0, 1),
                FieldOfView = 45
            };

            // Добавляем простое и четкое освещение
            _viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new AmbientLight(Color.FromRgb(180, 180, 180))
            });
            
            _viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1))
            });

            // Создаем координатные оси
            _viewport3D.Children.Add(new CoordinateSystemVisual3D
            {
                ArrowLengths = 50
            });

            // Создаем визуальные элементы камер и центра (исправлен рендеринг)
            _camera1Visual = new BoxVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Length = 20,
                Width = 15,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(70, 130, 180)), // Steel Blue
                Visible = false
            };
            _viewport3D.Children.Add(_camera1Visual);

            _camera2Visual = new BoxVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Length = 20,
                Width = 15,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(220, 20, 60)), // Crimson
                Visible = false
            };
            _viewport3D.Children.Add(_camera2Visual);

            _camera1LensVisual = new SphereVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Radius = 3,
                Fill = Brushes.Black,
                Visible = false
            };
            _viewport3D.Children.Add(_camera1LensVisual);

            _camera2LensVisual = new SphereVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Radius = 3,
                Fill = Brushes.Black,
                Visible = false
            };
            _viewport3D.Children.Add(_camera2LensVisual);

            _stereoCenterVisual = new TruncatedConeVisual3D
            {
                Origin = new Point3D(0, 0, 0),
                Height = 15,
                BaseRadius = 8,
                TopRadius = 0,
                Fill = new SolidColorBrush(Color.FromRgb(255, 215, 0)), // Gold
                Visible = false
            };
            _viewport3D.Children.Add(_stereoCenterVisual);

            _cameraBaselineVisual = new LinesVisual3D
            {
                Color = Colors.DarkSlateBlue,
                Thickness = 4
            };

            _stereoAxisVisual = new LinesVisual3D
            {
                Color = Colors.Goldenrod,
                Thickness = 3
            };

            _markerGuideLinesVisual = new LinesVisual3D
            {
                Color = Colors.Gray,
                Thickness = 1,
                Points = new Point3DCollection()
            };
            _viewport3D.Children.Add(_markerGuideLinesVisual);

            _baselineText = new TextVisual3D
            {
                Position = new Point3D(0, 0, 0),
                Text = "Базовая линия камер",
                Foreground = Brushes.DarkSlateBlue,
                FontSize = 12
            };

            // Создаем текстовые элементы
            _camera1Text = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Камера 1",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            _camera2Text = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Камера 2",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            _centerText = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Центр",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            // Поверхность маркеров создаётся один раз и дальше только меняет
            // коллекции Positions/TriangleIndices. Это дешевле, чем постоянно
            // добавлять и удалять ModelVisual3D из viewport.
            _markerSurfaceMesh = new MeshGeometry3D();
            var surfaceBrush = new SolidColorBrush(Color.FromArgb(70, 30, 144, 255));
            var backSurfaceBrush = new SolidColorBrush(Color.FromArgb(45, 30, 144, 255));
            surfaceBrush.Freeze();
            backSurfaceBrush.Freeze();
            _markerSurfaceModel = new GeometryModel3D
            {
                Geometry = _markerSurfaceMesh,
                Material = new DiffuseMaterial(surfaceBrush),
                BackMaterial = new DiffuseMaterial(backSurfaceBrush)
            };
            _markerSurfaceVisual = new ModelVisual3D
            {
                Content = _markerSurfaceModel
            };
            _viewport3D.Children.Add(_markerSurfaceVisual);

            _woundModelMesh = new MeshGeometry3D();
            var woundBrush = new SolidColorBrush(Color.FromArgb(150, 210, 92, 92));
            var woundBackBrush = new SolidColorBrush(Color.FromArgb(90, 210, 92, 92));
            woundBrush.Freeze();
            woundBackBrush.Freeze();
            _woundFallbackMaterial = new DiffuseMaterial(woundBrush);
            _woundFallbackBackMaterial = new DiffuseMaterial(woundBackBrush);
            _woundModelModel = new GeometryModel3D
            {
                Geometry = _woundModelMesh,
                Material = _woundFallbackMaterial,
                BackMaterial = _woundFallbackBackMaterial,
                Transform = Transform3D.Identity
            };
            _woundModelGroup = new Model3DGroup();
            _woundModelGroup.Children.Add(_woundModelModel);
            _woundModelVisual = new ModelVisual3D
            {
                Content = _woundModelGroup
            };
            _viewport3D.Children.Add(_woundModelVisual);

            _plannedPrintPathVisual = new LinesVisual3D
            {
                Color = Color.FromArgb(220, 45, 130, 255),
                Thickness = 1
            };
            _viewport3D.Children.Add(_plannedPrintPathVisual);

            _printedPrintPathVisual = new LinesVisual3D
            {
                Color = Colors.Red,
                Thickness = 3,
                Points = _printedTrajectoryPoints
            };
            _viewport3D.Children.Add(_printedPrintPathVisual);

            _printDebugNormalVisual = new LinesVisual3D
            {
                Color = Colors.DarkRed,
                Thickness = 2,
                Points = new Point3DCollection()
            };
            _viewport3D.Children.Add(_printDebugNormalVisual);

            _woundMarkerFitDebugVisual = new LinesVisual3D
            {
                Color = Colors.OrangeRed,
                Thickness = 1,
                Points = new Point3DCollection()
            };
            _viewport3D.Children.Add(_woundMarkerFitDebugVisual);

            _printNozzleVisual = new TruncatedConeVisual3D
            {
                Origin = new Point3D(0, 0, 0),
                Normal = new Vector3D(0, 0, 1),
                Height = NozzleHeight,
                BaseRadius = NozzleBaseRadius,
                TopRadius = NozzleTopRadius,
                Fill = Brushes.DarkRed,
                Visible = false
            };
            _viewport3D.Children.Add(_printNozzleVisual);

            // Добавляем viewport в первую колонку
            Grid.SetColumn(_viewport3D, 0);
            mainGrid.Children.Add(_viewport3D);

            // Создаем информационную панель
            var infoBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(125, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(5)
            };

            _infoText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                Text = "3D Сцена стереокалибровки\nКалибровка не выполнена",
                FontWeight = FontWeights.Bold
            };

            infoBorder.Child = _infoText;
            mainGrid.Children.Add(infoBorder);

            // Создаем правую панель с таблицей координат
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10),
                Background = new SolidColorBrush(Color.FromArgb(240, 248, 248, 248))
            };
            Grid.SetColumn(rightPanel, 1);

            // Заголовок таблицы
            var tableHeader = new TextBlock
            {
                Text = "КООРДИНАТЫ ОБЪЕКТОВ",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = Brushes.DarkBlue,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            rightPanel.Children.Add(tableHeader);

            // Создаем таблицу координат
            _coordinatesTable = new DataGrid
            {
                ItemsSource = _markersData,
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                IsReadOnly = true,
                Background = Brushes.White,
                FontSize = 10,
                RowHeight = 25,
                ColumnHeaderHeight = 30,
                Height = 150,
                Margin = new Thickness(0, 0, 0, 10)
            };

            // Настраиваем колонки таблицы
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Объект",
                Binding = new System.Windows.Data.Binding("Name"),
                Width = 100
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "X (мм)",
                Binding = new System.Windows.Data.Binding("X"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Y (мм)",
                Binding = new System.Windows.Data.Binding("Y"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Z (мм)",
                Binding = new System.Windows.Data.Binding("Z"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Расст.",
                Binding = new System.Windows.Data.Binding("Distance"),
                Width = 50
            });

            rightPanel.Children.Add(_coordinatesTable);

            var woundHeader = new TextBlock
            {
                Text = "3D МОДЕЛЬ РАНЫ",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 4, 0, 8)
            };
            rightPanel.Children.Add(woundHeader);

            _loadWoundModelButton = new Button
            {
                Content = "Загрузить модель раны",
                Height = 30,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _loadWoundModelButton.Click += async (_, _) => await LoadWoundModelAsync();
            rightPanel.Children.Add(_loadWoundModelButton);

            _showWoundModelCheckBox = new CheckBox
            {
                Content = "Показывать модель раны",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 10
            };
            _showWoundModelCheckBox.Checked += (_, _) => SetWoundModelVisibility(true);
            _showWoundModelCheckBox.Unchecked += (_, _) => SetWoundModelVisibility(false);
            rightPanel.Children.Add(_showWoundModelCheckBox);

            _woundModelStatusText = new TextBlock
            {
                Text = "Модель раны не загружена",
                FontSize = 10,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            rightPanel.Children.Add(_woundModelStatusText);

            var woundBindingsCaption = new TextBlock
            {
                Text = "Соответствие OBJ ↔ ArUco ID",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            };
            rightPanel.Children.Add(woundBindingsCaption);

            _woundMarkerBindingsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                MaxHeight = 125,
                Margin = new Thickness(0, 0, 0, 6),
                ItemsSource = _woundMarkerBindingRows
            };
            _woundMarkerBindingsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Объект в OBJ",
                Binding = new Binding(nameof(WoundMarkerBindingRow.ModelObjectName))
                {
                    Mode = BindingMode.OneWay
                },
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _woundMarkerBindingsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "ArUco ID",
                Binding = new Binding(nameof(WoundMarkerBindingRow.ArucoIdText))
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                Width = 80
            });
            rightPanel.Children.Add(_woundMarkerBindingsGrid);

            var woundBindingsButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _autoWoundMarkerBindingsButton = new Button
            {
                Content = "Авто",
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 2, 8, 2),
                IsEnabled = false
            };
            _autoWoundMarkerBindingsButton.Click += async (_, _) => await AutoWoundMarkerBindingsFromStereoAsync();
            woundBindingsButtons.Children.Add(_autoWoundMarkerBindingsButton);
            _applyWoundMarkerBindingsButton = new Button
            {
                Content = "Применить",
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 2, 8, 2),
                IsEnabled = false
            };
            _applyWoundMarkerBindingsButton.Click += (_, _) => ApplyWoundMarkerBindingsFromGrid();
            woundBindingsButtons.Children.Add(_applyWoundMarkerBindingsButton);
            _saveWoundMarkerBindingsButton = new Button
            {
                Content = "Сохранить в .markers.json",
                Padding = new Thickness(8, 2, 8, 2),
                IsEnabled = false
            };
            _saveWoundMarkerBindingsButton.Click += (_, _) => SaveWoundMarkerBindingsFromGrid();
            woundBindingsButtons.Children.Add(_saveWoundMarkerBindingsButton);
            rightPanel.Children.Add(woundBindingsButtons);

            _resetWoundDeformationReferenceButton = new Button
            {
                Content = "Сбросить опору деформации",
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8),
                IsEnabled = false
            };
            _resetWoundDeformationReferenceButton.Click += (_, _) => ResetWoundDeformationReferenceClick();
            rightPanel.Children.Add(_resetWoundDeformationReferenceButton);

            var printHeader = new TextBlock
            {
                Text = "ТРАЕКТОРИЯ ПЕЧАТИ",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = Brushes.DarkRed,
                Margin = new Thickness(0, 4, 0, 8)
            };
            rightPanel.Children.Add(printHeader);

            _printSurfaceModeCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 10
            };
            _printSurfaceModeCombo.Items.Add("Печать по mesh модели раны");
            _printSurfaceModeCombo.Items.Add("Печать по маркерной поверхности (≥6 ArUco)");
            _printSurfaceModeCombo.SelectedIndex = 0;
            _printSurfaceModeCombo.SelectionChanged += OnPrintProjectionModeChanged;
            rightPanel.Children.Add(_printSurfaceModeCombo);

            _loadGCodeButton = new Button
            {
                Content = "Загрузить G-code",
                Height = 32,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _loadGCodeButton.Click += async (_, _) => await LoadGCodeAsync();
            rightPanel.Children.Add(_loadGCodeButton);

            var playbackButtonsPanel = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = 3,
                Margin = new Thickness(0, 0, 0, 8)
            };

            _startPrintButton = new Button
            {
                Content = "Старт",
                Margin = new Thickness(0, 0, 4, 0),
                Height = 30
            };
            _startPrintButton.Click += (_, _) => StartPrintPlayback();
            playbackButtonsPanel.Children.Add(_startPrintButton);

            _pausePrintButton = new Button
            {
                Content = "Пауза",
                Margin = new Thickness(0, 0, 4, 0),
                Height = 30
            };
            _pausePrintButton.Click += (_, _) => TogglePausePrintPlayback();
            playbackButtonsPanel.Children.Add(_pausePrintButton);

            _stopPrintButton = new Button
            {
                Content = "Стоп",
                Height = 30
            };
            _stopPrintButton.Click += (_, _) => StopPrintPlayback();
            playbackButtonsPanel.Children.Add(_stopPrintButton);
            rightPanel.Children.Add(playbackButtonsPanel);

            var speedPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 6)
            };
            speedPanel.Children.Add(new TextBlock
            {
                Text = "Скорость печати",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2)
            });

            var speedRow = new DockPanel();
            _speedSlider = new Slider
            {
                Minimum = 0.25,
                Maximum = 3.0,
                Value = 1.0,
                TickFrequency = 0.25,
                IsSnapToTickEnabled = false
            };
            _speedSlider.ValueChanged += (_, _) => OnSpeedChanged();
            DockPanel.SetDock(_speedSlider, Dock.Left);
            speedRow.Children.Add(_speedSlider);

            _speedValueText = new TextBlock
            {
                Text = "1.00x",
                Width = 52,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_speedValueText, Dock.Right);
            speedRow.Children.Add(_speedValueText);
            speedPanel.Children.Add(speedRow);
            rightPanel.Children.Add(speedPanel);

            rightPanel.Children.Add(new TextBlock
            {
                Text = "Позиция печати",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2)
            });

            _scrubSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = false,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _scrubSlider.ValueChanged += (_, _) => OnScrubChanged();
            _scrubSlider.PreviewMouseLeftButtonDown += (_, _) => BeginScrub();
            _scrubSlider.PreviewMouseLeftButtonUp += (_, _) => EndScrub();
            rightPanel.Children.Add(_scrubSlider);

            _debugOverlayCheckBox = new CheckBox
            {
                Content = "Debug overlay деформации",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 10
            };
            _debugOverlayCheckBox.Checked += (_, _) => OnDebugOverlayToggle(true);
            _debugOverlayCheckBox.Unchecked += (_, _) => OnDebugOverlayToggle(false);
            rightPanel.Children.Add(_debugOverlayCheckBox);

            _gCodeStatusText = new TextBlock
            {
                Text = "G-code не загружен",
                FontSize = 10,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            };
            rightPanel.Children.Add(_gCodeStatusText);

            var diagnosticsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(205, 214, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6)
            };
            _trajectoryDiagnosticsText = new TextBlock
            {
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.DarkSlateGray,
                TextWrapping = TextWrapping.Wrap
            };
            diagnosticsBorder.Child = _trajectoryDiagnosticsText;
            rightPanel.Children.Add(new Expander
            {
                Header = "ДЕФОРМАЦИЯ И REBUILD",
                IsExpanded = false,
                Margin = new Thickness(0, 8, 0, 4),
                Content = diagnosticsBorder
            });

            var rightScrollViewer = new ScrollViewer
            {
                Content = rightPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetColumn(rightScrollViewer, 1);
            mainGrid.Children.Add(rightScrollViewer);

            // Устанавливаем grid как содержимое UserControl
            this.Content = mainGrid;
        }

        /// <summary>
        /// Инициализация 3D сцены
        /// </summary>
        private void InitializeScene()
        {
            try
            {
                // Настройка начального вида
                _infoText.Text = "3D Сцена стереокалибровки\nКалибровка не выполнена\n\nУправление:\n• ПКМ - поворот\n• Колесо - масштаб\n• Shift+ПКМ - панорама";
                
                // Дополнительная попытка отключить все возможные стерео эффекты после загрузки
                _viewport3D.Loaded += (sender, e) =>
                {
                    try
                    {
                        // Многоуровневая попытка исправить рендеринг через разные интервалы
                        Task.Delay(100).ContinueWith(_ => Dispatcher.Invoke(() => DisableStereoEffects()));
                        Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(() => TryAlternativeRenderMode()));
                        Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => FinalRenderingFix()));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка финальной настройки рендеринга: {ex.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка инициализации 3D сцены: {ex.Message}");
            }
        }

        private bool IsMarkerSurfaceProjectionMode =>
            _printProjectionMode == PrintProjectionMode.MarkerSurface;

        private bool HasActivePrintReference =>
            _woundMeshPrintReference != null || _surfacePrintReference != null;

        private double ActiveProjectionSafetyClearanceMm =>
            IsMarkerSurfaceProjectionMode
                ? SurfaceProjectionService.SafetyClearanceMm
                : WoundMeshProjectionService.SafetyClearanceMm;

        private void OnPrintProjectionModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_printSurfaceModeCombo == null)
                return;

            var next = _printSurfaceModeCombo.SelectedIndex == 1
                ? PrintProjectionMode.MarkerSurface
                : PrintProjectionMode.WoundMesh;
            if (next == _printProjectionMode)
                return;

            _printProjectionMode = next;
            InvalidateMeshProjectionState("сменён режим привязки печати.");
        }

        private void InitializePrintSubsystem()
        {
            _printProjectionMode = _printSurfaceModeCombo.SelectedIndex == 1
                ? PrintProjectionMode.MarkerSurface
                : PrintProjectionMode.WoundMesh;
            _printTrajectoryService.SetSpeedMultiplier(_speedSlider.Value);
            _scrubSlider.Value = 0;
            _isPausedByInvalidSurface = false;
            _resumeAfterSurfaceRecovery = false;
            _deformationStatus = "Ожидание G-code.";
            _trajectoryRebuildCount = 0;
            _trajectoryRebuildFailureCount = 0;
            _lastTrajectoryAvgDisplacementMm = 0;
            _lastTrajectoryMaxDisplacementMm = 0;
            _lastTrajectoryRebuildDurationMs = 0;
            _lastTrajectoryRebuildSucceeded = false;
            _lastTrajectoryRebuildCompletedAt = DateTime.MinValue;
            _lastTrajectoryRebuildReason = "Ожидание перестроения.";
            UpdatePrintControlState();
            SetGCodeStatus("G-code не загружен");
            SetWoundModelStatus("Модель раны не загружена");
            UpdateTrajectoryDiagnosticsPanel();
        }

        private void SetGCodeStatus(string status)
        {
            _gCodeStatusText.Text = status;
        }

        private void UpdateTrajectoryDiagnosticsPanel()
        {
            var lastRebuildTimeText = _lastTrajectoryRebuildCompletedAt == DateTime.MinValue
                ? "—"
                : _lastTrajectoryRebuildCompletedAt.ToLocalTime().ToString("HH:mm:ss.fff");
            var lastRebuildStatusText = _trajectoryRebuildCount == 0
                ? "—"
                : (_lastTrajectoryRebuildSucceeded ? "успешно" : "freeze/ошибка");

            _trajectoryDiagnosticsText.Text =
                $"Смещение маркеров: avg={_lastTrajectoryAvgDisplacementMm:F2} мм, max={_lastTrajectoryMaxDisplacementMm:F2} мм\n" +
                $"Перестроений: {_trajectoryRebuildCount} (ошибок: {_trajectoryRebuildFailureCount})\n" +
                $"Триггер: {_lastTrajectoryRebuildReason}\n" +
                $"Последний rebuild: {lastRebuildStatusText}, {_lastTrajectoryRebuildDurationMs:F1} мс, {lastRebuildTimeText}\n" +
                $"Статус деформации: {_deformationStatus}";
        }

        private (double AverageMm, double MaxMm, int SampleCount) CalculateTrajectoryDisplacementStats(
            IReadOnlyList<KeyValuePair<int, Point3D>> markers)
        {
            if (markers.Count == 0 || _lastTrajectoryMarkerSnapshot.Count == 0)
                return (0, 0, 0);

            var sum = 0.0;
            var max = 0.0;
            var sampleCount = 0;
            foreach (var marker in markers)
            {
                if (!_lastTrajectoryMarkerSnapshot.TryGetValue(marker.Key, out var previous))
                    continue;

                var displacement = Distance(previous, marker.Value);
                sum += displacement;
                max = Math.Max(max, displacement);
                sampleCount++;
            }

            if (sampleCount == 0)
                return (0, 0, 0);

            return (sum / sampleCount, max, sampleCount);
        }

        private void UpdatePrintControlState()
        {
            var hasTrajectory = _projectedPrintPath != null && _printTrajectoryService.HasTrajectory;
            var hasLoadedGCode = _parsedGCodePath != null;
            _startPrintButton.IsEnabled = hasLoadedGCode;
            _pausePrintButton.IsEnabled = hasTrajectory && !_isPausedByInvalidSurface;
            _stopPrintButton.IsEnabled = hasTrajectory;
            _speedSlider.IsEnabled = hasTrajectory;
            _scrubSlider.IsEnabled = hasTrajectory;
            _pausePrintButton.Content = _isPausedByInvalidSurface
                ? "Автопауза"
                : (_printTrajectoryService.IsRunning ? "Пауза" : "Продолжить");
        }

        private void OnSpeedChanged()
        {
            _printTrajectoryService.SetSpeedMultiplier(_speedSlider.Value);
            _speedValueText.Text = $"{_speedSlider.Value:F2}x";
        }

        private void OnDebugOverlayToggle(bool enabled)
        {
            _showDeformationDebugOverlay = enabled;
            if (!enabled)
            {
                _printDebugNormalVisual.Points = new Point3DCollection();
            }
        }

        private void BeginScrub()
        {
            if (_projectedPrintPath == null)
                return;

            _isScrubbing = true;
            _resumePlaybackAfterScrub = _printTrajectoryService.IsRunning;
            if (_resumePlaybackAfterScrub)
            {
                _printTrajectoryService.Pause();
            }

            UpdatePrintControlState();
        }

        private void EndScrub()
        {
            if (_projectedPrintPath == null)
                return;

            _isScrubbing = false;
            _printTrajectoryService.SeekNormalized(_scrubSlider.Value);
            var snapshot = _printTrajectoryService.GetSnapshot();
            UpdatePlaybackVisuals(snapshot, true);

            if (_resumePlaybackAfterScrub)
            {
                _printTrajectoryService.Start();
                _lastPrintTimerTickTime = DateTime.UtcNow;
                if (!_printTimer.IsEnabled)
                    _printTimer.Start();
            }

            _resumePlaybackAfterScrub = false;
            UpdatePrintControlState();
        }

        private void OnScrubChanged()
        {
            if (_isInternalScrubUpdate || _projectedPrintPath == null)
                return;

            if (!_isScrubbing && _printTrajectoryService.IsRunning)
                return;

            _printTrajectoryService.SeekNormalized(_scrubSlider.Value);
            var snapshot = _printTrajectoryService.GetSnapshot();
            UpdatePlaybackVisuals(snapshot, true);
        }

        private async Task LoadWoundModelAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "OBJ model (*.obj)|*.obj|Все файлы (*.*)|*.*",
                Title = "Выберите OBJ-модель раны"
            };

            var defaultModelPath = GetDefaultWoundModelPath();
            if (!string.IsNullOrWhiteSpace(defaultModelPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(defaultModelPath);
                dialog.FileName = Path.GetFileName(defaultModelPath);
            }

            if (dialog.ShowDialog() != true)
                return;

            SetWoundModelStatus("Загрузка модели раны...");
            await Task.Yield();

            try
            {
                var loadResult = _woundModelService.Load(dialog.FileName);
                if (_woundModelService.Mesh != null)
                {
                    _woundModelMesh = _woundModelService.Mesh;
                }
                _woundMaterialCache.Clear();
                ApplyWoundModelMaterial();
                UpdateWoundModelTransform();

                SetWoundModelVisibility(_showWoundModelCheckBox.IsChecked == true);
                SetWoundModelStatus(
                    $"{Path.GetFileName(loadResult.SourcePath)}: vertices={loadResult.VertexCount}, " +
                    $"triangles={loadResult.TriangleCount}, modelMarkers={loadResult.ModelMarkerCount}, " +
                    $"linked={loadResult.LinkedMarkerCount}. {_woundModelService.Status}");
                InvalidateMeshProjectionState("изменена модель раны, требуется новый mesh-референс.");
                RefreshWoundMarkerBindingsGrid();
                UpdateInfoPanel(force: true);
            }
            catch (Exception ex)
            {
                _woundModelMesh.Positions = new Point3DCollection();
                _woundModelMesh.TriangleIndices = new Int32Collection();
                SetWoundModelStatus($"Ошибка загрузки модели раны: {ex.Message}");
                ClearWoundMarkerBindingsGrid();
            }
        }

        private static string? GetDefaultWoundModelPath()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "3d_deform.obj"),
                Path.Combine(Directory.GetCurrentDirectory(), "3d_deform.obj"),
                Path.Combine(Directory.GetCurrentDirectory(), "calibr", "3d_deform.obj")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private void SetWoundModelVisibility(bool visible)
        {
            if (_woundModelVisual == null || _woundModelGroup == null)
                return;

            _woundModelVisual.Content = visible ? _woundModelGroup : null;
        }

        private void UpdateWoundModelTransform()
        {
            if (_woundModelVisual == null)
                return;

            if (_scene3DService == null || !_scene3DService.IsCalibrated)
            {
                _woundModelVisual.Transform = Transform3D.Identity;
                return;
            }

            _woundModelVisual.Transform = _scene3DService.Camera1ToSceneTransform;
        }

        private void ApplyWoundModelMaterial()
        {
            if (_woundModelModel == null)
                return;

            RebuildWoundMaterialModels();
            if (_woundModelGroup.Children.Count > 1)
                return;

            var texturePath = _woundModelService.ActiveTexturePath;
            if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
            {
                _activeWoundTexturePath = null;
                _woundModelModel.Material = _woundFallbackMaterial;
                _woundModelModel.BackMaterial = _woundFallbackBackMaterial;
                return;
            }

            if (string.Equals(_activeWoundTexturePath, texturePath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(texturePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var imageBrush = new ImageBrush(bitmap)
                {
                    Stretch = Stretch.Fill
                };
                imageBrush.Freeze();

                var material = new DiffuseMaterial(imageBrush);
                if (material.CanFreeze)
                    material.Freeze();

                _woundModelModel.Material = material;
                _woundModelModel.BackMaterial = material;
                _activeWoundTexturePath = texturePath;
            }
            catch (Exception ex)
            {
                _activeWoundTexturePath = null;
                _woundModelModel.Material = _woundFallbackMaterial;
                _woundModelModel.BackMaterial = _woundFallbackBackMaterial;
                System.Diagnostics.Debug.WriteLine($"Ошибка загрузки texture map для модели раны: {ex.Message}");
            }
        }

        private void RebuildWoundMaterialModels()
        {
            if (_woundModelService.Mesh == null || _woundModelGroup == null)
                return;

            var mesh = _woundModelService.Mesh;
            var triangleMaterials = _woundModelService.TriangleMaterialNames;
            var materialTextures = _woundModelService.MaterialTexturePaths;
            if (triangleMaterials.Count == 0 ||
                triangleMaterials.Count != mesh.TriangleIndices.Count / 3 ||
                materialTextures.Count == 0)
            {
                _woundModelGroup.Children.Clear();
                _woundModelModel.Geometry = mesh;
                _woundModelGroup.Children.Add(_woundModelModel);
                return;
            }

            _woundModelGroup.Children.Clear();
            var groups = new Dictionary<string, MaterialMeshBuilder>(StringComparer.OrdinalIgnoreCase);
            for (var triangle = 0; triangle < triangleMaterials.Count; triangle++)
            {
                var materialName = string.IsNullOrWhiteSpace(triangleMaterials[triangle])
                    ? "__fallback__"
                    : triangleMaterials[triangle]!;
                if (!groups.TryGetValue(materialName, out var builder))
                {
                    builder = new MaterialMeshBuilder();
                    groups[materialName] = builder;
                }

                for (var corner = 0; corner < 3; corner++)
                {
                    var globalIndex = mesh.TriangleIndices[triangle * 3 + corner];
                    builder.TriangleIndices.Add(builder.GetLocalIndex(globalIndex, mesh));
                }
            }

            foreach (var group in groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var material = CreateWoundMaterialFor(group.Key, materialTextures);
                var geometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(group.Value.Positions),
                    TriangleIndices = new Int32Collection(group.Value.TriangleIndices),
                    TextureCoordinates = new PointCollection(group.Value.TextureCoordinates)
                };

                _woundModelGroup.Children.Add(new GeometryModel3D
                {
                    Geometry = geometry,
                    Material = material,
                    BackMaterial = material
                });
            }
        }

        private Material CreateWoundMaterialFor(
            string materialName,
            IReadOnlyDictionary<string, string> materialTextures)
        {
            if (_woundMaterialCache.TryGetValue(materialName, out var cached))
                return cached;

            if (!materialTextures.TryGetValue(materialName, out var texturePath) ||
                string.IsNullOrWhiteSpace(texturePath) ||
                !File.Exists(texturePath))
            {
                _woundMaterialCache[materialName] = _woundFallbackMaterial;
                return _woundFallbackMaterial;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(texturePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var brush = new ImageBrush(bitmap)
                {
                    Stretch = Stretch.Fill
                };
                brush.Freeze();
                var material = new DiffuseMaterial(brush);
                if (material.CanFreeze)
                    material.Freeze();

                _woundMaterialCache[materialName] = material;
                return material;
            }
            catch
            {
                _woundMaterialCache[materialName] = _woundFallbackMaterial;
                return _woundFallbackMaterial;
            }
        }

        private sealed class MaterialMeshBuilder
        {
            private readonly Dictionary<int, int> _globalToLocal = new Dictionary<int, int>();

            public List<Point3D> Positions { get; } = new List<Point3D>();
            public List<Point> TextureCoordinates { get; } = new List<Point>();
            public List<int> TriangleIndices { get; } = new List<int>();

            public int GetLocalIndex(int globalIndex, MeshGeometry3D source)
            {
                if (_globalToLocal.TryGetValue(globalIndex, out var localIndex))
                    return localIndex;

                localIndex = Positions.Count;
                _globalToLocal[globalIndex] = localIndex;
                Positions.Add(source.Positions[globalIndex]);
                TextureCoordinates.Add(
                    source.TextureCoordinates != null && globalIndex < source.TextureCoordinates.Count
                        ? source.TextureCoordinates[globalIndex]
                        : new Point(0.5, 0.5));
                return localIndex;
            }
        }

        private void SetWoundModelStatus(string status)
        {
            _woundModelStatusText.Text = status;
        }

        private void InvalidateMeshProjectionState(string reason)
        {
            _woundMeshPrintReference = null;
            _surfacePrintReference = null;
            _pendingTrajectoryMarkers = null;

            if (_parsedGCodePath == null)
                return;

            ClearTrajectoryVisuals(keepParsedPath: true);
            if (!string.IsNullOrWhiteSpace(_loadedGCodeFileName))
            {
                SetGCodeStatus($"{_loadedGCodeFileName}: {reason}");
            }
        }

        private void SetWoundMarkerBindingActionsEnabled(bool enabled)
        {
            if (_autoWoundMarkerBindingsButton != null)
                _autoWoundMarkerBindingsButton.IsEnabled = enabled;
            if (_applyWoundMarkerBindingsButton != null)
                _applyWoundMarkerBindingsButton.IsEnabled = enabled;
            if (_saveWoundMarkerBindingsButton != null)
                _saveWoundMarkerBindingsButton.IsEnabled = enabled;
        }

        private void ClearWoundMarkerBindingsGrid()
        {
            _woundMarkerBindingRows.Clear();
            SetWoundMarkerBindingActionsEnabled(false);
            if (_resetWoundDeformationReferenceButton != null)
                _resetWoundDeformationReferenceButton.IsEnabled = false;
        }

        private void RefreshWoundMarkerBindingsGrid()
        {
            _woundMarkerBindingRows.Clear();
            if (!_woundModelService.HasModel)
            {
                SetWoundMarkerBindingActionsEnabled(false);
                if (_resetWoundDeformationReferenceButton != null)
                    _resetWoundDeformationReferenceButton.IsEnabled = false;
                return;
            }

            foreach (var kv in _woundModelService.GetMarkerBindingMapSnapshot())
                _woundMarkerBindingRows.Add(new WoundMarkerBindingRow(kv.Key, kv.Value));

            SetWoundMarkerBindingActionsEnabled(_woundMarkerBindingRows.Count > 0);
            if (_resetWoundDeformationReferenceButton != null)
                _resetWoundDeformationReferenceButton.IsEnabled = true;
        }

        private void ResetWoundDeformationReferenceClick()
        {
            if (!_woundModelService.HasModel)
                return;

            _woundModelService.ResetDeformationReference();
            if (_woundModelService.Mesh != null && _woundModelModel != null)
                _woundModelMesh = _woundModelService.Mesh;
            ApplyWoundModelMaterial();
            UpdateWoundModelTransform();
            InvalidateMeshProjectionState("опора деформации модели сброшена.");

            SetWoundModelStatus(_woundModelService.Status);
            UpdateInfoPanel(force: true);
        }

        private Dictionary<string, int?> BuildMarkerBindingMapFromGrid()
        {
            var map = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _woundMarkerBindingRows)
            {
                var text = row.ArucoIdText?.Trim() ?? "";
                if (text.Length == 0)
                {
                    map[row.ModelObjectName] = null;
                    continue;
                }

                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                    id < 0)
                {
                    throw new FormatException(
                        $"Некорректный ArUco ID для «{row.ModelObjectName}»: «{row.ArucoIdText}». Ожидается неотрицательное целое или пусто.");
                }

                map[row.ModelObjectName] = id;
            }

            return map;
        }

        private async Task AutoWoundMarkerBindingsFromStereoAsync()
        {
            if (!_woundModelService.HasModel)
                return;

            var visibleCamera1MarkerCount = _scene3DService?.MarkerPositionsCamera1RawMm.Count > 0
                ? _scene3DService.MarkerPositionsCamera1RawMm.Count
                : _scene3DService?.MarkerPositionsCamera1Mm.Count ?? 0;
            if (_scene3DService == null ||
                !_scene3DService.IsCalibrated ||
                visibleCamera1MarkerCount < 3)
            {
                MessageBox.Show(
                    "Для автопривязки нужны видимые 3D ArUco-маркеры после стереокалибровки.",
                    "Автопривязка маркеров",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                var markerSnapshot = _scene3DService.MarkerPositionsCamera1Mm
                    .ToDictionary(item => item.Key, item => item.Value);
                if (_scene3DService.MarkerPositionsCamera1RawMm.Count > 0)
                {
                    markerSnapshot = _scene3DService.MarkerPositionsCamera1RawMm
                    .ToDictionary(item => item.Key, item => item.Value);
                }
                SetWoundMarkerBindingActionsEnabled(false);
                SetWoundModelStatus("Автопривязка marker1.* -> ArUco...");
                var autoBindingResult = await Task.Run(() =>
                {
                    var map = _woundModelService.BuildAutoMarkerBindingMap(
                        markerSnapshot,
                        out var rmseMm);
                    return (Map: map, RmseMm: rmseMm);
                });

                foreach (var row in _woundMarkerBindingRows)
                {
                    row.ArucoIdText = autoBindingResult.Map.TryGetValue(row.ModelObjectName, out var arucoId) && arucoId.HasValue
                        ? arucoId.Value.ToString(CultureInfo.InvariantCulture)
                        : "";
                }

                var loadResult = _woundModelService.ApplyMarkerBindings(autoBindingResult.Map);
                if (_woundModelService.Mesh != null)
                    _woundModelMesh = _woundModelService.Mesh;
                ApplyWoundModelMaterial();
                UpdateWoundModelTransform();
                InvalidateMeshProjectionState("автопривязка маркеров модели обновила соответствия.");

                SetWoundModelStatus(
                    $"{Path.GetFileName(loadResult.SourcePath)}: автопривязка RMSE {autoBindingResult.RmseMm:F1} мм, " +
                    $"связано {loadResult.LinkedMarkerCount}. {_woundModelService.Status}");
                UpdateInfoPanel(force: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Автопривязка маркеров",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                SetWoundMarkerBindingActionsEnabled(_woundMarkerBindingRows.Count > 0);
            }
        }

        private void ApplyWoundMarkerBindingsFromGrid()
        {
            if (!_woundModelService.HasModel)
                return;

            try
            {
                var map = BuildMarkerBindingMapFromGrid();
                var loadResult = _woundModelService.ApplyMarkerBindings(map);
                if (_woundModelService.Mesh != null)
                    _woundModelMesh = _woundModelService.Mesh;
                ApplyWoundModelMaterial();
                UpdateWoundModelTransform();
                InvalidateMeshProjectionState("изменены соответствия маркеров модели.");

                SetWoundModelStatus(
                    $"{Path.GetFileName(loadResult.SourcePath)}: связано маркеров {loadResult.LinkedMarkerCount}. {_woundModelService.Status}");
                UpdateInfoPanel(force: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Соответствие маркеров",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void SaveWoundMarkerBindingsFromGrid()
        {
            if (!_woundModelService.HasModel)
                return;

            try
            {
                var map = BuildMarkerBindingMapFromGrid();
                var loadResult = _woundModelService.ApplyMarkerBindings(map);
                if (_woundModelService.Mesh != null)
                    _woundModelMesh = _woundModelService.Mesh;
                ApplyWoundModelMaterial();
                UpdateWoundModelTransform();
                InvalidateMeshProjectionState("изменены и сохранены соответствия маркеров модели.");

                _woundModelService.SaveMarkerBindingsToSidecar(map);
                SetWoundModelStatus(
                    $"{Path.GetFileName(loadResult.SourcePath)}: связано {loadResult.LinkedMarkerCount}. {_woundModelService.Status}");
                UpdateInfoPanel(force: true);
                MessageBox.Show(
                    "Соответствия применены и записаны в .markers.json рядом с OBJ.",
                    "Соответствие маркеров",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Сохранение маркеров",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private async Task LoadGCodeAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "G-code (*.gcode;*.gco;*.gc;*.nc)|*.gcode;*.gco;*.gc;*.nc|Все файлы (*.*)|*.*",
                Title = "Выберите G-code файл"
            };

            if (dialog.ShowDialog() != true)
                return;

            SetGCodeStatus("Чтение и парсинг G-code...");

            try
            {
                var parsedPath = await Task.Run(() => _gCodeParserService.ParseFile(dialog.FileName));
                if (parsedPath.Moves.Count == 0)
                {
                    _parsedGCodePath = null;
                    _projectedPrintPath = null;
                    _loadedGCodeFileName = string.Empty;
                    ClearTrajectoryVisuals(keepParsedPath: false);
                    SetGCodeStatus("Файл не содержит поддерживаемых перемещений G0/G1.");
                    return;
                }

                _parsedGCodePath = parsedPath;
                _loadedGCodeFileName = Path.GetFileName(dialog.FileName);
                _woundMeshPrintReference = null;
                _surfacePrintReference = null;
                _isPausedByInvalidSurface = false;
                _resumeAfterSurfaceRecovery = false;
                _deformationStatus = IsMarkerSurfaceProjectionMode
                    ? "Ожидание фиксации референса по маркерам."
                    : "Ожидание фиксации mesh-референса.";
                _lastTrajectoryMarkerSnapshot.Clear();
                ClearTrajectoryVisuals(keepParsedPath: true);
                SetGCodeStatus(
                    $"{_loadedGCodeFileName}: загружен ({parsedPath.Moves.Count} move). " +
                    "Нажмите Старт для фиксации референса печати.");
            }
            catch (Exception ex)
            {
                ClearTrajectoryVisuals(keepParsedPath: true);
                _deformationStatus = "Ошибка загрузки G-code.";
                SetGCodeStatus($"Ошибка загрузки G-code: {ex.Message}");
            }
        }

        private void RequestTrajectoryProjection(
            IReadOnlyList<KeyValuePair<int, Point3D>> orderedMarkers,
            bool preservePlaybackState,
            string rebuildReason)
        {
            if (_parsedGCodePath == null || !HasActivePrintReference)
                return;

            _lastTrajectoryRebuildReason = rebuildReason;

            var meshVerticesScene = new List<Point3D>();
            var rawMarkersSnapshot = orderedMarkers.ToList();

            if (_surfacePrintReference != null)
            {
                _lastDeformationMarkerCount = rawMarkersSnapshot.Count;
                if (_lastDeformationMarkerCount < SurfaceProjectionService.MinMarkersForDeformation)
                {
                    HandleInvalidSurface(
                        $"Режим маркеров: нужно минимум {SurfaceProjectionService.MinMarkersForDeformation} ArUco в кадре, сейчас {_lastDeformationMarkerCount}.");
                    return;
                }
            }
            else
            {
                _lastDeformationMarkerCount = Math.Max(0, _woundModelService.ActiveMarkerCount);
                if (_lastDeformationMarkerCount < MinMarkersForWoundMeshDeformation)
                {
                    HandleInvalidSurface(
                        $"Для деформации модели нужно минимум {MinMarkersForWoundMeshDeformation} связ. маркеров, сейчас {_lastDeformationMarkerCount}.");
                    return;
                }

                if (!TryGetCurrentWoundMeshSceneSnapshot(out meshVerticesScene, out _))
                {
                    HandleInvalidSurface("Не удалось получить деформированный mesh модели для проекции.");
                    return;
                }
            }

            if (_trajectoryRebuildInProgress)
            {
                _pendingTrajectoryMarkers = orderedMarkers.ToList();
                return;
            }

            _trajectoryRebuildInProgress = true;
            _lastTrajectoryRebuildTime = DateTime.UtcNow;

            var displacementStats = rawMarkersSnapshot.Count == 0
                ? (AverageMm: 0.0, MaxMm: 0.0, SampleCount: 0)
                : CalculateTrajectoryDisplacementStats(rawMarkersSnapshot);
            _lastTrajectoryAvgDisplacementMm = displacementStats.AverageMm;
            _lastTrajectoryMaxDisplacementMm = displacementStats.MaxMm;
            UpdateTrajectoryDiagnosticsPanel();

            var preferredSidePoint = GetCameraSideReferencePoint();
            var resumeProgress = preservePlaybackState ? _printTrajectoryService.NormalizedProgress : 0.0;
            var resumePlayback = preservePlaybackState && _printTrajectoryService.IsRunning;
            var rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var snapshotSurfacePrintRef = _surfacePrintReference;
            var snapshotWoundPrintRef = _woundMeshPrintReference;

            Task.Run(() =>
            {
                if (snapshotSurfacePrintRef != null &&
                    _surfaceProjectionService.TryProjectPath(
                        snapshotSurfacePrintRef,
                        rawMarkersSnapshot,
                        preferredSidePoint,
                        out var surfaceProjected))
                {
                    return surfaceProjected;
                }

                if (snapshotWoundPrintRef != null &&
                    _woundMeshProjectionService.TryProjectPath(
                        snapshotWoundPrintRef,
                        meshVerticesScene,
                        preferredSidePoint,
                        out var meshProjected))
                {
                    return meshProjected;
                }

                return (ProjectedPrintPath?)null;
            }).ContinueWith(task =>
            {
                Dispatcher.Invoke(() =>
                {
                    var rebuildSucceeded = false;
                    try
                    {
                        if (task.Exception != null)
                        {
                            HandleInvalidSurface($"Ошибка проекции: {task.Exception.GetBaseException().Message}");
                            return;
                        }

                        var projectedPath = task.Result;
                        if (projectedPath == null)
                        {
                            HandleInvalidSurface("Невалидная геометрия поверхности, проекция заморожена.");
                            return;
                        }

                        if (!IsProjectedPathRuntimeValid(projectedPath))
                        {
                            HandleInvalidSurface("Проверка no-penetration/runtime не пройдена, оставлена последняя валидная геометрия.");
                            return;
                        }

                        ApplyProjectedPath(projectedPath, resumeProgress, resumePlayback || _startPlaybackAfterProjection);
                        UpdatePrintDebugVisual(rawMarkersSnapshot, preferredSidePoint);
                        HandleSurfaceRecovered();
                        rebuildSucceeded = true;
                        _startPlaybackAfterProjection = false;
                        _lastTrajectoryMarkerSnapshot.Clear();
                        foreach (var marker in rawMarkersSnapshot)
                        {
                            _lastTrajectoryMarkerSnapshot[marker.Key] = marker.Value;
                        }
                    }
                    finally
                    {
                        rebuildStopwatch.Stop();
                        _trajectoryRebuildCount++;
                        if (!rebuildSucceeded)
                        {
                            _trajectoryRebuildFailureCount++;
                        }

                        _lastTrajectoryRebuildSucceeded = rebuildSucceeded;
                        _lastTrajectoryRebuildDurationMs = rebuildStopwatch.Elapsed.TotalMilliseconds;
                        _lastTrajectoryRebuildCompletedAt = DateTime.UtcNow;
                        UpdateTrajectoryDiagnosticsPanel();

                        _trajectoryRebuildInProgress = false;

                        if (_pendingTrajectoryMarkers != null)
                        {
                            var pending = _pendingTrajectoryMarkers;
                            _pendingTrajectoryMarkers = null;
                            var allowImmediateRetry = _isPausedByInvalidSurface ||
                                                      (DateTime.UtcNow - _lastTrajectoryRebuildTime).TotalMilliseconds >= TrajectoryRebuildIntervalMs;
                            if (allowImmediateRetry)
                            {
                                RequestTrajectoryProjection(pending, preservePlaybackState: true, rebuildReason);
                            }
                        }
                    }
                });
            }, TaskScheduler.Default);
        }

        private void UpdatePrintDebugVisual(
            IReadOnlyList<KeyValuePair<int, Point3D>> surfaceMarkers,
            Point3D preferredSidePoint)
        {
            if (!_showDeformationDebugOverlay || surfaceMarkers.Count == 0)
            {
                _printDebugNormalVisual.Points = new Point3DCollection();
                return;
            }

            var center = new Point3D(
                surfaceMarkers.Average(marker => marker.Value.X),
                surfaceMarkers.Average(marker => marker.Value.Y),
                surfaceMarkers.Average(marker => marker.Value.Z));
            var direction = preferredSidePoint - center;
            if (direction.Length < 1e-6)
            {
                _printDebugNormalVisual.Points = new Point3DCollection();
                return;
            }

            direction.Normalize();
            _printDebugNormalVisual.Points = new Point3DCollection
            {
                center,
                center + direction * 35.0
            };
        }

        private void HandleInvalidSurface(string reason)
        {
            var status = $"Невалидная геометрия: {reason}";
            var isSameStatus = string.Equals(_deformationStatus, status, StringComparison.Ordinal);
            _deformationStatus = status;
            _lastTrajectoryRebuildSucceeded = false;

            if (!_isPausedByInvalidSurface)
            {
                _resumeAfterSurfaceRecovery = _printTrajectoryService.IsRunning;
                _isPausedByInvalidSurface = true;
            }

            if (_printTrajectoryService.IsRunning)
            {
                _printTrajectoryService.Pause();
                _printTimer.Stop();
            }

            if (_isPausedByInvalidSurface && isSameStatus && !_printTrajectoryService.IsRunning)
            {
                UpdateTrajectoryDiagnosticsPanel();
                UpdatePrintControlState();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_loadedGCodeFileName))
            {
                SetGCodeStatus($"{_loadedGCodeFileName}: {reason} Геометрия заморожена.");
            }
            else
            {
                SetGCodeStatus(reason);
            }

            UpdateTrajectoryDiagnosticsPanel();
            UpdatePrintControlState();
        }

        private void HandleSurfaceRecovered()
        {
            _deformationStatus = $"Геометрия валидна, clearance={ActiveProjectionSafetyClearanceMm:F1} мм";
            if (!_isPausedByInvalidSurface)
            {
                UpdateTrajectoryDiagnosticsPanel();
                return;
            }

            _isPausedByInvalidSurface = false;
            var shouldResume = _resumeAfterSurfaceRecovery;
            _resumeAfterSurfaceRecovery = false;

            if (shouldResume && _projectedPrintPath != null && _printTrajectoryService.HasTrajectory)
            {
                _printTrajectoryService.Start();
                _lastPrintTimerTickTime = DateTime.UtcNow;
                if (!_printTimer.IsEnabled)
                    _printTimer.Start();

                SetGCodeStatus($"{_loadedGCodeFileName}: геометрия восстановлена, печать продолжена.");
            }
            else if (!string.IsNullOrWhiteSpace(_loadedGCodeFileName))
            {
                SetGCodeStatus($"{_loadedGCodeFileName}: геометрия восстановлена.");
            }

            UpdateTrajectoryDiagnosticsPanel();
            UpdatePrintControlState();
        }

        private static bool IsProjectedPathRuntimeValid(ProjectedPrintPath projectedPath)
        {
            const double maxReasonableSegmentLengthMm = 2500.0;

            foreach (var move in projectedPath.Moves)
            {
                if (!IsFinitePoint(move.Start) || !IsFinitePoint(move.End))
                    return false;

                if (Distance(move.Start, move.End) > maxReasonableSegmentLengthMm)
                    return false;
            }

            return true;
        }

        private static bool IsFinitePoint(Point3D point)
        {
            return !double.IsNaN(point.X) &&
                   !double.IsNaN(point.Y) &&
                   !double.IsNaN(point.Z) &&
                   !double.IsInfinity(point.X) &&
                   !double.IsInfinity(point.Y) &&
                   !double.IsInfinity(point.Z);
        }

        private Point3D GetCameraSideReferencePoint()
        {
            if (_scene3DService == null || !_scene3DService.IsCalibrated)
                return new Point3D(0, 0, 0);

            var cam1 = _scene3DService.Camera1Position;
            var cam2 = _scene3DService.Camera2Position;
            return new Point3D(
                (cam1.X + cam2.X) / 2.0,
                (cam1.Y + cam2.Y) / 2.0,
                (cam1.Z + cam2.Z) / 2.0);
        }

        private void ApplyProjectedPath(ProjectedPrintPath projectedPath, double normalizedProgress, bool resumePlayback)
        {
            _projectedPrintPath = projectedPath;
            _printTrajectoryService.LoadTrajectory(projectedPath);
            _printTrajectoryService.SeekNormalized(normalizedProgress);

            var snapshot = _printTrajectoryService.GetSnapshot();
            UpdatePlaybackVisuals(snapshot, true);

            if (resumePlayback)
            {
                _printTrajectoryService.Start();
                _lastPrintTimerTickTime = DateTime.UtcNow;
                if (!_printTimer.IsEnabled)
                    _printTimer.Start();
            }

            SetGCodeStatus(
                $"{_loadedGCodeFileName}: move={projectedPath.Moves.Count}, " +
                $"print={projectedPath.ExtrusionMoves.Count}, markers={projectedPath.MarkerCount}. " +
                $"clearance={ActiveProjectionSafetyClearanceMm:F1} мм.");
            UpdatePrintControlState();
        }

        private void UpdatePlannedTrajectoryVisual(PrintPlaybackSnapshot snapshot)
        {
            if (_projectedPrintPath == null)
            {
                _plannedPrintPathVisual.Points = new Point3DCollection();
                return;
            }

            var extrusionMoves = _projectedPrintPath.ExtrusionMoves;
            if (extrusionMoves.Count == 0)
            {
                _plannedPrintPathVisual.Points = new Point3DCollection();
                return;
            }

            var points = new Point3DCollection(extrusionMoves.Count * 2);
            var nextMoveIndex = Math.Min(snapshot.CompletedExtrusionCount, extrusionMoves.Count);

            if (snapshot.ActiveExtrusionIndex >= 0 && snapshot.ActiveExtrusionIndex < extrusionMoves.Count)
            {
                var activeMove = extrusionMoves[snapshot.ActiveExtrusionIndex];
                var activeSegmentStart = Lerp(activeMove.Start, activeMove.End, snapshot.ActiveExtrusionProgress);
                points.Add(activeSegmentStart);
                points.Add(activeMove.End);
                nextMoveIndex = Math.Max(nextMoveIndex, snapshot.ActiveExtrusionIndex + 1);
            }

            for (var index = nextMoveIndex; index < extrusionMoves.Count; index++)
            {
                points.Add(extrusionMoves[index].Start);
                points.Add(extrusionMoves[index].End);
            }

            _plannedPrintPathVisual.Points = points;
        }

        private void StartPrintPlayback()
        {
            if (_parsedGCodePath == null)
                return;

            if (_isPausedByInvalidSurface)
            {
                var need = IsMarkerSurfaceProjectionMode
                    ? SurfaceProjectionService.MinMarkersForDeformation
                    : MinMarkersForWoundMeshDeformation;
                SetGCodeStatus($"Автопауза: нужно минимум {need} маркеров для текущего режима печати.");
                return;
            }

            if (!HasActivePrintReference || _projectedPrintPath == null || !_printTrajectoryService.HasTrajectory)
            {
                if (!TryCapturePrintReference())
                    return;

                _startPlaybackAfterProjection = true;
                var markerCandidates = _scene3DService == null
                    ? new List<KeyValuePair<int, Point3D>>()
                    : GetTrajectoryMarkerCandidates(_scene3DService.MarkerPositions);
                RequestTrajectoryProjection(
                    markerCandidates,
                    preservePlaybackState: false,
                    rebuildReason: "Старт печати / фиксация референса");
                SetGCodeStatus(IsMarkerSurfaceProjectionMode
                    ? $"{_loadedGCodeFileName}: фиксирую поверхность по маркерам и строю траекторию..."
                    : $"{_loadedGCodeFileName}: фиксирую mesh-референс и строю биопечать...");
                return;
            }

            _printTrajectoryService.Start();
            _lastPrintTimerTickTime = DateTime.UtcNow;
            if (!_printTimer.IsEnabled)
                _printTimer.Start();

            UpdatePrintControlState();
        }

        private bool TryCapturePrintReference()
        {
            if (_parsedGCodePath == null || _scene3DService == null || !_scene3DService.IsCalibrated)
            {
                SetGCodeStatus("Нужна калибровка и загруженный G-code для старта печати.");
                return false;
            }

            var preferredSidePoint = GetCameraSideReferencePoint();

            if (IsMarkerSurfaceProjectionMode)
            {
                _woundMeshPrintReference = null;
                var ordered = GetSurfaceMarkerCandidates(_scene3DService.MarkerPositions);
                if (ordered.Count < SurfaceProjectionService.MinMarkersForDeformation)
                {
                    _deformationStatus =
                        $"Режим маркеров: {ordered.Count}/{SurfaceProjectionService.MinMarkersForDeformation}+ ArUco в кадре.";
                    UpdateTrajectoryDiagnosticsPanel();
                    SetGCodeStatus(
                        $"Для печати по маркерам нужно минимум {SurfaceProjectionService.MinMarkersForDeformation} видимых ArUco.");
                    return false;
                }

                if (!_surfaceProjectionService.TryCreateReference(
                    _parsedGCodePath,
                    ordered,
                    preferredSidePoint,
                    out var surfaceRef))
                {
                    SetGCodeStatus("Не удалось зафиксировать референс по маркерной поверхности.");
                    return false;
                }

                _surfacePrintReference = surfaceRef;
                _deformationStatus = "Референс по маркерам зафиксирован.";
                _isPausedByInvalidSurface = false;
                _resumeAfterSurfaceRecovery = false;
                _lastDeformationMarkerCount = ordered.Count;
                UpdateTrajectoryDiagnosticsPanel();
                return true;
            }

            _surfacePrintReference = null;

            if (!_woundModelService.HasModel || !_woundModelService.HasMesh)
            {
                SetGCodeStatus("Загрузите OBJ-модель раны и дождитесь её синхронизации перед стартом печати в режиме mesh.");
                return false;
            }

            var supportMarkerCount = Math.Max(0, _woundModelService.ActiveMarkerCount);
            if (supportMarkerCount < MinMarkersForWoundMeshDeformation)
            {
                _deformationStatus =
                    $"Ожидание маркеров модели: {supportMarkerCount}/{MinMarkersForWoundMeshDeformation}+";
                UpdateTrajectoryDiagnosticsPanel();
                SetGCodeStatus(
                    $"Для старта печати нужно минимум {MinMarkersForWoundMeshDeformation} связанных маркера модели.");
                return false;
            }

            var markerZoneScene = new List<Point3D>();
            foreach (var markerId in _woundModelService.ActiveDeformationMarkerIds)
            {
                if (_scene3DService.MarkerPositions.TryGetValue(markerId, out var scenePoint))
                    markerZoneScene.Add(scenePoint);
            }

            if (markerZoneScene.Count < WoundMeshProjectionService.MinMarkersForDeformation)
            {
                _deformationStatus =
                    $"В поле зрения: {markerZoneScene.Count}/{WoundMeshProjectionService.MinMarkersForDeformation}+ привязанных маркеров.";
                UpdateTrajectoryDiagnosticsPanel();
                SetGCodeStatus(
                    $"Для mesh-печати по зоне маркеров нужно минимум {WoundMeshProjectionService.MinMarkersForDeformation} привязанных маркеров одновременно в кадре (сейчас {markerZoneScene.Count}).");
                return false;
            }

            if (!TryGetCurrentWoundMeshSceneSnapshot(out var meshVerticesScene, out var meshTriangles))
            {
                SetGCodeStatus("Не удалось сформировать snapshot деформированной модели для печати.");
                return false;
            }

            if (!_woundMeshProjectionService.TryCreateReference(
                _parsedGCodePath,
                meshVerticesScene,
                meshTriangles,
                supportMarkerCount,
                preferredSidePoint,
                out var printReference,
                markerZoneScene))
            {
                SetGCodeStatus("Не удалось зафиксировать референсную mesh-поверхность печати.");
                return false;
            }

            _woundMeshPrintReference = printReference;
            _deformationStatus = "Референс mesh зафиксирован, деформация активна.";
            _isPausedByInvalidSurface = false;
            _resumeAfterSurfaceRecovery = false;
            _lastDeformationMarkerCount = supportMarkerCount;

            UpdateTrajectoryDiagnosticsPanel();
            return true;
        }

        private void TogglePausePrintPlayback()
        {
            if (_projectedPrintPath == null || !_printTrajectoryService.HasTrajectory)
                return;

            if (_isPausedByInvalidSurface)
                return;

            if (_printTrajectoryService.IsRunning)
            {
                _printTrajectoryService.Pause();
                _printTimer.Stop();
            }
            else
            {
                _printTrajectoryService.Start();
                _lastPrintTimerTickTime = DateTime.UtcNow;
                if (!_printTimer.IsEnabled)
                    _printTimer.Start();
            }

            UpdatePrintControlState();
        }

        private void StopPrintPlayback()
        {
            _printTrajectoryService.Stop();
            _printTimer.Stop();
            _isPausedByInvalidSurface = false;
            _resumeAfterSurfaceRecovery = false;
            var snapshot = _printTrajectoryService.GetSnapshot();
            UpdatePlaybackVisuals(snapshot, true);
            UpdatePrintControlState();
        }

        private void PrintTimer_Tick(object? sender, EventArgs e)
        {
            if (!_printTrajectoryService.IsRunning)
            {
                _printTimer.Stop();
                UpdatePrintControlState();
                return;
            }

            var now = DateTime.UtcNow;
            var deltaSeconds = Math.Max(0, (now - _lastPrintTimerTickTime).TotalSeconds);
            _lastPrintTimerTickTime = now;

            var snapshot = _printTrajectoryService.Advance(deltaSeconds);
            UpdatePlaybackVisuals(snapshot, false);

            if (snapshot.IsFinished)
            {
                _printTimer.Stop();
                UpdatePrintControlState();
            }
        }

        private void UpdatePlaybackVisuals(PrintPlaybackSnapshot snapshot, bool forceRebuild)
        {
            UpdatePlannedTrajectoryVisual(snapshot);
            UpdatePrintedTrajectoryVisual(snapshot, forceRebuild);
            UpdateNozzleVisual(snapshot);

            _isInternalScrubUpdate = true;
            _scrubSlider.Value = snapshot.NormalizedProgress;
            _isInternalScrubUpdate = false;
        }

        private void UpdatePrintedTrajectoryVisual(PrintPlaybackSnapshot snapshot, bool forceRebuild)
        {
            if (_projectedPrintPath == null)
            {
                _printedTrajectoryPoints.Clear();
                _printedPrintPathVisual.Points = _printedTrajectoryPoints;
                return;
            }

            var extrusionMoves = _projectedPrintPath.ExtrusionMoves;
            if (forceRebuild || snapshot.CompletedExtrusionCount < _lastRenderedCompletedExtrusionCount)
            {
                RebuildPrintedTrajectoryVisual(snapshot, extrusionMoves);
                return;
            }

            if (_lastRenderedActiveExtrusionIndex >= 0 && _printedTrajectoryPoints.Count >= 2)
            {
                _printedTrajectoryPoints.RemoveAt(_printedTrajectoryPoints.Count - 1);
                _printedTrajectoryPoints.RemoveAt(_printedTrajectoryPoints.Count - 1);
            }

            for (var index = _lastRenderedCompletedExtrusionCount;
                 index < Math.Min(snapshot.CompletedExtrusionCount, extrusionMoves.Count);
                 index++)
            {
                _printedTrajectoryPoints.Add(extrusionMoves[index].Start);
                _printedTrajectoryPoints.Add(extrusionMoves[index].End);
            }

            if (snapshot.ActiveExtrusionIndex >= 0 && snapshot.ActiveExtrusionIndex < extrusionMoves.Count)
            {
                var activeMove = extrusionMoves[snapshot.ActiveExtrusionIndex];
                _printedTrajectoryPoints.Add(activeMove.Start);
                _printedTrajectoryPoints.Add(Lerp(activeMove.Start, activeMove.End, snapshot.ActiveExtrusionProgress));
            }

            _lastRenderedCompletedExtrusionCount = Math.Min(snapshot.CompletedExtrusionCount, extrusionMoves.Count);
            _lastRenderedActiveExtrusionIndex = snapshot.ActiveExtrusionIndex;
            _printedPrintPathVisual.Points = _printedTrajectoryPoints;
        }

        private void RebuildPrintedTrajectoryVisual(PrintPlaybackSnapshot snapshot, IReadOnlyList<GCodeMove> extrusionMoves)
        {
            _printedTrajectoryPoints.Clear();
            for (var index = 0; index < Math.Min(snapshot.CompletedExtrusionCount, extrusionMoves.Count); index++)
            {
                _printedTrajectoryPoints.Add(extrusionMoves[index].Start);
                _printedTrajectoryPoints.Add(extrusionMoves[index].End);
            }

            if (snapshot.ActiveExtrusionIndex >= 0 && snapshot.ActiveExtrusionIndex < extrusionMoves.Count)
            {
                var activeMove = extrusionMoves[snapshot.ActiveExtrusionIndex];
                _printedTrajectoryPoints.Add(activeMove.Start);
                _printedTrajectoryPoints.Add(Lerp(activeMove.Start, activeMove.End, snapshot.ActiveExtrusionProgress));
            }

            _lastRenderedCompletedExtrusionCount = Math.Min(snapshot.CompletedExtrusionCount, extrusionMoves.Count);
            _lastRenderedActiveExtrusionIndex = snapshot.ActiveExtrusionIndex;
            _printedPrintPathVisual.Points = _printedTrajectoryPoints;
        }

        private void UpdateNozzleVisual(PrintPlaybackSnapshot snapshot)
        {
            if (_projectedPrintPath == null)
            {
                _printNozzleVisual.Visible = false;
                return;
            }

            var surfaceSideNormal = GetCameraSideReferencePoint() - snapshot.NozzlePosition;
            if (surfaceSideNormal.Length < 1e-6)
            {
                surfaceSideNormal = new Vector3D(0, 0, 1);
            }
            else
            {
                surfaceSideNormal.Normalize();
            }

            // Кончик сопла должен находиться в точке печати. Для TruncatedConeVisual3D
            // origin соответствует основанию, поэтому сдвигаем основание назад по оси.
            var coneAxisToTip = -surfaceSideNormal;
            var coneBaseOrigin = snapshot.NozzlePosition + surfaceSideNormal * NozzleHeight;

            _printNozzleVisual.Visible = true;
            _printNozzleVisual.Normal = coneAxisToTip;
            _printNozzleVisual.Origin = coneBaseOrigin;
        }

        private void ClearTrajectoryVisuals(bool keepParsedPath)
        {
            _printTimer.Stop();
            _printTrajectoryService.Stop();
            _isPausedByInvalidSurface = false;
            _resumeAfterSurfaceRecovery = false;
            _deformationStatus = !keepParsedPath
                ? "Ожидание G-code."
                : IsMarkerSurfaceProjectionMode
                    ? "Ожидание референса по маркерной поверхности (≥6 ArUco)."
                    : "Ожидание фиксации mesh-референса.";
            _lastDeformationMarkerCount = 0;
            _trajectoryRebuildCount = 0;
            _trajectoryRebuildFailureCount = 0;
            _lastTrajectoryAvgDisplacementMm = 0;
            _lastTrajectoryMaxDisplacementMm = 0;
            _lastTrajectoryRebuildDurationMs = 0;
            _lastTrajectoryRebuildSucceeded = false;
            _lastTrajectoryRebuildCompletedAt = DateTime.MinValue;
            _lastTrajectoryRebuildReason = "Ожидание перестроения.";
            _projectedPrintPath = null;
            _plannedPrintPathVisual.Points = new Point3DCollection();
            _printedTrajectoryPoints.Clear();
            _printedPrintPathVisual.Points = _printedTrajectoryPoints;
            _printDebugNormalVisual.Points = new Point3DCollection();
            _printNozzleVisual.Visible = false;
            _lastRenderedCompletedExtrusionCount = 0;
            _lastRenderedActiveExtrusionIndex = -1;
            _pendingTrajectoryMarkers = null;
            _startPlaybackAfterProjection = false;

            if (!keepParsedPath)
            {
                _parsedGCodePath = null;
                _woundMeshPrintReference = null;
                _surfacePrintReference = null;
                _loadedGCodeFileName = string.Empty;
                _lastTrajectoryMarkerSnapshot.Clear();
            }

            _isInternalScrubUpdate = true;
            _scrubSlider.Value = 0;
            _isInternalScrubUpdate = false;
            UpdateTrajectoryDiagnosticsPanel();
            UpdatePrintControlState();
        }

        /// <summary>
        /// Комплексное отключение всех возможных источников интерлейса
        /// </summary>
        private void ApplyAntiInterlaceSettings()
        {
            try
            {
                // 1. Базовые настройки viewport
                DisableViewportStereoSettings();
                
                // 2. Настройки через рефлексию на RenderHost
                ConfigureRenderHost();
                
                // 3. Настройки уровня WPF
                ConfigureWpfRenderingSettings();
                
                System.Diagnostics.Debug.WriteLine("Применены все настройки против интерлейса");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при применении настроек против интерлейса: {ex.Message}");
            }
        }

        /// <summary>
        /// Отключение стерео настроек на уровне viewport
        /// </summary>
        private void DisableViewportStereoSettings()
        {
            try
            {
                var type = _viewport3D.GetType();
                var allProperties = type.GetProperties();
                foreach (var prop in allProperties)
                {
                    var name = prop.Name.ToLower();
                    if (name.Contains("stereo") || name.Contains("interlace") || name.Contains("interleave"))
                    {
                        try
                        {
                            if (prop.PropertyType == typeof(bool))
                            {
                                prop.SetValue(_viewport3D, false);
                                System.Diagnostics.Debug.WriteLine($"Viewport: Отключено {prop.Name}");
                            }
                            else if (prop.PropertyType.IsEnum)
                            {
                                var enumValues = Enum.GetValues(prop.PropertyType);
                                if (enumValues.Length > 0)
                                {
                                    prop.SetValue(_viewport3D, enumValues.GetValue(0));
                                    System.Diagnostics.Debug.WriteLine($"Viewport: {prop.Name} = {enumValues.GetValue(0)}");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в DisableViewportStereoSettings: {ex.Message}");
            }
        }

        /// <summary>
        /// Настройка RenderHost для устранения интерлейса
        /// </summary>
        private void ConfigureRenderHost()
        {
            try
            {
                var renderHostProperty = _viewport3D.GetType().GetProperty("RenderHost");
                if (renderHostProperty != null)
                {
                    var renderHost = renderHostProperty.GetValue(_viewport3D);
                    if (renderHost != null)
                    {
                        var hostType = renderHost.GetType();
                        
                        // Настраиваем RenderConfiguration
                        var configProperty = hostType.GetProperty("RenderConfiguration");
                        if (configProperty != null)
                        {
                            var config = configProperty.GetValue(renderHost);
                            if (config != null)
                            {
                                var configType = config.GetType();
                                
                                // Отключаем стерео режим
                                var stereoProperty = configType.GetProperty("StereoMode");
                                if (stereoProperty != null)
                                {
                                    stereoProperty.SetValue(config, 0); // None
                                    System.Diagnostics.Debug.WriteLine("RenderHost: StereoMode = None");
                                }
                                
                                // Отключаем MSAA (может вызывать проблемы)
                                var msaaProperty = configType.GetProperty("MSAALevel");
                                if (msaaProperty != null)
                                {
                                    msaaProperty.SetValue(config, 0); // Off
                                    System.Diagnostics.Debug.WriteLine("RenderHost: MSAA = Off");
                                }
                                
                                // Отключаем все возможные буферы
                                foreach (var prop in configType.GetProperties())
                                {
                                    var name = prop.Name.ToLower();
                                    if ((name.Contains("buffer") || name.Contains("depth") || name.Contains("stencil")) && prop.PropertyType == typeof(bool))
                                    {
                                        try
                                        {
                                            prop.SetValue(config, false);
                                            System.Diagnostics.Debug.WriteLine($"RenderHost: Отключено {prop.Name}");
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        
                        // Отключаем интерлейс на уровне хоста
                        foreach (var prop in hostType.GetProperties())
                        {
                            var name = prop.Name.ToLower();
                            if ((name.Contains("interlace") || name.Contains("interleave") || name.Contains("stereo")) && prop.PropertyType == typeof(bool))
                            {
                                try
                                {
                                    prop.SetValue(renderHost, false);
                                    System.Diagnostics.Debug.WriteLine($"RenderHost: Отключено {prop.Name}");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в ConfigureRenderHost: {ex.Message}");
            }
        }

        /// <summary>
        /// Настройка рендеринга на уровне WPF
        /// </summary>
        private void ConfigureWpfRenderingSettings()
        {
            try
            {
                // Отключаем кэширование, которое может вызывать проблемы
                _viewport3D.CacheMode = null;
                
                // Устанавливаем режим рендеринга
                _viewport3D.SnapsToDevicePixels = true;
                _viewport3D.UseLayoutRounding = true;
                
                System.Diagnostics.Debug.WriteLine("WPF: Применены настройки рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в ConfigureWpfRenderingSettings: {ex.Message}");
            }
        }

        /// <summary>
        /// Финальное отключение стерео эффектов после загрузки
        /// </summary>
        private void DisableStereoEffects()
        {
            try
            {
                // Повторно применяем все настройки после полной загрузки
                ApplyAntiInterlaceSettings();
                
                // Принудительно обновляем рендеринг
                _viewport3D.InvalidateVisual();
                _viewport3D.UpdateLayout();
                
                System.Diagnostics.Debug.WriteLine("Финальное отключение стерео эффектов завершено");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в финальном DisableStereoEffects: {ex.Message}");
            }
        }

        /// <summary>
        /// Попытка альтернативного режима рендеринга
        /// </summary>
        private void TryAlternativeRenderMode()
        {
            try
            {
                // Пробуем установить программный рендеринг через рефлексию
                try
                {
                    var processRenderModeProperty = typeof(System.Windows.Media.RenderOptions).GetProperty("ProcessRenderMode");
                    if (processRenderModeProperty != null)
                    {
                        processRenderModeProperty.SetValue(null, 1); // SoftwareOnly
                        System.Diagnostics.Debug.WriteLine("Установлен программный режим рендеринга");
                    }
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine("Не удалось установить программный режим рендеринга");
                }
                
                // Альтернативные настройки
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(_viewport3D, BitmapScalingMode.Linear);
                System.Windows.Media.RenderOptions.SetEdgeMode(_viewport3D, EdgeMode.Unspecified);
                
                // Принудительно пересоздаем рендер контекст
                _viewport3D.InvalidateVisual();
                
                System.Diagnostics.Debug.WriteLine("Применен альтернативный режим рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в TryAlternativeRenderMode: {ex.Message}");
            }
        }

        /// <summary>
        /// Финальные попытки исправить рендеринг
        /// </summary>
        private void FinalRenderingFix()
        {
            try
            {
                // Последняя попытка - полное отключение всех возможных эффектов
                _viewport3D.Effect = null;
                // BitmapEffect устарело в .NET, убираем
                
                // Принудительно убираем все трансформации которые могут влиять на рендеринг
                _viewport3D.RenderTransform = null;
                _viewport3D.LayoutTransform = null;
                
                // Финальная настройка viewport
                _viewport3D.ClipToBounds = true;
                
                // Принудительное обновление всей иерархии
                _viewport3D.InvalidateArrange();
                _viewport3D.InvalidateMeasure();
                _viewport3D.InvalidateVisual();
                _viewport3D.UpdateLayout();
                
                System.Diagnostics.Debug.WriteLine("Применены финальные исправления рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в FinalRenderingFix: {ex.Message}");
            }
        }

        /// <summary>
        /// Привязывает визуальный контрол к модели 3D-сцены.
        /// 
        /// При повторной привязке старый обработчик обязательно снимается, иначе
        /// один и тот же UI начал бы обновляться несколько раз на одно событие.
        /// </summary>
        /// <param name="scene3DService">Сервис 3D сцены</param>
        public void BindToService(Scene3DService scene3DService)
        {
            try
            {
                if (_scene3DService != null)
                {
                    _scene3DService.OnSceneUpdated -= UpdateScene;
                }

                _scene3DService = scene3DService;
                _scene3DService.OnSceneUpdated += UpdateScene;
                
                // Первоначальное обновление
                UpdateScene();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка привязки к сервису 3D сцены: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет все визуальные части сцены после события Scene3DService.
        /// 
        /// Dispatcher нужен потому, что событие может прийти не из WPF-потока.
        /// Внутри метод разделён на камеры, маркеры и инфопанель, чтобы проще
        /// ограничивать частоту тяжёлых обновлений.
        /// </summary>
        private void UpdateScene()
        {
            if (_scene3DService == null)
                return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateCameras();
                    UpdateMarkers();
                    UpdateInfoPanel();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления 3D сцены: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление позиций камер
        /// </summary>
        private void UpdateCameras()
        {
            if (_scene3DService == null)
                return;

            try
            {
                bool isCalibrated = _scene3DService.IsCalibrated;
                var cam1Pos = _scene3DService.Camera1Position;
                var cam2Pos = _scene3DService.Camera2Position;
                var centerPos = _scene3DService.StereoCenter;
                var cameraStateChanged = !_cameraVisualsInitialized ||
                                         _lastCameraCalibrationState != isCalibrated ||
                                         Distance(_lastCamera1Position, cam1Pos) > CameraPositionUpdateThresholdMm ||
                                         Distance(_lastCamera2Position, cam2Pos) > CameraPositionUpdateThresholdMm ||
                                         Distance(_lastStereoCenterPosition, centerPos) > CameraPositionUpdateThresholdMm;

                if (!cameraStateChanged)
                    return;

                _cameraVisualsInitialized = true;
                var wasCalibrated = _lastCameraCalibrationState;
                _lastCameraCalibrationState = isCalibrated;
                _lastCamera1Position = cam1Pos;
                _lastCamera2Position = cam2Pos;
                _lastStereoCenterPosition = centerPos;

                if (isCalibrated && !wasCalibrated && _scene3DService != null)
                    WoundDiagnosticsSessionRecorder.Instance.LogCalibration("viewport_calibration_applied", _scene3DService);
                
                // Показать/скрыть камеры
                _camera1Visual.Visible = isCalibrated;
                _camera2Visual.Visible = isCalibrated;
                _camera1LensVisual.Visible = isCalibrated;
                _camera2LensVisual.Visible = isCalibrated;
                _stereoCenterVisual.Visible = isCalibrated;
                
                // Управление видимостью текстовых элементов через добавление/удаление
                if (isCalibrated)
                {
                    // Добавляем текстовые элементы если их нет
                    if (!_viewport3D.Children.Contains(_camera1Text))
                        _viewport3D.Children.Add(_camera1Text);
                    if (!_viewport3D.Children.Contains(_camera2Text))
                        _viewport3D.Children.Add(_camera2Text);
                    if (!_viewport3D.Children.Contains(_centerText))
                        _viewport3D.Children.Add(_centerText);
                    if (!_viewport3D.Children.Contains(_cameraBaselineVisual))
                        _viewport3D.Children.Add(_cameraBaselineVisual);
                    if (!_viewport3D.Children.Contains(_stereoAxisVisual))
                        _viewport3D.Children.Add(_stereoAxisVisual);
                    if (!_viewport3D.Children.Contains(_baselineText))
                        _viewport3D.Children.Add(_baselineText);

                    // Камера 1 (смещена от центра координат)
                    _camera1Visual.Center = new Point3D(cam1Pos.X, cam1Pos.Y, cam1Pos.Z);
                    _camera1LensVisual.Center = GetCameraLensPoint(cam1Pos);
                    _camera1Text.Position = new Point3D(cam1Pos.X + 15, cam1Pos.Y + 15, cam1Pos.Z + 15);
                    _camera1Text.Text = $"Камера 1\n({cam1Pos.X:F0}, {cam1Pos.Y:F0}, {cam1Pos.Z:F0}) мм";

                    // Камера 2
                    _camera2Visual.Center = new Point3D(cam2Pos.X, cam2Pos.Y, cam2Pos.Z);
                    _camera2LensVisual.Center = GetCameraLensPoint(cam2Pos);
                    _camera2Text.Position = new Point3D(cam2Pos.X + 15, cam2Pos.Y + 15, cam2Pos.Z + 15);
                    _camera2Text.Text = $"Камера 2\n({cam2Pos.X:F0}, {cam2Pos.Y:F0}, {cam2Pos.Z:F0}) мм";

                    // Центр стереосистемы
                    _stereoCenterVisual.Origin = new Point3D(centerPos.X, centerPos.Y, centerPos.Z);
                    _centerText.Position = new Point3D(centerPos.X + 15, centerPos.Y + 15, centerPos.Z + 15);
                    _centerText.Text = $"Центр\n({centerPos.X:F0}, {centerPos.Y:F0}, {centerPos.Z:F0}) мм";

                    UpdateStereoGuides(cam1Pos, cam2Pos, centerPos);
                }
                else
                {
                    // Удаляем текстовые элементы если калибровка не выполнена
                    if (_viewport3D.Children.Contains(_camera1Text))
                        _viewport3D.Children.Remove(_camera1Text);
                    if (_viewport3D.Children.Contains(_camera2Text))
                        _viewport3D.Children.Remove(_camera2Text);
                    if (_viewport3D.Children.Contains(_centerText))
                        _viewport3D.Children.Remove(_centerText);
                    if (_viewport3D.Children.Contains(_cameraBaselineVisual))
                        _viewport3D.Children.Remove(_cameraBaselineVisual);
                    if (_viewport3D.Children.Contains(_stereoAxisVisual))
                        _viewport3D.Children.Remove(_stereoAxisVisual);
                    if (_viewport3D.Children.Contains(_baselineText))
                        _viewport3D.Children.Remove(_baselineText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления камер: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление направляющих между камерами и рабочей зоной маркеров
        /// </summary>
        private void UpdateStereoGuides(Point3D cam1Pos, Point3D cam2Pos, Point3D centerPos)
        {
            var axisEnd = new Point3D(centerPos.X, centerPos.Y + StereoAxisLength, centerPos.Z);

            _cameraBaselineVisual.Points = new Point3DCollection
            {
                cam1Pos,
                cam2Pos
            };

            _stereoAxisVisual.Points = new Point3DCollection
            {
                centerPos,
                axisEnd
            };

            _baselineText.Position = new Point3D(centerPos.X, centerPos.Y - 35, centerPos.Z + 20);
            _baselineText.Text = $"Базовая линия камер\n{Distance(cam1Pos, cam2Pos):F0} мм";
        }

        /// <summary>
        /// Синхронизирует визуальные объекты маркеров с MarkerPositions сервиса.
        /// 
        /// Метод удаляет сферы для исчезнувших ID, создаёт новые сферы для новых ID,
        /// обновляет существующие, а затем при необходимости перестраивает поверхность.
        /// Таблица и сортировка обновляются не на каждый кадр, чтобы не вызывать лаги.
        /// </summary>
        private void UpdateMarkers()
        {
            if (_scene3DService == null)
                return;

            try
            {
                var currentMarkers = _scene3DService.MarkerPositions;
                var currentIds = new HashSet<int>(currentMarkers.Keys);
                var existingIds = new HashSet<int>(_markerVisuals.Keys);
                var markerSetChanged = currentIds.Count != existingIds.Count || currentIds.Any(id => !existingIds.Contains(id));
                var shouldUpdateMarkerTable = ShouldUpdateMarkerTable();

                // Удаляем маркеры, которых больше нет
                foreach (var id in existingIds)
                {
                    if (!currentIds.Contains(id))
                    {
                        RemoveMarkerVisual(id);
                    }
                }

                // Добавляем или обновляем маркеры в стабильном порядке отображения
                var shouldUpdateMarkerText = ShouldUpdateMarkerText();
                foreach (var marker in currentMarkers.OrderBy(m => GetMarkerDisplayIndex(m.Key)).ThenBy(m => m.Key))
                {
                    if (_markerVisuals.ContainsKey(marker.Key))
                    {
                        // Обновляем существующий маркер
                        UpdateMarkerVisual(marker.Key, marker.Value, shouldUpdateMarkerText, shouldUpdateMarkerTable);
                    }
                    else
                    {
                        // Создаем новый маркер
                        CreateMarkerVisual(marker.Key, marker.Value);
                    }
                }

                UpdateMarkerGuideLines(currentMarkers);
                UpdateMarkerSurface(currentMarkers);
                UpdateWoundModel();
                TryRebuildProjectedTrajectory(currentMarkers);
                if (markerSetChanged || shouldUpdateMarkerTable)
                {
                    SortMarkersTable();
                }

                if (!_scene3DService.IsCalibrated)
                {
                    ClearMarkerGuideLines();
                    ClearMarkerSurface();
                    UpdateWoundModel();
                    if (_projectedPrintPath != null)
                    {
                        ClearTrajectoryVisuals(keepParsedPath: true);
                        _woundMeshPrintReference = null;
                        _surfacePrintReference = null;
                        SetGCodeStatus(
                            string.IsNullOrWhiteSpace(_loadedGCodeFileName)
                                ? "G-code не загружен"
                                : $"{_loadedGCodeFileName}: ожидание калибровки.");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления маркеров: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт тонкий плоский чип, подпись и строку таблицы для нового маркера.
        /// </summary>
        private void CreateMarkerVisual(int markerId, Point3D position)
        {
            try
            {
                var baseColor = GetMarkerColor(markerId);
                var extent = MarkerChipExtentMm * 2.0;
                var markerChip = new BoxVisual3D
                {
                    Center = position,
                    Length = extent,
                    Width = extent,
                    Height = MarkerChipThicknessMm,
                    Fill = new SolidColorBrush(Color.FromArgb(MarkerChipFillAlpha, baseColor.R, baseColor.G, baseColor.B))
                };

                var displayIndex = GetMarkerDisplayIndex(markerId);
                var markerName = GetMarkerName(markerId);
                var markerHudText = BuildMarkerHudText(markerId, position, markerName);

                var markerText = new TextVisual3D
                {
                    Position = new Point3D(position.X + 8, position.Y + 8, position.Z + 8),
                    Text = markerHudText,
                    Foreground = Brushes.Black,
                    FontSize = 10
                };

                _viewport3D.Children.Add(markerChip);
                _viewport3D.Children.Add(markerText);

                _markerVisuals[markerId] = markerChip;
                _markerTexts[markerId] = markerText;
                _markerTextCache[markerId] = markerText.Text;
                
                // Добавляем в таблицу координат
                var distance = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
                var newMarker = new MarkerCoordinate
                {
                    ID = markerId,
                    DisplayIndex = displayIndex,
                    Name = $"ArUco {markerId}",
                    X = position.X.ToString("F0"),
                    Y = position.Y.ToString("F0"),
                    Z = position.Z.ToString("F0"),
                    Distance = distance.ToString("F1")
                };
                AddMarkerDataSorted(newMarker);
                
                System.Diagnostics.Debug.WriteLine($"3D: Создан {markerName}, ArUco {markerId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка создания маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет положение существующего маркера (чипа).
        /// </summary>
        private void UpdateMarkerVisual(int markerId, Point3D position, bool updateText, bool updateTable)
        {
            try
            {
                if (_markerVisuals.TryGetValue(markerId, out var chip) &&
                    _markerTexts.TryGetValue(markerId, out var text))
                {
                    var displayIndex = GetMarkerDisplayIndex(markerId);
                    var markerHudText = BuildMarkerHudText(markerId, position, GetMarkerName(markerId));

                    chip.Center = position;
                    if (updateText)
                    {
                        text.Position = new Point3D(position.X + 8, position.Y + 8, position.Z + 8);
                        var markerText = markerHudText;
                        if (!_markerTextCache.TryGetValue(markerId, out var previousText) || previousText != markerText)
                        {
                            text.Text = markerText;
                            _markerTextCache[markerId] = markerText;
                        }
                    }
                    
                    // Обновляем данные в таблице
                    if (updateTable)
                    {
                        var markerData = _markersData.FirstOrDefault(m => m.ID == markerId);
                        if (markerData != null)
                        {
                            var distance = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
                            markerData.DisplayIndex = displayIndex;
                            markerData.Name = $"ArUco {markerId}";
                            markerData.X = position.X.ToString("F0");
                            markerData.Y = position.Y.ToString("F0");
                            markerData.Z = position.Z.ToString("F0");
                            markerData.Distance = distance.ToString("F1");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Удаление визуального представления маркера
        /// </summary>
        private void RemoveMarkerVisual(int markerId)
        {
            try
            {
                if (_markerVisuals.TryGetValue(markerId, out var visual))
                {
                    _viewport3D.Children.Remove(visual);
                    _markerVisuals.Remove(markerId);
                }

                if (_markerTexts.TryGetValue(markerId, out var text))
                {
                    _viewport3D.Children.Remove(text);
                    _markerTexts.Remove(markerId);
                    _markerTextCache.Remove(markerId);
                }

                // Удаляем из таблицы (только реальные маркеры, не системные объекты)
                var markerData = _markersData.FirstOrDefault(m => m.ID == markerId && m.ID >= 0);
                if (markerData != null)
                {
                    _markersData.Remove(markerData);
                    System.Diagnostics.Debug.WriteLine($"3D: Удален маркер {markerId} из таблицы");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка удаления маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет тонкие линии от камер к маркерам одним общим LinesVisual3D.
        /// Это заметно дешевле, чем держать отдельный визуальный объект на каждый ID.
        /// </summary>
        private void UpdateMarkerGuideLines(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (_scene3DService == null || !_scene3DService.IsCalibrated)
            {
                ClearMarkerGuideLines();
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastGuideLinesUpdateTime).TotalMilliseconds < GuideLinesUpdateIntervalMs)
                return;

            _lastGuideLinesUpdateTime = now;

            if (currentMarkers.Count == 0)
            {
                ClearMarkerGuideLines();
                return;
            }

            var cam1Pos = _scene3DService.Camera1Position;
            var cam2Pos = _scene3DService.Camera2Position;
            var cam1GuideStart = GetCameraLensPoint(cam1Pos);
            var cam2GuideStart = GetCameraLensPoint(cam2Pos);
            var points = new Point3DCollection(currentMarkers.Count * 4);

            foreach (var marker in currentMarkers.OrderBy(m => GetMarkerDisplayIndex(m.Key)).ThenBy(m => m.Key))
            {
                points.Add(cam1GuideStart);
                points.Add(marker.Value);
                points.Add(cam2GuideStart);
                points.Add(marker.Value);
            }

            _markerGuideLinesVisual.Points = points;
        }

        private static Point3D GetCameraLensPoint(Point3D cameraCenter)
        {
            return new Point3D(cameraCenter.X, cameraCenter.Y + CameraLensOffset, cameraCenter.Z);
        }

        private void ClearMarkerGuideLines()
        {
            if (_markerGuideLinesVisual.Points.Count > 0)
                _markerGuideLinesVisual.Points = new Point3DCollection();
        }

        /// <summary>
        /// Ограничивает частоту обновления WPF DataGrid.
        /// DataGrid заметно тяжелее сфер в HelixViewport3D, поэтому частые
        /// PropertyChanged на каждую координату могут давать лаги.
        /// </summary>
        private bool ShouldUpdateMarkerTable()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMarkerTableUpdateTime).TotalMilliseconds < MarkerTableUpdateIntervalMs)
                return false;

            _lastMarkerTableUpdateTime = now;
            return true;
        }

        private bool ShouldUpdateMarkerText()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMarkerTextUpdateTime).TotalMilliseconds < MarkerTextUpdateIntervalMs)
                return false;

            _lastMarkerTextUpdateTime = now;
            return true;
        }

        /// <summary>
        /// Обновляет полупрозрачную поверхность, проходящую через текущие маркеры.
        /// 
        /// Поверхность строится только при наличии минимум трёх точек и только
        /// когда позиции заметно изменились. Это компромисс: пользователь видит
        /// деформацию таблички, но алгоритм триангуляции сетки не запускается
        /// на каждый кадр видеопотока.
        /// </summary>
        private void UpdateMarkerSurface(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (_showWoundModelCheckBox.IsChecked == true &&
                _woundModelService.HasModel &&
                !IsMarkerSurfaceProjectionMode)
            {
                ClearMarkerSurface();
                return;
            }

            var surfaceMarkers = GetSurfaceMarkerCandidates(currentMarkers);
            if (surfaceMarkers.Count < 3)
            {
                ClearMarkerSurface();
                return;
            }

            if (!ShouldUpdateMarkerSurface(surfaceMarkers))
                return;

            var markerPoints = surfaceMarkers
                .Select(marker => marker.Value)
                .ToList();

            var mesh = BuildMarkerSurfaceMesh(markerPoints);
            _markerSurfaceMesh.Positions = mesh.Positions;
            _markerSurfaceMesh.TriangleIndices = mesh.TriangleIndices;

            _lastSurfaceMarkerSnapshot.Clear();
            _lastSurfaceMarkerIds.Clear();
            foreach (var marker in surfaceMarkers)
            {
                _lastSurfaceMarkerSnapshot[marker.Key] = marker.Value;
                _lastSurfaceMarkerIds.Add(marker.Key);
            }

            _surfaceTopologyChangeDetectedAt = DateTime.MinValue;
            _lastSurfaceUpdateTime = DateTime.UtcNow;
        }

        private List<KeyValuePair<int, Point3D>> GetSurfaceMarkerCandidates(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            return currentMarkers
                .OrderBy(marker => GetMarkerDisplayIndex(marker.Key))
                .ThenBy(marker => marker.Key)
                .Take(MaxSurfaceMarkers)
                .ToList();
        }

        private List<KeyValuePair<int, Point3D>> GetTrajectoryMarkerCandidates(IReadOnlyDictionary<int, Point3D> currentMarkers)
            => GetSurfaceMarkerCandidates(currentMarkers);

        private bool TryGetCurrentWoundMeshSceneSnapshot(
            out List<Point3D> meshVerticesScene,
            out List<int> meshTriangles)
        {
            meshVerticesScene = new List<Point3D>();
            meshTriangles = new List<int>();

            if (!_woundModelService.HasMesh || _woundModelService.Mesh == null)
                return false;

            var mesh = _woundModelService.Mesh;
            if (mesh.Positions == null || mesh.TriangleIndices == null)
                return false;
            if (mesh.Positions.Count < 3 || mesh.TriangleIndices.Count < 3)
                return false;

            var transformToScene = _scene3DService != null && _scene3DService.IsCalibrated;
            meshVerticesScene = new List<Point3D>(mesh.Positions.Count);
            foreach (var point in mesh.Positions)
            {
                meshVerticesScene.Add(transformToScene
                    ? _scene3DService!.ConvertCamera1PointToScene(point)
                    : point);
            }

            meshTriangles = mesh.TriangleIndices.ToList();
            return true;
        }

        private void UpdateWoundModel()
        {
            if (!_woundModelService.HasModel)
                return;

            // Тот же сглаженный поток TTL, что и MarkerPositions во вьюпорте — иначе меш следует другим XYZ, чем чипы.
            IReadOnlyDictionary<int, Point3D> markersForWound =
                _scene3DService != null && _scene3DService.IsCalibrated
                    ? _scene3DService.MarkerPositionsCamera1Mm
                    : new Dictionary<int, Point3D>();

            if (_scene3DService != null && _scene3DService.IsCalibrated)
            {
                var stereoScene = _scene3DService.StereoCenter;
                var stereoCamera1 = _scene3DService.ConvertScenePointToCamera1(stereoScene);
                _woundModelService.SetStereoLookTargetCamera1(stereoCamera1);
            }

            if (_woundModelService.TryUpdate(markersForWound) && _woundModelService.Mesh != null)
            {
                _woundModelMesh = _woundModelService.Mesh;
            }

            if (_showWoundModelCheckBox.IsChecked == true)
                ApplyWoundModelMaterial();
            UpdateWoundModelTransform();
            UpdateWoundDiagnosticsVisual();

            SetWoundModelStatus(_woundModelService.Status);

            if (_scene3DService != null &&
                _scene3DService.IsCalibrated &&
                _woundModelService.HasModel &&
                ShouldThrottleViewportDiag(ref _lastViewportParityLogUtc, ViewportMarkerParityLogIntervalMs))
            {
                WoundDiagnosticsSessionRecorder.Instance.LogViewportMarkerParity(
                    _scene3DService,
                    _scene3DService.MarkerPositions,
                    _scene3DService.MarkerPositionsCamera1Mm,
                    _scene3DService.MarkerPositionsCamera1RawMm,
                    _woundModelService.ActiveDeformationMarkerIds);
            }
        }

        private static bool ShouldThrottleViewportDiag(ref DateTime lastUtc, int intervalMs)
        {
            var now = DateTime.UtcNow;
            if ((now - lastUtc).TotalMilliseconds < intervalMs)
                return false;
            lastUtc = now;
            return true;
        }

        private static Point3DCollection BuildPredictedGizmoCrossPoints(Point3D center, double halfExtentMm)
        {
            var x = center.X;
            var y = center.Y;
            var z = center.Z;
            return new Point3DCollection(new[]
            {
                new Point3D(x - halfExtentMm, y, z), new Point3D(x + halfExtentMm, y, z),
                new Point3D(x, y - halfExtentMm, z), new Point3D(x, y + halfExtentMm, z),
                new Point3D(x, y, z - halfExtentMm), new Point3D(x, y, z + halfExtentMm),
            });
        }

        private void UpdateWoundDiagnosticsVisual()
        {
            if (!_showDeformationDebugOverlay ||
                _scene3DService == null ||
                !_scene3DService.IsCalibrated ||
                _showWoundModelCheckBox.IsChecked != true ||
                !_woundModelService.HasModel)
            {
                _woundMarkerFitDebugVisual.Points = new Point3DCollection();
                ClearWoundPredictedGizmoVisuals();
                return;
            }

            var points = new Point3DCollection();
            var activeGizmoIds = new HashSet<int>();
            foreach (var pair in _woundModelService.LastPredictedMarkerPositionsCamera1)
            {
                if (!_woundModelService.LastObservedMarkerPositionsCamera1.TryGetValue(pair.Key, out var observedCamera1))
                    continue;

                var observedScene = _scene3DService.ConvertCamera1PointToScene(observedCamera1);
                var predictedScene = _scene3DService.ConvertCamera1PointToScene(pair.Value);
                points.Add(observedScene);
                points.Add(predictedScene);
                UpdateWoundPredictedGizmoVisual(pair.Key, predictedScene);
                activeGizmoIds.Add(pair.Key);
            }

            RemoveStaleWoundPredictedGizmoVisuals(activeGizmoIds);

            if (TryGetMeshAndMarkerSceneStats(out var meshStatsScene, out var markerStatsScene))
            {
                points.Add(markerStatsScene.Center);
                points.Add(meshStatsScene.Center);
            }

            _woundMarkerFitDebugVisual.Points = points;
        }

        private void UpdateWoundPredictedGizmoVisual(int markerId, Point3D position)
        {
            if (!_woundPredictedGizmoVisuals.TryGetValue(markerId, out var lines))
            {
                lines = new LinesVisual3D
                {
                    Color = GetMarkerColor(markerId),
                    Thickness = 1
                };
                _woundPredictedGizmoVisuals[markerId] = lines;
                _viewport3D.Children.Add(lines);
            }

            lines.Points = BuildPredictedGizmoCrossPoints(position, PredictedGizmoHalfExtentMm);
        }

        private void RemoveStaleWoundPredictedGizmoVisuals(IReadOnlySet<int> activeIds)
        {
            var staleIds = _woundPredictedGizmoVisuals.Keys
                .Where(id => !activeIds.Contains(id))
                .ToList();
            foreach (var staleId in staleIds)
            {
                _viewport3D.Children.Remove(_woundPredictedGizmoVisuals[staleId]);
                _woundPredictedGizmoVisuals.Remove(staleId);
            }
        }

        private void ClearWoundPredictedGizmoVisuals()
        {
            foreach (var lines in _woundPredictedGizmoVisuals.Values)
            {
                _viewport3D.Children.Remove(lines);
            }

            _woundPredictedGizmoVisuals.Clear();
        }

        private bool TryGetMeshAndMarkerCamera1Stats(
            out PointCloudStats meshStatsCamera1,
            out PointCloudStats markerStatsCamera1)
        {
            meshStatsCamera1 = default;
            markerStatsCamera1 = default;

            if (_scene3DService == null ||
                !_woundModelService.HasMesh ||
                _woundModelService.Mesh?.Positions == null ||
                _woundModelService.Mesh.Positions.Count == 0)
            {
                return false;
            }

            var activeMarkerIds = _woundModelService.LastObservedMarkerPositionsCamera1.Count > 0
                ? _woundModelService.LastObservedMarkerPositionsCamera1.Keys.ToHashSet()
                : _scene3DService.MarkerPositionsCamera1Mm.Keys.ToHashSet();
            var markerPoints = _scene3DService.MarkerPositionsCamera1Mm
                .Where(marker => activeMarkerIds.Contains(marker.Key))
                .Select(marker => marker.Value)
                .ToList();

            if (markerPoints.Count == 0)
                return false;

            meshStatsCamera1 = CalculatePointCloudStats(_woundModelService.Mesh.Positions);
            markerStatsCamera1 = CalculatePointCloudStats(markerPoints);
            return true;
        }

        private bool TryGetMeshAndMarkerSceneStats(
            out PointCloudStats meshStatsScene,
            out PointCloudStats markerStatsScene)
        {
            meshStatsScene = default;
            markerStatsScene = default;

            if (_scene3DService == null ||
                !_scene3DService.IsCalibrated ||
                !_woundModelService.HasMesh ||
                _woundModelService.Mesh?.Positions == null ||
                _woundModelService.Mesh.Positions.Count == 0)
            {
                return false;
            }

            var activeMarkerIds = _woundModelService.LastObservedMarkerPositionsCamera1.Count > 0
                ? _woundModelService.LastObservedMarkerPositionsCamera1.Keys.ToHashSet()
                : _scene3DService.MarkerPositions.Keys.ToHashSet();
            var markerPoints = _scene3DService.MarkerPositions
                .Where(marker => activeMarkerIds.Contains(marker.Key))
                .Select(marker => marker.Value)
                .ToList();

            if (markerPoints.Count == 0)
                return false;

            var meshPointsScene = _woundModelService.Mesh.Positions
                .Select(point => _scene3DService.ConvertCamera1PointToScene(point))
                .ToList();
            meshStatsScene = CalculatePointCloudStats(meshPointsScene);
            markerStatsScene = CalculatePointCloudStats(markerPoints);
            return true;
        }

        private static PointCloudStats CalculatePointCloudStats(IEnumerable<Point3D> points)
        {
            var pointList = points.ToList();
            if (pointList.Count == 0)
                return new PointCloudStats(new Point3D(), new Vector3D(), 0);

            var minX = pointList.Min(point => point.X);
            var maxX = pointList.Max(point => point.X);
            var minY = pointList.Min(point => point.Y);
            var maxY = pointList.Max(point => point.Y);
            var minZ = pointList.Min(point => point.Z);
            var maxZ = pointList.Max(point => point.Z);
            var center = new Point3D(
                (minX + maxX) / 2.0,
                (minY + maxY) / 2.0,
                (minZ + maxZ) / 2.0);
            var size = new Vector3D(maxX - minX, maxY - minY, maxZ - minZ);
            return new PointCloudStats(center, size, pointList.Count);
        }

        private static double VectorLength(Vector3D vector)
        {
            return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        }

        private string? TryWriteWoundDiagnosticsFile()
        {
            if (_scene3DService == null || !_woundModelService.HasModel)
                return null;

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wound_diagnostics_latest.txt");
            if (_scene3DService.LastTriangulatedFreshMarkerCount == 0 ||
                _scene3DService.MarkerPositionsCamera1RawMm.Count == 0)
            {
                return File.Exists(path) ? path : null;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastWoundDiagnosticsWriteTime).TotalMilliseconds < WoundDiagnosticsFileWriteIntervalMs)
            {
                return path;
            }

            _lastWoundDiagnosticsWriteTime = now;
            try
            {
                File.WriteAllText(path, BuildWoundDiagnosticsReport(now));
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wound_diagnostics_latest.json"),
                    JsonConvert.SerializeObject(BuildWoundDiagnosticsJson(now), Formatting.Indented));
                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка записи wound diagnostics: {ex.Message}");
                return null;
            }
        }

        private string BuildWoundDiagnosticsReport(DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("StereoCalibration wound diagnostics");
            sb.AppendLine($"utc: {nowUtc:O}");
            sb.AppendLine($"calibrated: {_scene3DService?.IsCalibrated}");
            sb.AppendLine($"freshStereoMarkers: {_scene3DService?.LastTriangulatedFreshMarkerCount}");
            sb.AppendLine($"sceneMarkers: {_scene3DService?.MarkerPositions.Count}");
            sb.AppendLine();

            AppendPoint(sb, "camera1.scene", _scene3DService?.Camera1Position ?? default);
            AppendPoint(sb, "camera2.scene", _scene3DService?.Camera2Position ?? default);
            AppendPoint(sb, "stereoCenter.scene", _scene3DService?.StereoCenter ?? default);
            sb.AppendLine();

            sb.AppendLine("[wound-status]");
            sb.AppendLine($"loadedFile: {_woundModelService.LoadedFileName}");
            sb.AppendLine($"status: {_woundModelService.Status}");
            sb.AppendLine($"referenceReason: {_woundModelService.LastReferenceReason}");
            sb.AppendLine($"linkedMarkers: {_woundModelService.LinkedMarkerCount}");
            sb.AppendLine($"activeMarkers: {_woundModelService.ActiveMarkerCount}");
            sb.AppendLine($"visibleActiveMarkers: {_woundModelService.LastVisibleActiveMarkerCount}");
            sb.AppendLine($"fallbackMarkers: {_woundModelService.LastFallbackMarkerCount}");
            sb.AppendLine($"alignRmseMm: {_woundModelService.LastAlignmentRmseMm:F6}");
            sb.AppendLine($"fitRmseMm: {_woundModelService.LastMarkerFitRmseMm:F6}");
            sb.AppendLine($"fitMaxMm: {_woundModelService.LastMarkerFitMaxMm:F6}");
            sb.AppendLine($"fitWorstMarkerId: {_woundModelService.LastMarkerFitWorstMarkerId}");
            sb.AppendLine($"activeScaleMode: {_woundModelService.ActiveModelScaleMode}");
            sb.AppendLine($"activeScaleMultiplier: {_woundModelService.ActiveModelScaleMultiplier:F9}");
            sb.AppendLine($"captureCombinedScale: {_woundModelService.LastCaptureCombinedScale:F9}");
            AppendVector(sb, "captureTranslationMm", _woundModelService.LastCaptureTranslationMm);
            sb.AppendLine($"referenceMarkerBiasRmseMm: {_woundModelService.LastReferenceMarkerBiasRmseMm:F6}");
            sb.AppendLine($"globalCorrectionApplied: {_woundModelService.LastGlobalCorrectionApplied}");
            sb.AppendLine($"globalCorrectionScale: {_woundModelService.LastGlobalCorrectionScale:F9}");
            sb.AppendLine($"globalCorrectionTranslationNormMm: {_woundModelService.LastGlobalCorrectionTranslationNormMm:F6}");
            sb.AppendLine();

            AppendStatsSection(sb, "referenceMesh.model", _woundModelService.TryGetReferenceMeshStats, null);
            AppendStatsSection(sb, "modelMarkers.model", _woundModelService.TryGetModelMarkerStats, null);
            if (_woundModelService.TryGetReferenceMeshStats(out var refMeshCenter, out _, out _) &&
                _woundModelService.TryGetModelMarkerStats(out var refMarkerCenter, out _, out _))
            {
                sb.AppendLine($"referenceMesh_to_modelMarkers_centerDistanceMm: {Distance(refMeshCenter, refMarkerCenter):F6}");
            }

            if (TryGetMeshAndMarkerCamera1Stats(out var meshCamera1Stats, out var markerCamera1Stats))
            {
                AppendStats(sb, "mesh.camera1", meshCamera1Stats);
                AppendStats(sb, "activeMarkers.camera1", markerCamera1Stats);
                sb.AppendLine($"mesh_to_activeMarkers_camera1_centerDistanceMm: {Distance(meshCamera1Stats.Center, markerCamera1Stats.Center):F6}");
            }

            if (TryGetMeshAndMarkerSceneStats(out var meshSceneStats, out var markerSceneStats))
            {
                AppendStats(sb, "mesh.scene", meshSceneStats);
                AppendStats(sb, "activeMarkers.scene", markerSceneStats);
                sb.AppendLine($"mesh_to_activeMarkers_scene_centerDistanceMm: {Distance(meshSceneStats.Center, markerSceneStats.Center):F6}");
            }

            sb.AppendLine();
            sb.AppendLine("[marker-pairs]");
            sb.AppendLine("id;observed.camera1;predicted.camera1;observed.scene;predicted.scene;residual.mm");
            foreach (var marker in _woundModelService.LastPredictedMarkerPositionsCamera1.OrderBy(item => item.Key))
            {
                if (!_woundModelService.LastObservedMarkerPositionsCamera1.TryGetValue(marker.Key, out var observedCamera1))
                    continue;

                _woundModelService.LastMarkerFitByIdMm.TryGetValue(marker.Key, out var residual);
                var observedScene = _scene3DService != null && _scene3DService.IsCalibrated
                    ? _scene3DService.ConvertCamera1PointToScene(observedCamera1)
                    : observedCamera1;
                var predictedScene = _scene3DService != null && _scene3DService.IsCalibrated
                    ? _scene3DService.ConvertCamera1PointToScene(marker.Value)
                    : marker.Value;
                sb.AppendLine(
                    $"{marker.Key};{FormatPoint(observedCamera1)};{FormatPoint(marker.Value)};" +
                    $"{FormatPoint(observedScene)};{FormatPoint(predictedScene)};{residual:F6}");
            }

            sb.AppendLine();
            sb.AppendLine("[all-scene-markers]");
            sb.AppendLine("id;camera1;scene");
            if (_scene3DService != null)
            {
                foreach (var marker in _scene3DService.MarkerPositionsCamera1Mm.OrderBy(item => item.Key))
                {
                    _scene3DService.MarkerPositions.TryGetValue(marker.Key, out var scenePoint);
                    sb.AppendLine($"{marker.Key};{FormatPoint(marker.Value)};{FormatPoint(scenePoint)}");
                }
            }

            return sb.ToString();
        }

        private object BuildWoundDiagnosticsJson(DateTime nowUtc)
        {
            PointCloudStats? meshCamera1Stats = null;
            PointCloudStats? markerCamera1Stats = null;
            PointCloudStats? meshSceneStats = null;
            PointCloudStats? markerSceneStats = null;
            if (TryGetMeshAndMarkerCamera1Stats(out var meshCamera1, out var markerCamera1))
            {
                meshCamera1Stats = meshCamera1;
                markerCamera1Stats = markerCamera1;
            }

            if (TryGetMeshAndMarkerSceneStats(out var meshScene, out var markerScene))
            {
                meshSceneStats = meshScene;
                markerSceneStats = markerScene;
            }

            _woundModelService.TryGetReferenceMeshStats(out var referenceMeshCenter, out var referenceMeshSize, out var referenceMeshCount);
            _woundModelService.TryGetModelMarkerStats(out var modelMarkerCenter, out var modelMarkerSize, out var modelMarkerCount);

            var markerPairs = _woundModelService.LastPredictedMarkerPositionsCamera1
                .OrderBy(item => item.Key)
                .Select(item =>
                {
                    if (!_woundModelService.LastObservedMarkerPositionsCamera1.TryGetValue(item.Key, out var observedCamera1))
                        return null;

                    _woundModelService.LastMarkerFitByIdMm.TryGetValue(item.Key, out var residual);
                    var observedScene = _scene3DService != null && _scene3DService.IsCalibrated
                        ? _scene3DService.ConvertCamera1PointToScene(observedCamera1)
                        : observedCamera1;
                    var predictedScene = _scene3DService != null && _scene3DService.IsCalibrated
                        ? _scene3DService.ConvertCamera1PointToScene(item.Value)
                        : item.Value;

                    return new
                    {
                        id = item.Key,
                        observedCamera1 = ToJsonPoint(observedCamera1),
                        predictedCamera1 = ToJsonPoint(item.Value),
                        observedScene = ToJsonPoint(observedScene),
                        predictedScene = ToJsonPoint(predictedScene),
                        residualMm = residual
                    };
                })
                .Where(item => item != null)
                .ToArray();

            return new
            {
                utc = nowUtc,
                calibrated = _scene3DService?.IsCalibrated ?? false,
                freshStereoMarkers = _scene3DService?.LastTriangulatedFreshMarkerCount ?? 0,
                sceneMarkers = _scene3DService?.MarkerPositions.Count ?? 0,
                wound = new
                {
                    loadedFile = _woundModelService.LoadedFileName,
                    status = _woundModelService.Status,
                    referenceReason = _woundModelService.LastReferenceReason,
                    linkedMarkers = _woundModelService.LinkedMarkerCount,
                    activeMarkers = _woundModelService.ActiveMarkerCount,
                    visibleActiveMarkers = _woundModelService.LastVisibleActiveMarkerCount,
                    fallbackMarkers = _woundModelService.LastFallbackMarkerCount,
                    frameFrozen = _woundModelService.LastFrameFrozen,
                    freezeReason = _woundModelService.LastFreezeReason,
                    rigidRmseMm = _woundModelService.LastRigidRmseMm,
                    residualMaxMm = _woundModelService.LastResidualMaxMm,
                    residualP95Mm = _woundModelService.LastResidualP95Mm,
                    outlierMarkerCount = _woundModelService.LastOutlierMarkerCount,
                    alignRmseMm = _woundModelService.LastAlignmentRmseMm,
                    fitRmseMm = _woundModelService.LastMarkerFitRmseMm,
                    fitMaxMm = _woundModelService.LastMarkerFitMaxMm,
                    fitWorstMarkerId = _woundModelService.LastMarkerFitWorstMarkerId,
                    activeScaleMode = _woundModelService.ActiveModelScaleMode,
                    activeScaleMultiplier = _woundModelService.ActiveModelScaleMultiplier,
                    captureCombinedScale = _woundModelService.LastCaptureCombinedScale,
                    captureTranslationMm = ToJsonVector(_woundModelService.LastCaptureTranslationMm),
                    referenceMarkerBiasRmseMm = _woundModelService.LastReferenceMarkerBiasRmseMm,
                    currentSurfaceNormalCamera1 = ToJsonVector(_woundModelService.LastSurfaceNormalCamera1)
                },
                referenceMeshModel = ToJsonStats(referenceMeshCenter, referenceMeshSize, referenceMeshCount),
                modelMarkersModel = ToJsonStats(modelMarkerCenter, modelMarkerSize, modelMarkerCount),
                meshCamera1 = meshCamera1Stats.HasValue ? ToJsonStats(meshCamera1Stats.Value) : null,
                activeMarkersCamera1 = markerCamera1Stats.HasValue ? ToJsonStats(markerCamera1Stats.Value) : null,
                meshScene = meshSceneStats.HasValue ? ToJsonStats(meshSceneStats.Value) : null,
                activeMarkersScene = markerSceneStats.HasValue ? ToJsonStats(markerSceneStats.Value) : null,
                markerPairs
            };
        }

        private static object ToJsonStats(PointCloudStats stats)
        {
            return ToJsonStats(stats.Center, stats.Size, stats.Count);
        }

        private static object ToJsonStats(Point3D center, Vector3D size, int count)
        {
            return new
            {
                count,
                center = ToJsonPoint(center),
                size = ToJsonVector(size)
            };
        }

        private static object ToJsonPoint(Point3D point)
        {
            return new { x = point.X, y = point.Y, z = point.Z };
        }

        private static object ToJsonVector(Vector3D vector)
        {
            return new { x = vector.X, y = vector.Y, z = vector.Z, length = VectorLength(vector) };
        }

        private static void AppendStatsSection(
            StringBuilder sb,
            string name,
            TryGetStatsDelegate tryGetStats,
            string? suffix)
        {
            if (tryGetStats(out var center, out var size, out var count))
            {
                AppendStats(sb, suffix == null ? name : $"{name}.{suffix}", new PointCloudStats(center, size, count));
            }
        }

        private static void AppendStats(StringBuilder sb, string name, PointCloudStats stats)
        {
            sb.AppendLine($"[{name}]");
            sb.AppendLine($"count: {stats.Count}");
            AppendPoint(sb, "center", stats.Center);
            AppendVector(sb, "size", stats.Size);
        }

        private static void AppendPoint(StringBuilder sb, string name, Point3D point)
        {
            sb.AppendLine($"{name}: {FormatPoint(point)}");
        }

        private static void AppendVector(StringBuilder sb, string name, Vector3D vector)
        {
            sb.AppendLine($"{name}: ({vector.X:F6}, {vector.Y:F6}, {vector.Z:F6}), length={VectorLength(vector):F6}");
        }

        private static string FormatPoint(Point3D point)
        {
            return $"({point.X:F6}, {point.Y:F6}, {point.Z:F6})";
        }

        private delegate bool TryGetStatsDelegate(out Point3D center, out Vector3D size, out int count);

        private readonly struct PointCloudStats
        {
            public PointCloudStats(Point3D center, Vector3D size, int count)
            {
                Center = center;
                Size = size;
                Count = count;
            }

            public Point3D Center { get; }
            public Vector3D Size { get; }
            public int Count { get; }
        }

        private void TryRebuildProjectedTrajectory(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (_parsedGCodePath == null ||
                !HasActivePrintReference ||
                _scene3DService == null ||
                !_scene3DService.IsCalibrated)
                return;

            var surfaceMarkers = GetTrajectoryMarkerCandidates(currentMarkers);

            if (_surfacePrintReference != null)
            {
                if (surfaceMarkers.Count < SurfaceProjectionService.MinMarkersForDeformation)
                {
                    HandleInvalidSurface(
                        $"Режим маркеров: нужно минимум {SurfaceProjectionService.MinMarkersForDeformation} точек для перестроения, сейчас {surfaceMarkers.Count}.");
                    return;
                }
            }
            else
            {
                _lastDeformationMarkerCount = Math.Max(0, _woundModelService.ActiveMarkerCount);
                if (_lastDeformationMarkerCount < MinMarkersForWoundMeshDeformation)
                {
                    HandleInvalidSurface(
                        $"Для деформации модели нужно минимум {MinMarkersForWoundMeshDeformation} связ. маркеров, сейчас {_lastDeformationMarkerCount}.");
                    return;
                }

                if (!_woundModelService.HasModel || !_woundModelService.HasMesh)
                {
                    HandleInvalidSurface("Модель раны не загружена или mesh недоступен.");
                    return;
                }

                if (!TryGetCurrentWoundMeshSceneSnapshot(out _, out _))
                {
                    HandleInvalidSurface("Не удалось получить текущий деформированный mesh.");
                    return;
                }
            }

            if (!ShouldRebuildProjectedTrajectory(surfaceMarkers))
                return;

            RequestTrajectoryProjection(
                surfaceMarkers,
                preservePlaybackState: true,
                rebuildReason: _isPausedByInvalidSurface
                    ? "Автовосстановление после freeze"
                    : "Смещение маркеров");
        }

        private bool ShouldRebuildProjectedTrajectory(IReadOnlyList<KeyValuePair<int, Point3D>> surfaceMarkers)
        {
            if (_parsedGCodePath == null)
                return false;

            if (!HasActivePrintReference)
                return false;

            var now = DateTime.UtcNow;
            if ((now - _lastTrajectoryRebuildTime).TotalMilliseconds < TrajectoryRebuildIntervalMs)
                return false;

            if (_isPausedByInvalidSurface)
                return true;

            if (_projectedPrintPath == null)
                return true;

            if (_lastTrajectoryMarkerSnapshot.Count != surfaceMarkers.Count)
                return true;

            var movementThreshold = _printTrajectoryService.IsRunning
                ? TrajectoryRunningRebuildThresholdMm
                : TrajectoryRebuildThresholdMm;

            foreach (var marker in surfaceMarkers)
            {
                if (!_lastTrajectoryMarkerSnapshot.TryGetValue(marker.Key, out var previousPosition))
                    return true;

                if (Distance(previousPosition, marker.Value) >= movementThreshold)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Проверяет, нужно ли реально перестраивать mesh поверхности.
        /// 
        /// Обновление пропускается, если набор маркеров тот же и все точки
        /// сдвинулись меньше порога. Дополнительно действует временной интервал,
        /// чтобы поверхность не потребляла много ресурсов при небольшом шуме.
        /// </summary>
        private bool ShouldUpdateMarkerSurface(IReadOnlyList<KeyValuePair<int, Point3D>> surfaceMarkers)
        {
            var now = DateTime.UtcNow;
            var currentSurfaceIds = surfaceMarkers.Select(marker => marker.Key).ToHashSet();
            var topologyChanged = !_lastSurfaceMarkerIds.SetEquals(currentSurfaceIds);
            if (topologyChanged)
            {
                if (_lastSurfaceMarkerSnapshot.Count == 0)
                    return true;

                if (_surfaceTopologyChangeDetectedAt == DateTime.MinValue)
                {
                    _surfaceTopologyChangeDetectedAt = now;
                    return false;
                }

                if ((now - _surfaceTopologyChangeDetectedAt).TotalMilliseconds < SurfaceTopologyStabilizationMs)
                    return false;

                return (now - _lastSurfaceUpdateTime).TotalMilliseconds >= SurfaceUpdateIntervalMs;
            }

            bool hasSignificantChange = false;
            foreach (var marker in surfaceMarkers)
            {
                if (!_lastSurfaceMarkerSnapshot.TryGetValue(marker.Key, out var previousPosition))
                    return true;

                if (Distance(previousPosition, marker.Value) >= SurfaceUpdateThresholdMm)
                {
                    hasSignificantChange = true;
                    break;
                }
            }

            if (!hasSignificantChange)
                return false;

            return (now - _lastSurfaceUpdateTime).TotalMilliseconds >= SurfaceUpdateIntervalMs;
        }

        private void ClearMarkerSurface()
        {
            if (_markerSurfaceMesh.Positions.Count == 0 && _markerSurfaceMesh.TriangleIndices.Count == 0)
                return;

            _markerSurfaceMesh.Positions = new Point3DCollection();
            _markerSurfaceMesh.TriangleIndices = new Int32Collection();
            _lastSurfaceMarkerSnapshot.Clear();
            _lastSurfaceMarkerIds.Clear();
            _surfaceTopologyChangeDetectedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Строит MeshGeometry3D по набору 3D-точек маркеров.
        /// 
        /// Триангуляция выполняется не в 3D напрямую, а в 2D-проекции на две оси
        /// с наибольшим разбросом. Это позволяет корректнее работать и с почти
        /// вертикальной табличкой, где глубина меняется мало, а точки лежат в
        /// плоскости X/Z или X/Y.
        /// </summary>
        private static MeshGeometry3D BuildMarkerSurfaceMesh(IReadOnlyList<Point3D> points3D)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(points3D),
                TriangleIndices = new Int32Collection()
            };

            if (points3D.Count < 3)
                return mesh;

            var projectionAxes = ChooseProjectionAxes(points3D);
            var projectedPoints = points3D
                .Select(point => new SurfacePoint(
                    GetAxisValue(point, projectionAxes.First),
                    GetAxisValue(point, projectionAxes.Second)))
                .ToList();

            var triangles = BuildDelaunayTriangles(projectedPoints);
            foreach (var triangle in triangles)
            {
                mesh.TriangleIndices.Add(triangle.A);
                mesh.TriangleIndices.Add(triangle.B);
                mesh.TriangleIndices.Add(triangle.C);
            }

            return mesh;
        }

        /// <summary>
        /// Строит 2D Delaunay-подобную триангуляцию методом Bowyer-Watson.
        /// 
        /// Алгоритм начинает с большого "super-triangle", добавляет точки по одной,
        /// удаляет треугольники, чьи описанные окружности содержат новую точку,
        /// и сшивает образовавшуюся границу новыми треугольниками. В конце
        /// треугольники с искусственными вершинами отбрасываются.
        /// </summary>
        private static List<SurfaceTriangle> BuildDelaunayTriangles(IReadOnlyList<SurfacePoint> sourcePoints)
        {
            var points = sourcePoints.ToList();
            var bounds = GetSurfaceBounds(points);
            var delta = Math.Max(bounds.MaxU - bounds.MinU, bounds.MaxV - bounds.MinV);
            if (delta <= 1e-6)
                return new List<SurfaceTriangle>();

            var midU = (bounds.MinU + bounds.MaxU) / 2.0;
            var midV = (bounds.MinV + bounds.MaxV) / 2.0;
            int firstSuperIndex = points.Count;
            points.Add(new SurfacePoint(midU - 20 * delta, midV - delta));
            points.Add(new SurfacePoint(midU, midV + 20 * delta));
            points.Add(new SurfacePoint(midU + 20 * delta, midV - delta));

            var triangles = new List<SurfaceTriangle>
            {
                new SurfaceTriangle(firstSuperIndex, firstSuperIndex + 1, firstSuperIndex + 2)
            };

            for (int pointIndex = 0; pointIndex < sourcePoints.Count; pointIndex++)
            {
                var point = points[pointIndex];
                var badTriangles = triangles
                    .Where(triangle => CircumcircleContains(points, triangle, point))
                    .ToList();

                var boundaryEdges = new List<SurfaceEdge>();
                foreach (var triangle in badTriangles)
                {
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.A, triangle.B));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.B, triangle.C));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.C, triangle.A));
                }

                foreach (var triangle in badTriangles)
                {
                    triangles.Remove(triangle);
                }

                foreach (var edge in boundaryEdges)
                {
                    var triangle = new SurfaceTriangle(edge.A, edge.B, pointIndex);
                    if (Math.Abs(GetTriangleArea(points, triangle)) < MinTriangleArea)
                        continue;

                    if (GetTriangleArea(points, triangle) < 0)
                    {
                        triangle = new SurfaceTriangle(edge.B, edge.A, pointIndex);
                    }

                    triangles.Add(triangle);
                }
            }

            return triangles
                .Where(triangle => triangle.A < firstSuperIndex &&
                                   triangle.B < firstSuperIndex &&
                                   triangle.C < firstSuperIndex)
                .ToList();
        }

        private static void AddOrRemoveBoundaryEdge(List<SurfaceEdge> edges, SurfaceEdge edge)
        {
            var existingIndex = edges.FindIndex(existing => existing.Equals(edge));
            if (existingIndex >= 0)
            {
                edges.RemoveAt(existingIndex);
            }
            else
            {
                edges.Add(edge);
            }
        }

        private static bool CircumcircleContains(IReadOnlyList<SurfacePoint> points, SurfaceTriangle triangle, SurfacePoint point)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            var ax = a.U - point.U;
            var ay = a.V - point.V;
            var bx = b.U - point.U;
            var by = b.V - point.V;
            var cx = c.U - point.U;
            var cy = c.V - point.V;

            var determinant =
                (ax * ax + ay * ay) * (bx * cy - cx * by) -
                (bx * bx + by * by) * (ax * cy - cx * ay) +
                (cx * cx + cy * cy) * (ax * by - bx * ay);

            var orientation = GetTriangleArea(points, triangle);
            return orientation > 0
                ? determinant > 1e-6
                : determinant < -1e-6;
        }

        private static double GetTriangleArea(IReadOnlyList<SurfacePoint> points, SurfaceTriangle triangle)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            return (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        }

        private static SurfaceBounds GetSurfaceBounds(IReadOnlyList<SurfacePoint> points)
        {
            return new SurfaceBounds(
                points.Min(point => point.U),
                points.Max(point => point.U),
                points.Min(point => point.V),
                points.Max(point => point.V));
        }

        private static (SurfaceAxis First, SurfaceAxis Second) ChooseProjectionAxes(IReadOnlyList<Point3D> points)
        {
            var ranges = new[]
            {
                (Axis: SurfaceAxis.X, Range: points.Max(point => point.X) - points.Min(point => point.X)),
                (Axis: SurfaceAxis.Y, Range: points.Max(point => point.Y) - points.Min(point => point.Y)),
                (Axis: SurfaceAxis.Z, Range: points.Max(point => point.Z) - points.Min(point => point.Z))
            };

            var selectedAxes = ranges
                .OrderByDescending(range => range.Range)
                .Take(2)
                .Select(range => range.Axis)
                .ToArray();

            return (selectedAxes[0], selectedAxes[1]);
        }

        private static double GetAxisValue(Point3D point, SurfaceAxis axis)
        {
            return axis switch
            {
                SurfaceAxis.X => point.X,
                SurfaceAxis.Y => point.Y,
                SurfaceAxis.Z => point.Z,
                _ => point.X
            };
        }

        /// <summary>
        /// Получение цвета для маркера
        /// </summary>
        private Color GetMarkerColor(int markerId)
        {
            // Разные цвета для разных маркеров
            var colors = new Color[]
            {
                Colors.Green, Colors.Orange, Colors.Cyan, Colors.Magenta,
                Colors.Purple, Colors.Lime, Colors.Pink, Colors.Gold
            };
            
            return colors[markerId % colors.Length];
        }

        private int GetMarkerDisplayIndex(int markerId)
        {
            if (_scene3DService != null &&
                _scene3DService.MarkerDisplayIndices.TryGetValue(markerId, out var displayIndex))
            {
                return displayIndex;
            }

            return int.MaxValue;
        }

        private string GetMarkerName(int markerId)
        {
            return _scene3DService?.GetMarkerDisplayName(markerId) ?? "Маркер ?";
        }

        private static string BuildMarkerHudText(int markerId, Point3D position, string displayNameLine)
        {
            return $"ArUco {markerId}\n({position.X:F0}, {position.Y:F0}, {position.Z:F0}) мм\n{displayNameLine}";
        }

        private void AddMarkerDataSorted(MarkerCoordinate marker)
        {
            var insertIndex = 0;
            while (insertIndex < _markersData.Count &&
                   (_markersData[insertIndex].DisplayIndex < marker.DisplayIndex ||
                    (_markersData[insertIndex].DisplayIndex == marker.DisplayIndex &&
                     _markersData[insertIndex].ID < marker.ID)))
            {
                insertIndex++;
            }

            _markersData.Insert(insertIndex, marker);
        }

        private void SortMarkersTable()
        {
            for (int targetIndex = 0; targetIndex < _markersData.Count; targetIndex++)
            {
                var markerAtTarget = _markersData
                    .Skip(targetIndex)
                    .OrderBy(m => m.DisplayIndex)
                    .ThenBy(m => m.ID)
                    .FirstOrDefault();

                if (markerAtTarget != null)
                {
                    var currentIndex = _markersData.IndexOf(markerAtTarget);
                    if (currentIndex != targetIndex)
                    {
                        _markersData.Move(currentIndex, targetIndex);
                    }
                }
            }
        }

        private static Point3D Lerp(Point3D from, Point3D to, double alpha)
        {
            var clampedAlpha = Math.Max(0, Math.Min(1, alpha));
            return new Point3D(
                from.X + (to.X - from.X) * clampedAlpha,
                from.Y + (to.Y - from.Y) * clampedAlpha,
                from.Z + (to.Z - from.Z) * clampedAlpha);
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Обновление информационной панели
        /// </summary>
        private void UpdateInfoPanel(bool force = false)
        {
            if (_scene3DService == null)
                return;

            try
            {
                var now = DateTime.UtcNow;
                if (!force && (now - _lastInfoPanelUpdateTime).TotalMilliseconds < InfoPanelUpdateIntervalMs)
                    return;

                _lastInfoPanelUpdateTime = now;

                string info = "3D Сцена стереокалибровки\n\n";
                
                if (_scene3DService.IsCalibrated)
                {
                    info += $"✓ Калибровка выполнена\n\n";
                    
                    var cam1 = _scene3DService.Camera1Position;
                    var cam2 = _scene3DService.Camera2Position;
                    var center = _scene3DService.StereoCenter;
                    
                    info += $"📹 Камера 1: ({cam1.X:F0}, {cam1.Y:F0}, {cam1.Z:F0}) мм\n";
                    info += $"📹 Камера 2: ({cam2.X:F0}, {cam2.Y:F0}, {cam2.Z:F0}) мм\n";
                    info += $"🎯 Центр: ({center.X:F0}, {center.Y:F0}, {center.Z:F0}) мм\n";
                    info += $"🎯 Маркеров: {_scene3DService.MarkerPositions.Count}\n\n";
                    info += $"🧷 Fresh stereo markers: {_scene3DService.LastTriangulatedFreshMarkerCount}\n\n";

                    if (_woundModelService.HasModel)
                    {
                        info += "Live trace JSONL: " +
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wound_deformation_live_trace.jsonl") + "\n";
                        info += $"Модель раны: {_woundModelService.LoadedFileName}\n";
                        info += $"Wound active: {_woundModelService.LastVisibleActiveMarkerCount}/{_woundModelService.ActiveMarkerCount}, fallback {_woundModelService.LastFallbackMarkerCount}\n";
                        info += $"Rigid RMSE: {_woundModelService.LastRigidRmseMm:F1} мм, residual max {_woundModelService.LastResidualMaxMm:F1} мм\n";
                        info += $"Fit: {_woundModelService.LastMarkerFitRmseMm:F2}/{_woundModelService.LastMarkerFitMaxMm:F2} мм, align {_woundModelService.LastAlignmentRmseMm:F1} мм\n";
                        info += _woundModelService.LastFrameFrozen
                            ? $"Freeze: {_woundModelService.LastFreezeReason}\n"
                            : $"Deform: OK, outliers {_woundModelService.LastOutlierMarkerCount}\n";
                        var diagnosticsPath = TryWriteWoundDiagnosticsFile();
                        if (!string.IsNullOrWhiteSpace(diagnosticsPath))
                        {
                            info += $"Diag file: {Path.GetFileName(diagnosticsPath)}\n";
                        }
                        info += string.IsNullOrWhiteSpace(_woundModelService.ActiveTexturePath)
                            ? "Wound texture: fallback material\n\n"
                            : $"Wound texture: {Path.GetFileName(_woundModelService.ActiveTexturePath)}\n\n";
                    }

                    if (_parsedGCodePath != null)
                    {
                        info += $"🧾 G-code: {_loadedGCodeFileName}\n";
                        info += $"🛤️ Move: {_parsedGCodePath.Moves.Count}, печать: {_parsedGCodePath.ExtrusionMoves.Count}\n";
                        info += $"🧠 Деформация: {_deformationStatus}\n";
                        info += $"📌 Маркеры деформации модели: {_lastDeformationMarkerCount}/{MinMarkersForWoundMeshDeformation}+\n";
                        info += $"🛡 No-penetration: clearance {ActiveProjectionSafetyClearanceMm:F1} мм\n";
                        info += _isPausedByInvalidSurface
                            ? "⛔ Автопауза активна\n"
                            : "✅ Геометрия валидна\n";
                        if (_showDeformationDebugOverlay)
                        {
                            info += "🧭 Debug overlay: ON\n";
                        }

                        if (_projectedPrintPath != null)
                        {
                            info += $"🖨️ Прогресс: {_printTrajectoryService.NormalizedProgress * 100:F1}%\n";
                            info += _printTrajectoryService.IsRunning
                                ? "▶ Печать активна\n\n"
                                : "⏸ Печать остановлена/пауза\n\n";
                        }
                        else
                        {
                            info += "⏳ Траектория ожидает проекцию\n\n";
                        }
                    }
                }
                else
                {
                    info += "❌ Калибровка не выполнена\n\n";
                }
                
                info += "Управление:\n";
                info += "• ПКМ - поворот\n";
                info += "• Колесо - масштаб\n";
                info += "• Shift+ПКМ - панорама";
                
                _infoText.Text = info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления информационной панели: {ex.Message}");
            }
        }



        /// <summary>
        /// Очистка ресурсов
        /// </summary>
        public void Cleanup()
        {
            try
            {
                if (_scene3DService != null)
                {
                    _scene3DService.OnSceneUpdated -= UpdateScene;
                }

                _printTimer.Stop();
                _printTimer.Tick -= PrintTimer_Tick;

                // Очищаем маркеры
                foreach (var visual in _markerVisuals.Values)
                {
                    _viewport3D.Children.Remove(visual);
                }
                _markerVisuals.Clear();

                foreach (var text in _markerTexts.Values)
                {
                    _viewport3D.Children.Remove(text);
                }
                _markerTexts.Clear();
                _markerTextCache.Clear();

                ClearMarkerGuideLines();
                ClearMarkerSurface();
                ClearWoundPredictedGizmoVisuals();
                _woundMarkerFitDebugVisual.Points = new Point3DCollection();
                _woundModelMesh.Positions = new Point3DCollection();
                _woundModelMesh.TriangleIndices = new Int32Collection();
                ClearTrajectoryVisuals(keepParsedPath: false);

                // Очищаем таблицу координат
                _markersData?.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка очистки 3D сцены: {ex.Message}");
            }
        }

        private enum SurfaceAxis
        {
            X,
            Y,
            Z
        }

        private readonly struct SurfacePoint
        {
            public SurfacePoint(double u, double v)
            {
                U = u;
                V = v;
            }

            public double U { get; }
            public double V { get; }
        }

        private readonly struct SurfaceBounds
        {
            public SurfaceBounds(double minU, double maxU, double minV, double maxV)
            {
                MinU = minU;
                MaxU = maxU;
                MinV = minV;
                MaxV = maxV;
            }

            public double MinU { get; }
            public double MaxU { get; }
            public double MinV { get; }
            public double MaxV { get; }
        }

        private readonly struct SurfaceTriangle
        {
            public SurfaceTriangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public int A { get; }
            public int B { get; }
            public int C { get; }
        }

        private readonly struct SurfaceEdge : IEquatable<SurfaceEdge>
        {
            public SurfaceEdge(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }

            public bool Equals(SurfaceEdge other)
            {
                return (A == other.A && B == other.B) ||
                       (A == other.B && B == other.A);
            }

            public override bool Equals(object? obj)
            {
                return obj is SurfaceEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                var min = Math.Min(A, B);
                var max = Math.Max(A, B);
                return HashCode.Combine(min, max);
            }
        }
    }
}