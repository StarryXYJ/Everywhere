using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Everywhere.Interop;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class Win32VisualElementContext
{
    private class ElementPicker : Window
    {
        private readonly Win32VisualElementContext _context;
        private readonly INativeHelper _nativeHelper;
        private readonly PickElementMode _mode;

        private readonly PixelRect _screenBounds;
        private readonly Bitmap _bitmap;
        private readonly Border _clipBorder;
        private readonly Image _image;
        private readonly double _scale;
        private readonly TaskCompletionSource<IVisualElement?> _taskCompletionSource = new();

        private Rect? _previousMaskRect;
        private IVisualElement? _selectedElement;

        private ElementPicker(
            Win32VisualElementContext context,
            INativeHelper nativeHelper,
            PickElementMode mode)
        {
            _context = context;
            _nativeHelper = nativeHelper;
            _mode = mode;

            var allScreens = Screens.All;
            _screenBounds = allScreens.Aggregate(default(PixelRect), (current, screen) => current.Union(screen.Bounds));
            if (_screenBounds.Width <= 0 || _screenBounds.Height <= 0)
            {
                throw new InvalidOperationException("No valid screen bounds found.");
            }

            _bitmap = CaptureScreen(_screenBounds);
            _clipBorder = new Border
            {
                ClipToBounds = false,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Content = new Panel
            {
                IsHitTestVisible = false,
                Children =
                {
                    new Image { Source = _bitmap },
                    new Border
                    {
                        Background = Brushes.Black,
                        Opacity = 0.4
                    },
                    (_image = new Image { Source = _bitmap }),
                    _clipBorder
                }
            };

            Topmost = true;
            CanResize = false;
            ShowInTaskbar = false;
            Cursor = new Cursor(StandardCursorType.Cross);
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Position = _screenBounds.Position;
            _scale = DesktopScaling; // we must set Position first to get the correct scaling factor
            Width = _screenBounds.Width / _scale;
            Height = _screenBounds.Height / _scale;
        }

        protected override unsafe void OnPointerEntered(PointerEventArgs e)
        {
            // SendInput MouseLeftButtonDown, this will:
            // 1. prevent the cursor from changing to the default arrow cursor and interacting with other windows
            // 2. Trigger the OnPointerPressed event to set the window to hit test invisible
            PInvoke.SendInput(
                new ReadOnlySpan<INPUT>(
                [
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_MOUSE,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE,
                                dx = Position.X + 1,
                                dy = Position.Y + 1
                            }
                        }
                    },
                ]),
                sizeof(INPUT));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _nativeHelper.SetWindowHitTestInvisible(this);

            if (PInvoke.GetCursorPos(out var point)) Pick(point);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (PInvoke.GetCursorPos(out var point)) Pick(point);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
            {
                _selectedElement = null;
            }

            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) _selectedElement = null;

            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _bitmap.Dispose();
            _taskCompletionSource.TrySetResult(_selectedElement);
        }

        private void Pick(Point point)
        {
            var maskRect = new Rect();
            var pixelPoint = new PixelPoint(point.X, point.Y);
            switch (_mode)
            {
                case PickElementMode.Screen:
                {
                    var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pixelPoint));
                    if (screen == null) break;

                    var hMonitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                    if (hMonitor == HMONITOR.Null) break;

                    _selectedElement = new ScreenVisualElementImpl(_context, hMonitor);

                    maskRect = screen.Bounds.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
                    break;
                }
                case PickElementMode.Window:
                {
                    var selectedHWnd = PInvoke.WindowFromPoint(point);
                    if (selectedHWnd == HWND.Null) break;

                    var rootHWnd = PInvoke.GetAncestor(selectedHWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
                    if (rootHWnd == HWND.Null) break;

                    _selectedElement = _context.TryFrom(() => Automation.FromHandle(rootHWnd));
                    if (_selectedElement == null) break;

                    maskRect = _selectedElement.BoundingRectangle.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
                    break;
                }
                case PickElementMode.Element:
                {
                    _selectedElement = _context.TryFrom(() => Automation.FromPoint(point));
                    if (_selectedElement == null) break;

                    maskRect = _selectedElement.BoundingRectangle.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
                    break;
                }
            }

            SetMask(maskRect);
        }

        private void SetMask(Rect rect)
        {
            if (_previousMaskRect == rect) return;

            _image.Clip = new RectangleGeometry(rect);
            _clipBorder.Margin = new Thickness(rect.X, rect.Y, 0, 0);
            _clipBorder.Width = rect.Width;
            _clipBorder.Height = rect.Height;

            _previousMaskRect = rect;
        }

        public static Task<IVisualElement?> PickAsync(
            Win32VisualElementContext context,
            INativeHelper nativeHelper,
            PickElementMode mode)
        {
            var window = new ElementPicker(context, nativeHelper, mode);
            window.Show();
            return window._taskCompletionSource.Task;
        }
    }

    private class RegionPicker : Window
    {
        private readonly Bitmap _bitmap;
        private readonly PixelRect _screenBounds;
        private readonly Canvas _canvas;
        private readonly Avalonia.Controls.Shapes.Rectangle _selectionRect;
        private readonly double _scale;
        private readonly TaskCompletionSource<PixelRect?> _taskCompletionSource = new();
        private PixelPoint _startPoint;
        private bool _isDragging;

        private RegionPicker()
        {
            var allScreens = Screens.All;
            _screenBounds = allScreens.Aggregate(default(PixelRect), (current, screen) => current.Union(screen.Bounds));
            if (_screenBounds.Width <= 0 || _screenBounds.Height <= 0)
            {
                throw new InvalidOperationException("No valid screen bounds found.");
            }

            _bitmap = CaptureScreen(_screenBounds);
            _canvas = new Canvas();
            _selectionRect = new Avalonia.Controls.Shapes.Rectangle
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };

            Content = new Panel
            {
                IsHitTestVisible = true,
                Children =
                {
                    new Image { Source = _bitmap },
                    new Border
                    {
                        Background = Brushes.Black,
                        Opacity = 0.4
                    },
                    _canvas
                }
            };

            _canvas.Children.Add(_selectionRect);

            Topmost = true;
            CanResize = false;
            ShowInTaskbar = false;
            Cursor = new Cursor(StandardCursorType.Cross);
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Position = _screenBounds.Position;
            _scale = DesktopScaling; // we must set Position first to get the correct scaling factor
            Width = _screenBounds.Width / _scale;
            Height = _screenBounds.Height / _scale;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                var pos = e.GetPosition(this) * _scale;
                _startPoint = new PixelPoint((int)pos.X, (int)pos.Y) + _screenBounds.Position;
                SetSelectionRect(_startPoint, _startPoint);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_isDragging)
            {
                var pos = e.GetPosition(this) * _scale;
                var currentPoint = new PixelPoint((int)pos.X, (int)pos.Y) + _screenBounds.Position;
                SetSelectionRect(_startPoint, currentPoint);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton == MouseButton.Left && _isDragging)
            {
                _isDragging = false;
                var position = e.GetPosition(this) * _scale;
                var endPoint = new PixelPoint((int)position.X, (int)position.Y) + _screenBounds.Position;
                var selectedRect = GetPixelRect(_startPoint, endPoint);
                _taskCompletionSource.TrySetResult(selectedRect);
            }
            else
            {
                _taskCompletionSource.TrySetResult(null);
            }
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _taskCompletionSource.TrySetResult(null);
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _bitmap.Dispose();
            if (!_taskCompletionSource.Task.IsCompleted)
            {
                _taskCompletionSource.TrySetResult(null);
            }
        }

        private void SetSelectionRect(PixelPoint start, PixelPoint end)
        {
            var rect = GetPixelRect(start, end);
            var scaledRect = rect.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
            _selectionRect.Margin = new Thickness(scaledRect.X, scaledRect.Y, 0, 0);
            _selectionRect.Width = scaledRect.Width;
            _selectionRect.Height = scaledRect.Height;
        }

        private PixelRect GetPixelRect(PixelPoint start, PixelPoint end)
        {
            var x = Math.Min(start.X, end.X);
            var y = Math.Min(start.Y, end.Y);
            var width = Math.Abs(start.X - end.X);
            var height = Math.Abs(start.Y - end.Y);
            return new PixelRect(x, y, width, height);
        }

        public static Task<PixelRect?> PickAsync()
        {
            var window = new RegionPicker();
            window.Show();
            return window._taskCompletionSource.Task;
        }
    }
}