using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Gd = GdPicture14;
using GdWPF = GdPicture14.WPF;

namespace ZonalOcrViewer
{
    public partial class MainWindow : Window
    {
        private Color m_StrokingColor = Colors.Black;
        private Color m_FillingColor = Colors.Red;
        private float m_LineWidth = 0.10f;

        private bool m_FreehandContinuous = false;
        private bool m_FreehandOcrContinuous = false;

        // Tracks whether the new "Line OCR" mode is currently active.
        private bool m_LineOcrContinuous = false;

        private Brush m_FreehandButtonOriginalBackground = null;
        private Brush m_FreehandOcrButtonOriginalBackground = null;

        // Original background brush of the Line OCR button, cached so we can restore it
        // when the mode is toggled off.
        private Brush m_LineOcrButtonOriginalBackground = null;

        // State machine that drives the Line OCR interaction. The user can either
        // click-hold-drag-release (drag mode) or click-then-click (two-click mode);
        // both must be handled and each needs its own phase to detect completion.
        private enum LineOcrPhase { WaitingFirst, PressDownFirst, WaitingSecond }
        private LineOcrPhase _lineOcrPhase = LineOcrPhase.WaitingFirst;
        private Point _lineOcrP1;
        private Point _lineOcrP2;
        private DateTime _lineOcrMouseDownTime;
        private bool _lineOcrMouseMoved;
        private const double LineOcrMoveThreshold = 5.0;
        private const int LineOcrHoldMs = 500;

        private Point _startPoint;
        private Point _lastPoint;
        private List<Point> _mousePath;
        private bool _isMouseTracking = false;
        private bool _mouseHandlersAttached = false;

        private DrawingDirection _lastDetectedDirection = DrawingDirection.Diagonal;

        public string m_Password = "";

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // An empty key starts GdPicture in evaluation mode.
                var oLicenceManager = new Gd.LicenseManager();
                oLicenceManager.RegisterKEY(Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "");
            }
            catch { }

            BookmarksTree1.GdViewer = GdViewer1;
            annotationEditor1.InitFromAnnotationManager(GdViewer1.GetAnnotationManager());

            try { AlignmentMC.IsChecked = true; } catch { }
            try { PositionMC.IsChecked = true; } catch { }

            GdViewer1.AnnotationAddedByUser += GdViewer1_AnnotationAddedByUser;
            GdViewer1.PageChanged += GdViewer1_PageChanged;
            GdViewer1.PageDisplayed += GdViewer1_PageDisplayed;

            try { m_FreehandButtonOriginalBackground = tsFreeHandHighlighter.Background; } catch { }
            try { m_FreehandOcrButtonOriginalBackground = tsFreeHandOcr.Background; } catch { }
            try { m_LineOcrButtonOriginalBackground = tsLineOcr.Background; } catch { }

            AttachMouseHandlers();
            ShowPageCount();
        }

        private void AttachMouseHandlers()
        {
            if (_mouseHandlersAttached) return;
            try
            {
                GdViewer1.MouseDown += GdViewer_MouseDown;
                GdViewer1.MouseMove += GdViewer_MouseMove;
                GdViewer1.MouseUp += GdViewer_MouseUp;
            }
            catch (Exception ex) { Console.WriteLine("AttachMouseHandlers error: " + ex); }
            _mouseHandlersAttached = true;
        }

        private void GdViewer_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Line OCR branch. Two behaviors are handled here:
            //  * First click: record P1, start a timer, and let the interactive mode
            //    place the first point of the polyline on the viewer.
            //  * Second click (two-click mode): record P2 and schedule a
            //    TerminateLastAnnotInteractiveAdd on the dispatcher. That completes the
            //    polyline natively and raises AnnotationAddedByUser which routes to the
            //    LineOcrService.
            if (m_LineOcrContinuous)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(GdViewer1);
                if (_lineOcrPhase == LineOcrPhase.WaitingSecond)
                {
                    _lineOcrP2 = pos;
                    _lineOcrPhase = LineOcrPhase.WaitingFirst;
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { GdViewer1.TerminateLastAnnotInteractiveAdd(); } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
                else
                {
                    _lineOcrP1 = pos;
                    _lineOcrMouseDownTime = DateTime.Now;
                    _lineOcrMouseMoved = false;
                    _lineOcrPhase = LineOcrPhase.PressDownFirst;
                }
                return;
            }

            if (!m_FreehandOcrContinuous && !m_FreehandContinuous) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            _startPoint = e.GetPosition(GdViewer1);
            _lastPoint = _startPoint;
            _mousePath = new List<Point> { _startPoint };
            _isMouseTracking = true;
            DebugWrite($"Mouse tracking started at ({_startPoint.X}, {_startPoint.Y})");
        }

        private void GdViewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // While the left button is down in Line OCR mode, track whether the cursor
            // moved by more than LineOcrMoveThreshold pixels. This lets MouseUp decide
            // between drag mode and the first half of a two-click sequence.
            if (m_LineOcrContinuous && _lineOcrPhase == LineOcrPhase.PressDownFirst)
            {
                var pos = e.GetPosition(GdViewer1);
                double dx = pos.X - _lineOcrP1.X;
                double dy = pos.Y - _lineOcrP1.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > LineOcrMoveThreshold)
                    _lineOcrMouseMoved = true;
                return;
            }

            if (!_isMouseTracking) return;
            Point current = e.GetPosition(GdViewer1);
            if (current != _lastPoint)
            {
                _mousePath.Add(current);
                _lastPoint = current;
            }
        }

        private void GdViewer_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Line OCR MouseUp branch. If the user held the button more than
            // LineOcrHoldMs milliseconds OR dragged further than the move threshold we
            // treat it as drag mode: cancel the in-progress interactive polyline and
            // invoke FinalizeLineOcr directly. Otherwise it was a short click and we
            // wait for the second click to finish the two-click sequence.
            if (m_LineOcrContinuous)
            {
                if (_lineOcrPhase != LineOcrPhase.PressDownFirst) return;
                var pos = e.GetPosition(GdViewer1);
                var duration = (DateTime.Now - _lineOcrMouseDownTime).TotalMilliseconds;
                if (duration > LineOcrHoldMs || _lineOcrMouseMoved)
                {
                    var p1 = _lineOcrP1;
                    _lineOcrPhase = LineOcrPhase.WaitingFirst;
                    try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
                    FinalizeLineOcr(p1, pos);
                }
                else
                {
                    _lineOcrP1 = pos;
                    _lineOcrPhase = LineOcrPhase.WaitingSecond;
                }
                return;
            }

            if (!_isMouseTracking) return;
            _isMouseTracking = false;
            if (_mousePath == null || _mousePath.Count < 2)
            {
                _lastDetectedDirection = DrawingDirection.Diagonal;
                DebugWrite("Mouse tracking ended with insufficient points -> Diagonal");
                return;
            }
            _lastDetectedDirection = DetermineAdvancedDirection(_mousePath);
            DebugWrite($"Mouse tracking ended. Direction: {_lastDetectedDirection}. Points: {_mousePath.Count}");
            _mousePath.Clear();
        }

        private DrawingDirection DetermineAdvancedDirection(List<Point> path)
        {
            if (path == null || path.Count < 2) return DrawingDirection.Diagonal;
            double totalDX = 0, totalDY = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                totalDX += path[i + 1].X - path[i].X;
                totalDY += path[i + 1].Y - path[i].Y;
            }
            double avgDX = totalDX / (path.Count - 1);
            double avgDY = totalDY / (path.Count - 1);
            double absX = Math.Abs(avgDX), absY = Math.Abs(avgDY);
            const double thresholdRatio = 1.2;
            if (absX > absY * thresholdRatio) return avgDX > 0 ? DrawingDirection.LeftToRight : DrawingDirection.RightToLeft;
            if (absY > absX * thresholdRatio) return avgDY > 0 ? DrawingDirection.TopToBottom : DrawingDirection.BottomToTop;
            return DrawingDirection.Diagonal;
        }

        private DrawingDirection GetLastDetectedDirection() => _lastDetectedDirection;

        // Turns off the Freehand OCR mode: resets the toggle flag, restores the button
        // background and cancels any interactive freehand highlighter still in progress.
        // Used when switching from Freehand OCR to Line OCR (and vice versa) to keep the
        // two toolbar buttons mutually exclusive.
        private void DeactivateFreehandOcrMode()
        {
            if (!m_FreehandOcrContinuous) return;
            m_FreehandOcrContinuous = false;
            try { if (m_FreehandOcrButtonOriginalBackground != null) tsFreeHandOcr.Background = m_FreehandOcrButtonOriginalBackground; } catch { }
            try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
        }

        // Symmetrical counterpart of DeactivateFreehandOcrMode for the Line OCR mode.
        // Also resets the click-state machine so the next activation starts clean.
        private void DeactivateLineOcrMode()
        {
            if (!m_LineOcrContinuous) return;
            m_LineOcrContinuous = false;
            try { if (m_LineOcrButtonOriginalBackground != null) tsLineOcr.Background = m_LineOcrButtonOriginalBackground; } catch { }
            _lineOcrPhase = LineOcrPhase.WaitingFirst;
            _lineOcrMouseMoved = false;
        }

        private void tsFreeHandOcr_Click(object sender, RoutedEventArgs e)
        {
            // Always cancel any annotation currently being drawn by another tool, then
            // make sure the Line OCR mode is off so the two OCR buttons can never be
            // active at the same time.
            try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
            if (!m_FreehandOcrContinuous) DeactivateLineOcrMode();

            m_FreehandOcrContinuous = !m_FreehandOcrContinuous;
            if (m_FreehandOcrContinuous)
            {
                try { tsFreeHandOcr.Background = Brushes.LightBlue; } catch { }
                AttachMouseHandlers();
                GdViewer1.Focus();
                GdViewer1.AddFreeHandHighlighterAnnotInteractive(Colors.Yellow, m_LineWidth);
            }
            else
            {
                try { if (m_FreehandOcrButtonOriginalBackground != null) tsFreeHandOcr.Background = m_FreehandOcrButtonOriginalBackground; } catch { }
                GdViewer1.CancelLastAnnotInteractiveAdd();
            }
        }

        // Rendering parameters for the yellow connected-lines "highlighter" used by
        // Line OCR. BorderWidth is expressed in inches, Opacity in [0..1].
        private const float LineOcrBorderWidth = 0.2f;
        private const float LineOcrOpacity = 0.4f;

        // Starts (or restarts) the interactive connected-lines annotation for Line OCR.
        // Cancelling any pending interactive add and explicitly enabling the editor
        // mode makes this helper safe to call repeatedly between OCR runs.
        private void StartLineOcrInteractive()
        {
            try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
            try { GdViewer1.SetAnnotationEditorMode(true); } catch { }
            GdViewer1.Focus();
            try { GdViewer1.AddConnectedLinesAnnotInteractive(Colors.Yellow, LineOcrBorderWidth, LineOcrOpacity); } catch { }
        }

        // Toolbar click handler for the new "Line OCR" button. Cancels any in-progress
        // annotation, ensures Freehand OCR is off (mutual exclusion), toggles the mode
        // and starts/stops the interactive connected-lines annotation accordingly.
        private void tsLineOcr_Click(object sender, RoutedEventArgs e)
        {
            try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
            if (!m_LineOcrContinuous) DeactivateFreehandOcrMode();

            m_LineOcrContinuous = !m_LineOcrContinuous;
            if (m_LineOcrContinuous)
            {
                try { tsLineOcr.Background = Brushes.LightBlue; } catch { }
                AttachMouseHandlers();
                _lineOcrPhase = LineOcrPhase.WaitingFirst;
                _lineOcrMouseMoved = false;
                StartLineOcrInteractive();
            }
            else
            {
                try { if (m_LineOcrButtonOriginalBackground != null) tsLineOcr.Background = m_LineOcrButtonOriginalBackground; } catch { }
                try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
                _lineOcrPhase = LineOcrPhase.WaitingFirst;
                _lineOcrMouseMoved = false;
            }
        }

        // Adds a yellow semi-transparent connected-lines annotation between the two
        // supplied viewer-space points. Used by the drag-mode Line OCR path so the
        // user still sees the highlighter mark even though we bypass the interactive
        // mode for that gesture. Returns the index of the new annotation so the caller
        // can delete it once OCR is done.
        private int AddLineOcrAnnotation(Point p1, Point p2)
        {
            try
            {
                var mgr = GdViewer1.GetAnnotationManager();
                double p1InX = 0, p1InY = 0, p2InX = 0, p2InY = 0;
                GdViewer1.CoordViewerPixelToDocumentInch(p1.X, p1.Y, ref p1InX, ref p1InY);
                GdViewer1.CoordViewerPixelToDocumentInch(p2.X, p2.Y, ref p2InX, ref p2InY);

                var points = new[]
                {
                    new System.Drawing.PointF((float)p1InX, (float)p1InY),
                    new System.Drawing.PointF((float)p2InX, (float)p2InY),
                };
                var annot = mgr.AddConnectedLinesAnnot(System.Drawing.Color.Yellow, points);
                if (annot != null)
                {
                    annot.BorderWidth = LineOcrBorderWidth;
                    annot.Opacity = LineOcrOpacity;
                }
                GdViewer1.Redraw();
                return mgr.GetAnnotationCount() - 1;
            }
            catch
            {
                return -1;
            }
        }

        // Drag-mode completion handler for Line OCR. Adds a programmatic yellow line
        // between p1 and p2, then defers the OCR / popup / delete pipeline at
        // Background priority so the viewer has time to actually render the annotation
        // before the synchronous OCR call blocks the UI thread.
        private void FinalizeLineOcr(Point p1, Point p2)
        {
            int annotIdx = AddLineOcrAnnotation(p1, p2);

            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                string ocrResult = null;
                try
                {
                    var svc = new LineOcrService();
                    ocrResult = svc.LineOcr(GdViewer1, p1, p2);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Line OCR error: " + ex);
                }

                ShowOcrPopup(ocrResult, OcrPipeline.CropImagePath);

                if (annotIdx >= 0)
                {
                    try { GdViewer1.DeleteAnnotation(annotIdx); } catch { }
                }

                if (m_LineOcrContinuous)
                {
                    this.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(120);
                        _lineOcrPhase = LineOcrPhase.WaitingFirst;
                        _lineOcrMouseMoved = false;
                        StartLineOcrInteractive();
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void GdViewer1_AnnotationAddedByUser(object sender, GdWPF.GdViewer.AnnotationAddedByUserEventArgs e)
        {
            try
            {
                string ocrResult = null;

                // Line OCR routing. When the two-click gesture completes, we call
                // TerminateLastAnnotInteractiveAdd from the mouse handler, which causes
                // this event to fire. Run LineOcrService with the two points recorded
                // from the mouse clicks, show the popup, delete the just-created
                // annotation and restart the interactive mode for the next line.
                if (m_LineOcrContinuous)
                {
                    try
                    {
                        var svc = new LineOcrService();
                        ocrResult = svc.LineOcr(GdViewer1, _lineOcrP1, _lineOcrP2);
                    }
                    catch (Exception exInner) { Console.WriteLine("Line OCR error: " + exInner); }

                    ShowOcrPopup(ocrResult, OcrPipeline.CropImagePath);

                    try
                    {
                        int sel = GdViewer1.GetSelectedAnnotationIdx();
                        if (sel >= 0) GdViewer1.DeleteAnnotation(sel);
                        else if (e.AnnotationIdx >= 0 && GdViewer1.GetAnnotationCount() > 0)
                        {
                            try { GdViewer1.DeleteAnnotation(e.AnnotationIdx); } catch { }
                        }
                    }
                    catch { }

                    if (m_LineOcrContinuous)
                    {
                        this.Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            await Task.Delay(120);
                            _lineOcrPhase = LineOcrPhase.WaitingFirst;
                            _lineOcrMouseMoved = false;
                            StartLineOcrInteractive();
                        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                    return;
                }

                var ocrService = new FreehandOcrService();

                try
                {
                    ocrResult = ocrService.FreehandOcr(GdViewer1, e.AnnotationIdx, _startPoint, _lastPoint);
                }
                catch (Exception exInner)
                {
                    Console.WriteLine("OCR service error: " + exInner);
                    ocrResult = null;
                }

                var dir = GetLastDetectedDirection();
                Console.WriteLine("Annotation overall direction (best-fit): " + dir.ToString());

                string horizontal = "Undetermined";
                string vertical = "Undetermined";

                try
                {
                    double dx = _lastPoint.X - _startPoint.X;
                    double dy = _lastPoint.Y - _startPoint.Y;
                    double absX = Math.Abs(dx);
                    double absY = Math.Abs(dy);

                    const double thresholdRatio = 1.2;
                    const double minMovement = 2.0;

                    if (absX >= minMovement)
                    {
                        if (absX > absY * thresholdRatio)
                            horizontal = dx > 0 ? "LeftToRight" : "RightToLeft";
                        else
                            horizontal = "Mixed";
                    }

                    if (absY >= minMovement)
                    {
                        if (absY > absX * thresholdRatio)
                            vertical = dy > 0 ? "TopToBottom" : "BottomToTop";
                        else
                            vertical = "Mixed";
                    }
                }
                catch (Exception exDir)
                {
                    Console.WriteLine("Direction detection error: " + exDir);
                }

                Console.WriteLine($"Horizontal: {horizontal}  |  Vertical: {vertical}");

                ShowOcrPopup(ocrResult, OcrPipeline.CropImagePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error running OCR service: " + ex);
                ShowOcrPopup(null, OcrPipeline.CropImagePath);
            }
            finally
            {
                try
                {
                    int sel = GdViewer1.GetSelectedAnnotationIdx();
                    if (sel >= 0)
                    {
                        GdViewer1.DeleteAnnotation(sel);
                    }
                    else
                    {
                        if (e.AnnotationIdx >= 0 && GdViewer1.GetAnnotationCount() > 0)
                        {
                            try { GdViewer1.DeleteAnnotation(e.AnnotationIdx); } catch { }
                        }
                    }
                }
                catch (Exception exDel)
                {
                    Console.WriteLine("Warning: failed to delete annotation: " + exDel);
                }

                if (m_FreehandOcrContinuous)
                {
                    this.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(120);
                        try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
                        GdViewer1.Focus();
                        GdViewer1.AddFreeHandHighlighterAnnotInteractive(Colors.Yellow, 0.2f);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                else if (m_FreehandContinuous)
                {
                    this.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(120);
                        try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { }
                        GdViewer1.Focus();
                        GdViewer1.AddFreeHandHighlighterAnnotInteractive(Colors.Yellow, m_LineWidth);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
        }

        private void ShowOcrPopup(string text, string imagePath)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var imageScrollViewer = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    };

                    var image = new System.Windows.Controls.Image
                    {
                        Stretch = Stretch.None,
                        Margin = new Thickness(8)
                    };

                    if (System.IO.File.Exists(imagePath))
                    {
                        try
                        {
                            byte[] imageBytes = System.IO.File.ReadAllBytes(imagePath);
                            
                            using (var memoryStream = new System.IO.MemoryStream(imageBytes))
                            {
                                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                                    memoryStream,
                                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                
                                var frame = decoder.Frames[0];
                                frame.Freeze();
                                
                                image.Source = frame;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error loading image: {ex.Message}");
                            Console.WriteLine($"Stack trace: {ex.StackTrace}");
                        }
                    }
                    else
                    {
                        var textBlock = new TextBlock
                        {
                            Text = "(image not found)",
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(8)
                        };
                        imageScrollViewer.Content = textBlock;
                    }

                    if (image.Source != null)
                    {
                        imageScrollViewer.Content = image;
                    }

                    Grid.SetColumn(imageScrollViewer, 0);
                    grid.Children.Add(imageScrollViewer);

                    var splitter = new GridSplitter
                    {
                        Width = 5,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Background = System.Windows.Media.Brushes.LightGray
                    };
                    Grid.SetColumn(splitter, 1);
                    grid.Children.Add(splitter);

                    var textBox = new System.Windows.Controls.TextBox
                    {
                        Text = string.IsNullOrEmpty(text) ? "(no text found)" : text,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        AcceptsReturn = true,
                        Margin = new Thickness(8)
                    };

                    var textScrollViewer = new ScrollViewer
                    {
                        Content = textBox,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                    };

                    Grid.SetColumn(textScrollViewer, 2);
                    grid.Children.Add(textScrollViewer);

                    var wnd = new Window
                    {
                        Title = "OCR Result",
                        Content = grid,
                        Width = 900,
                        Height = 600,
                        Owner = this,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };
                    wnd.Show();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ShowOcrPopup error: {ex}");
                    MessageBox.Show($"Error displaying OCR result: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }));
        }

        private void DebugWrite(string s) => Console.WriteLine(s);

        private void btStrokingColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GdViewer1.CancelLastAnnotInteractiveAdd();
                var dlg = new System.Windows.Forms.ColorDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    m_StrokingColor = System.Windows.Media.Color.FromArgb(dlg.Color.A, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    btStrokingColor.Background = new SolidColorBrush(m_StrokingColor);
                }
            }
            catch (Exception ex) { Console.WriteLine("btStrokingColor_Click error: " + ex); }
        }

        private void btFillingColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GdViewer1.CancelLastAnnotInteractiveAdd();
                var dlg = new System.Windows.Forms.ColorDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    m_FillingColor = System.Windows.Media.Color.FromArgb(dlg.Color.A, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    btFillingColor.Background = new SolidColorBrush(m_FillingColor);
                }
            }
            catch (Exception ex) { Console.WriteLine("btFillingColor_Click error: " + ex); }
        }

        private void cmdUp_Click(object sender, RoutedEventArgs e) { m_LineWidth += 0.01f; nupLineWidth.Text = m_LineWidth.ToString(); }
        private void cmdDown_Click(object sender, RoutedEventArgs e) { m_LineWidth -= 0.01f; if (m_LineWidth < 0.0f) m_LineWidth = 0.0f; nupLineWidth.Text = m_LineWidth.ToString(); }
        private void txtText_TextChanged(object sender, TextChangedEventArgs e) { GdViewer1.CancelLastAnnotInteractiveAdd(); }

        private void tsHighlightedRectangle_Click(object sender, RoutedEventArgs e) { TryAddRectangleHighlighter(); }
        private void tsFreeHandHighlighter_Click(object sender, RoutedEventArgs e) { TryAddFreehandHighlighter(false); }
        private void tsRectangle_Click(object sender, RoutedEventArgs e) { TryAddRectangle(false); }
        private void tsFilledRectangle_Click(object sender, RoutedEventArgs e) { TryAddRectangle(true); }
        private void tsEllipse_Click(object sender, RoutedEventArgs e) { TryAddEllipse(false); }
        private void tsFilledEllipse_Click(object sender, RoutedEventArgs e) { TryAddEllipse(true); }
        private void tsEmbeddedImage_Click(object sender, RoutedEventArgs e) { TryAddEmbeddedImage(); }
        private void tsRubberStamp_Click(object sender, RoutedEventArgs e) { TryAddRubberStamp(); }
        private void tsLine_Click(object sender, RoutedEventArgs e) { TryAddLine(); }
        private void tsConnectedLines_Click(object sender, RoutedEventArgs e) { TryAddConnectedLines(); }
        private void tsLineArraw_Click(object sender, RoutedEventArgs e) { TryAddLineArrow(); }
        private void tsText_Click(object sender, RoutedEventArgs e) { TryAddTextAnnotation(); }
        private void tsStickyNote_Click(object sender, RoutedEventArgs e) { TryAddStickyNote(); }
        private void tsFreeHand_Click(object sender, RoutedEventArgs e) { TryAddFreehandHighlighter(true); }
        private void tsLink_Click(object sender, RoutedEventArgs e) { TryAddLink(); }
        private void tsPolygon_Click(object sender, RoutedEventArgs e) { TryAddPolygon(); }
        private void fsFreeHandPolygon_Click(object sender, RoutedEventArgs e) { TryAddFreeHandPolygon(); }
        private void tsRuler_Click(object sender, RoutedEventArgs e) { TryAddRuler(); }

        private void TryAddRectangle(bool filled)
        {
            try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddRectangleAnnotInteractive(filled, !filled, m_FillingColor, m_StrokingColor, m_LineWidth, 1.0f); }
            catch { }
        }
        private void TryAddEllipse(bool filled) { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddEllipseAnnotInteractive(filled, !filled, m_FillingColor, m_StrokingColor, m_LineWidth, 1.0f); } catch { } }
        private void TryAddFreehandHighlighter(bool continuous) { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddFreeHandHighlighterAnnotInteractive(Colors.Yellow, m_LineWidth); } catch { } }
        private void TryAddRectangleHighlighter() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddRectangleHighlighterAnnotInteractive(m_FillingColor); } catch { } }
        private void TryAddEmbeddedImage() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); /* sample opens dialog */ } catch { } }
        private void TryAddRubberStamp() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); } catch { } }
        private void TryAddLine() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddLineAnnotInteractive(m_StrokingColor, m_LineWidth, System.Windows.Media.PenLineCap.Round, System.Windows.Media.PenLineCap.Round, 1.0f); } catch { } }
        private void TryAddConnectedLines() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddConnectedLinesAnnotInteractive(m_StrokingColor, m_LineWidth, 1.0f); } catch { } }
        private void TryAddLineArrow() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddLineArrowAnnotInteractive(m_StrokingColor, m_LineWidth, 10f, 10f, true, 1.0f, false); } catch { } }
        private void TryAddTextAnnotation() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddTextAnnotationInteractive(txtText.Text, m_StrokingColor, "Arial", System.Drawing.FontStyle.Regular, 12f, true, m_StrokingColor, m_FillingColor, 1.0f, 0f); } catch { } }
        private void TryAddStickyNote() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddStickyNoteAnnotationInteractive(txtText.Text, m_StrokingColor, "Arial", System.Drawing.FontStyle.Regular, 12f, 1.0f, 0f); } catch { } }
        private void TryAddFreeHandPolygon() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddFreeHandPolygonAnnotInteractive(m_StrokingColor, m_LineWidth, m_FillingColor, 1.0f); } catch { } }
        private void TryAddLink() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddLinkAnnotationInteractive("Link", "http://www.google.com", m_StrokingColor, m_StrokingColor, "Arial", System.Drawing.FontStyle.Regular, System.Drawing.FontStyle.Regular, 1.0f, 0f); } catch { } }
        private void TryAddPolygon() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddPolygonAnnotInteractive(m_StrokingColor, m_LineWidth, m_FillingColor, 1.0f); } catch { } }
        private void TryAddRuler() { try { GdViewer1.CancelLastAnnotInteractiveAdd(); GdViewer1.AddRulerAnnotInteractive(m_StrokingColor, m_LineWidth, 1.0f, Gd.Annotations.Annotation.UnitMode.Inch); } catch { } }

        private void OpenFileCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void OpenFileCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "All Files|*.*" };
            if (dlg.ShowDialog(this) == true) { try { GdViewer1.DisplayFromFile(dlg.FileName, ""); ShowPageCount(); } catch { } }
        }

        private void PrintCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.PrintDialog(Gd.PrintSizeOption.PrintSizeOptionActual, this); } catch { } }

        private void SaveCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { /* TODO: implement Save document */ } catch { } }

        private void CloseCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.CloseDocument(); ShowPageCount(); } catch { } }

        private void ExitCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void ExitCommand_Executed(object sender, ExecutedRoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

        private void FirstPageCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void FirstPageCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.DisplayFirstPage(); } catch { } }

        private void PreviousPageCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.CurrentPage > 1);
        private void PreviousPageCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.DisplayPreviousPage(); } catch { } }

        private void LastPageCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void LastPageCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.DisplayLastPage(); } catch { } }

        private void NextPageCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.CurrentPage < GdViewer1.PageCount);
        private void NextPageCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.DisplayNextPage(); } catch { } }

        private void DocumentPositionCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void DocumentAlignmentCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

        private void PositionMLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionML.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionMiddleLeft" }); }
        private void PositionMCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionMC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionMiddleCenter" }); }
        private void PositionMRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionMR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionMiddleRight" }); }
        private void PositionTLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionTL.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionTopLeft" }); }
        private void PositionTCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionTC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionTopCenter" }); }
        private void PositionTRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionTR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionTopRight" }); }
        private void PositionBLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionBL.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionBottomLeft" }); }
        private void PositionBCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionBC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionBottomCenter" }); }
        private void PositionBRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckPositions(); PositionBR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentPosition", new[] { "DocumentPositionBottomRight" }); }

        private void UncheckPositions()
        {
            try
            {
                PositionML.IsChecked = PositionMC.IsChecked = PositionMR.IsChecked =
                PositionTL.IsChecked = PositionTC.IsChecked = PositionTR.IsChecked =
                PositionBL.IsChecked = PositionBC.IsChecked = PositionBR.IsChecked = false;
            }
            catch { }
        }

        private void AlignmentMLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentML.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentMiddleLeft" }); }
        private void AlignmentMCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentMC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentMiddleCenter" }); }
        private void AlignmentMRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentMR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentMiddleRight" }); }
        private void AlignmentTLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentTL.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentTopLeft" }); }
        private void AlignmentTCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentTC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentTopCenter" }); }
        private void AlignmentTRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentTR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentTopRight" }); }
        private void AlignmentBLCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentBL.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentBottomLeft" }); }
        private void AlignmentBCCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentBC.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentBottomCenter" }); }
        private void AlignmentBRCommand_Executed(object s, ExecutedRoutedEventArgs e) { UncheckAlignments(); AlignmentBR.IsChecked = true; TrySetViewerEnumProperty(GdViewer1, "DocumentAlignment", new[] { "DocumentAlignmentBottomRight" }); }

        private void UncheckAlignments()
        {
            try
            {
                AlignmentML.IsChecked = AlignmentMC.IsChecked = AlignmentMR.IsChecked =
                AlignmentTL.IsChecked = AlignmentTC.IsChecked = AlignmentTR.IsChecked =
                AlignmentBL.IsChecked = AlignmentBC.IsChecked = AlignmentBR.IsChecked = false;
            }
            catch { }
        }

        private void ThumbnailSizeCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void SmallThumbCommand_Executed(object sender, ExecutedRoutedEventArgs e) { ThumbnailEx1.ThumbnailSize = new System.Windows.Size(64, 64); smallThumbnailsMenuItem.IsChecked = true; mediumThumbnailsMenuItem.IsChecked = false; largeThumbnailsMenuItem.IsChecked = false; extralargeThumbnailsMenuItem.IsChecked = false; }
        private void MediumThumbCommand_Executed(object sender, ExecutedRoutedEventArgs e) { ThumbnailEx1.ThumbnailSize = new System.Windows.Size(128, 128); smallThumbnailsMenuItem.IsChecked = false; mediumThumbnailsMenuItem.IsChecked = true; largeThumbnailsMenuItem.IsChecked = false; extralargeThumbnailsMenuItem.IsChecked = false; }
        private void LargeThumbCommand_Executed(object sender, ExecutedRoutedEventArgs e) { ThumbnailEx1.ThumbnailSize = new System.Windows.Size(256, 256); smallThumbnailsMenuItem.IsChecked = false; mediumThumbnailsMenuItem.IsChecked = false; largeThumbnailsMenuItem.IsChecked = true; extralargeThumbnailsMenuItem.IsChecked = false; }
        private void ExLargeThumbCommand_Executed(object sender, ExecutedRoutedEventArgs e) { ThumbnailEx1.ThumbnailSize = new System.Windows.Size(512, 512); smallThumbnailsMenuItem.IsChecked = false; mediumThumbnailsMenuItem.IsChecked = false; largeThumbnailsMenuItem.IsChecked = false; extralargeThumbnailsMenuItem.IsChecked = true; }

        private void RotateCommand_CanExecute(object s, CanExecuteRoutedEventArgs e) => e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0);
        private void Rotate90Command_Executed(object s, ExecutedRoutedEventArgs e) { TryInvokeRotatePage(GdViewer1, new[] { "Rotate90", "Rotate_90", "Rotate90CW" }); }
        private void Rotate180Command_Executed(object s, ExecutedRoutedEventArgs e) { TryInvokeRotatePage(GdViewer1, new[] { "Rotate180", "Rotate_180", "Rotate180CW" }); }
        private void Rotate270Command_Executed(object s, ExecutedRoutedEventArgs e) { TryInvokeRotatePage(GdViewer1, new[] { "Rotate270", "Rotate_270", "Rotate270CW" }); }
        private void FlipXCommand_Executed(object s, ExecutedRoutedEventArgs e) { TryInvokeRotatePage(GdViewer1, new[] { "FlipX", "FlipHorizontal", "Flip_Horizontal" }); }
        private void FlipYCommand_Executed(object s, ExecutedRoutedEventArgs e) { TryInvokeRotatePage(GdViewer1, new[] { "FlipY", "FlipVertical", "Flip_Vertical" }); }

        private void ViewRotate90Command_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.RotateView(Gd.GdPictureRotateFlipType.GdPictureRotate90FlipNone); } catch { } }
        private void ViewRotate180Command_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.RotateView(Gd.GdPictureRotateFlipType.GdPictureRotate180FlipNone); } catch { } }
        private void ViewRotate270Command_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.RotateView(Gd.GdPictureRotateFlipType.GdPictureRotate270FlipNone); } catch { } }
        private void ViewFlipXCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.RotateView(Gd.GdPictureRotateFlipType.GdPictureRotateNoneFlipX); } catch { } }
        private void ViewFlipYCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.RotateView(Gd.GdPictureRotateFlipType.GdPictureRotateNoneFlipY); } catch { } }

        private void TryInvokeRotatePage(object viewer, string[] candidateEnumNames)
        {
            if (viewer == null) return;
            try
            {
                var vt = viewer.GetType();
                var method = vt.GetMethod("RotatePage", BindingFlags.Public | BindingFlags.Instance);
                if (method == null) return;
                var parameters = method.GetParameters();
                if (parameters.Length != 1) return;
                var enumType = parameters[0].ParameterType;
                if (!enumType.IsEnum) return;
                var names = Enum.GetNames(enumType);
                foreach (var cand in candidateEnumNames)
                {
                    var match = names.FirstOrDefault(n => string.Equals(n, cand, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        var val = Enum.Parse(enumType, match);
                        method.Invoke(viewer, new object[] { val });
                        return;
                    }
                }
            }
            catch { }
        }

        private void ZoomCommand_CanExecute(object s, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void Zoom50Command_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.Zoom = 0.5; } catch { } }
        private void Zoom100Command_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.Zoom = 1.0; } catch { } }
        private void Zoom150Command_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.Zoom = 1.5; } catch { } }
        private void Zoom200Command_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.Zoom = 2.0; } catch { } }
        private void ZoomINCommand_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.ZoomIN(); } catch { } }
        private void ZoomOUTCommand_Executed(object s, ExecutedRoutedEventArgs e) { try { GdViewer1.ZoomOUT(); } catch { } }
        private void ZoomBestFitCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "ZoomMode", new[] { "BestFit", "ZoomModeBestFit" }); }
        private void ZoomHeightFitCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "ZoomMode", new[] { "HeightFit", "ZoomModeHeightFit" }); }
        private void ZoomWidthFitCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "ZoomMode", new[] { "WidthFit", "ZoomModeWidthFit" }); }

        private void MouseModeCommand_CanExecute(object s, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void MousePanCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseMode", new[] { "Pan", "MouseModePan" }); }
        private void MouseAutoZoomCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseMode", new[] { "AutoZoom", "MouseModeAutoZoom" }); }
        private void MouseAreaSelectionCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseMode", new[] { "AreaSelection", "MouseModeAreaSelection" }); }
        private void MouseMagnifierCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseMode", new[] { "Magnifier", "MouseModeMagnifier" }); }
        private void MouseNothingCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseMode", new[] { "Nothing", "MouseModeNothing" }); }

        private void MouseWheelCommand_CanExecute(object s, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void MouseWheelZoomCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseWheelMode", new[] { "Zoom", "MouseWheelModeZoom" }); }
        private void MouseWheelVerticalScrollCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseWheelMode", new[] { "VerticalScroll", "MouseWheelModeVerticalScroll" }); }
        private void MouseWheelPageChangeCommand_Executed(object s, ExecutedRoutedEventArgs e) { TrySetViewerEnumProperty(GdViewer1, "MouseWheelMode", new[] { "PageChange", "MouseWheelModePageChange" }); }

        private void RenderEngineCommand_CanExecute(object s, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void RenderEngineGDICommand_Executed(object s, ExecutedRoutedEventArgs e) { try { } catch { } }
        private void RenderEngineWPFCommand_Executed(object s, ExecutedRoutedEventArgs e) { try { } catch { } }
        private void RenderEngineHYBRIDCommand_Executed(object s, ExecutedRoutedEventArgs e) { try { } catch { } }

        private void EditorModeCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
        private void EditorModeCommand_Executed(object sender, ExecutedRoutedEventArgs e) { try { GdViewer1.SetAnnotationEditorMode(EditorMode.IsChecked == true); toolBar1.IsEnabled = (EditorMode.IsChecked == true); } catch { } }

        private void RemoveSelectedCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.GetSelectedAnnotationIdx() != -1); } catch { e.CanExecute = false; }
        }
        private void RemoveSelectedCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try { int idx = GdViewer1.GetSelectedAnnotationIdx(); if (idx >= 0) GdViewer1.DeleteAnnotation(idx); } catch { }
        }

        private void RemoveAllCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.GetAnnotationCount() > 0); } catch { e.CanExecute = false; }
        }
        private void RemoveAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try { while (GdViewer1.GetAnnotationCount() > 0) GdViewer1.DeleteAnnotation(0); } catch { }
        }

        private void SaveCurrentCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0); } catch { e.CanExecute = false; }
        }
        private void SaveCurrentCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save Annotations As XMP", Filter = "XMP|*.xmp" };
                if (dlg.ShowDialog(this) == true)
                {
                    if (GdViewer1.SaveAnnotationsToXMP(dlg.FileName) == Gd.GdPictureStatus.OK) MessageBox.Show("Annotations saved: " + dlg.FileName);
                    else MessageBox.Show("Can't save annotations: " + dlg.FileName);
                }
            }
            catch { }
        }

        private void SaveAllCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0); } catch { e.CanExecute = false; }
        }
        private void SaveAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save Annotations As XMP", Filter = "XMP|*.xmp" };
                if (dlg.ShowDialog(this) == true)
                {
                    if (GdViewer1.SaveAnnotationsToXMPEx(dlg.FileName) == Gd.GdPictureStatus.OK) MessageBox.Show("Annotations saved: " + dlg.FileName);
                    else MessageBox.Show("Can't save annotations: " + dlg.FileName);
                }
            }
            catch { }
        }

        private void LoadFromXMLCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0); } catch { e.CanExecute = false; }
        }
        private void LoadFromXMLCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Load Annotations from XMP file", Filter = "XMP|*.xmp" };
                if (dlg.ShowDialog(this) == true) { if (GdViewer1.LoadAnnotationsFromXMP(dlg.FileName) != Gd.GdPictureStatus.OK) MessageBox.Show("Can't load annotations from " + dlg.FileName); }
            }
            catch { }
        }

        private void BurnAnnotationsCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            try { e.CanExecute = (GdViewer1 != null && GdViewer1.PageCount > 0 && GdViewer1.GetAnnotationCount() > 0); } catch { e.CanExecute = false; }
        }
        private void BurnAnnotationsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            try { GdViewer1.BurnAnnotationsToPage(true); } catch { }
        }

        private void ShowPageCount()
        {
            try
            {
                if (GdViewer1 != null && GdViewer1.PageCount > 0 && GdViewer1.CurrentPage != 0)
                    labelCurrentPage.Content = GdViewer1.CurrentPage.ToString() + "/" + GdViewer1.PageCount.ToString();
                else labelCurrentPage.Content = "";
            }
            catch { labelCurrentPage.Content = ""; }
        }

        private void GdViewer1_PageChanged(object sender, GdWPF.GdViewer.PageChangedEventArgs e)
        {
            try { GdViewer1.RemoveAllRegions(); ShowPageCount(); } catch { }
        }

        private void GdViewer1_PageDisplayed(object sender, GdWPF.GdViewer.PageDisplayedEventArgs e)
        {
            try
            {
                this.statusLabel.Content = "Page No : " + GdViewer1.CurrentPage.ToString() + " / " + GdViewer1.PageCount.ToString() +
                                           "   -   " +
                                           "Page Size (inches) : " + Math.Round(GdViewer1.PageInchHeight, 2).ToString() + " * " + Math.Round(GdViewer1.PageInchWidth, 2).ToString() +
                                           "   -   " + "Page Size (pixels) : " + GdViewer1.PagePixelHeight.ToString() + " * " + GdViewer1.PagePixelWidth.ToString() +
                                           "   -   " + "Horizontal resolution : " + Math.Round(GdViewer1.PageHorizontalResolution, 2).ToString() + " dpi   -   " +
                                           "Vertical resolution : " + Math.Round(GdViewer1.PageVerticalResolution, 2).ToString() + " dpi";
            }
            catch { }
        }

        private void TrySetViewerEnumProperty(object viewer, string propertyName, string[] candidateNames)
        {
            if (viewer == null) return;
            try
            {
                var viewerType = viewer.GetType();
                var prop = viewerType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return;
                var enumType = prop.PropertyType;
                if (!enumType.IsEnum)
                {
                    enumType = viewerType.Assembly.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name.IndexOf(propertyName, StringComparison.OrdinalIgnoreCase) >= 0) ?? enumType;
                }
                if (!enumType.IsEnum) return;
                var enumNames = Enum.GetNames(enumType);
                foreach (var candidate in candidateNames)
                {
                    var match = enumNames.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
                    if (match != null) { prop.SetValue(viewer, Enum.Parse(enumType, match)); return; }
                    var stripped = candidate;
                    var toRemove = new[] { "MouseMode", "MouseWheelMode", "Viewer", "Mode", "ZoomMode", "Document" };
                    foreach (var pref in toRemove) stripped = RemovePrefixIgnoreCase(stripped, pref);
                    match = enumNames.FirstOrDefault(n => string.Equals(n, stripped, StringComparison.OrdinalIgnoreCase));
                    if (match != null) { prop.SetValue(viewer, Enum.Parse(enumType, match)); return; }
                    match = enumNames.FirstOrDefault(n => n.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null) { prop.SetValue(viewer, Enum.Parse(enumType, match)); return; }
                    match = enumNames.FirstOrDefault(n => n.IndexOf(stripped, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null) { prop.SetValue(viewer, Enum.Parse(enumType, match)); return; }
                }
            }
            catch { 
            
            }
        }

        private string RemovePrefixIgnoreCase(string source, string prefix)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(prefix)) return source;
            int idx = source.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return source;
            return source.Remove(idx, prefix.Length);
        }
    }

    public enum DrawingDirection { LeftToRight, RightToLeft, TopToBottom, BottomToTop, Diagonal }

}
