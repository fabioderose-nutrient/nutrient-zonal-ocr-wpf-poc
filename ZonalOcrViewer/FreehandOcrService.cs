using System;
using System.Globalization;
using System.Drawing.Drawing2D;
using GdPicture14;
using GdPicture14.WPF;
using System.Windows.Media;

namespace ZonalOcrViewer
{
    public class FreehandOcrService
    {
        public string FreehandOcr(GdPicture14.WPF.GdViewer viewer, int annotationID, System.Windows.Point mouseStart, System.Windows.Point mouseEnd)
        {
            try
            {
                var annotManager = viewer.GetAnnotationManager();
                var annotType = annotManager.GetAnnotationType(annotationID);

                if (annotType != GdPicture14.Annotations.Annotation.GdPictureAnnotationType.AnnotationTypeFreeHandHighlighter &&
                    !annotType.ToString().ToLower().Contains("freehand"))
                {
                    return null;
                }

                double annotLeft = ToDoubleSafe(annotManager.GetAnnotationPropertyValue(annotationID, "Left"));
                double annotTop = ToDoubleSafe(annotManager.GetAnnotationPropertyValue(annotationID, "Top"));
                double annotWidth = ToDoubleSafe(annotManager.GetAnnotationPropertyValue(annotationID, "Width"));
                double annotHeight = ToDoubleSafe(annotManager.GetAnnotationPropertyValue(annotationID, "Height"));

                double dpiX = viewer.PageHorizontalResolution;
                double dpiY = viewer.PageVerticalResolution;

                // CopyRegionToGdPictureImage expects coordinates in the device's physical
                // pixel space. For rendered document formats (PDF, Office, HTML, etc.) this
                // is the page DPI multiplied by the system DPI scale factor (for example 2.0
                // on a 200% Windows display). For raster image formats (JPEG, PNG, TIFF, ...)
                // the method uses the image's native pixels as-is, so we must not apply the
                // system DPI scale, otherwise the extracted region ends up shifted toward
                // the bottom-right of the page.
                double dpiScaleX = 1.0, dpiScaleY = 1.0;
                if (!LineOcrService.IsRasterImageFormat(viewer.DocumentFormat))
                {
                    try
                    {
                        var sysDpi = System.Windows.Media.VisualTreeHelper.GetDpi((System.Windows.Media.Visual)viewer);
                        dpiScaleX = sysDpi.DpiScaleX;
                        dpiScaleY = sysDpi.DpiScaleY;
                    }
                    catch { }
                }

                // GdPicture exposes Annotation.Left/Top as the CENTER of the bounding box,
                // not the top-left corner. Subtract half the width/height to get the actual
                // top-left corner before converting to pixels.
                double leftInches = annotLeft - (annotWidth / 2.0);
                double topInches = annotTop - (annotHeight / 2.0);

                int leftPx = (int)Math.Floor(leftInches * dpiX * dpiScaleX);
                int topPx = (int)Math.Floor(topInches * dpiY * dpiScaleY);
                int widthPx = (int)Math.Ceiling(annotWidth * dpiX * dpiScaleX);
                int heightPx = (int)Math.Ceiling(annotHeight * dpiY * dpiScaleY);

                if (widthPx <= 0 || heightPx <= 0)
                {
                    return null;
                }

                const int margin = 30;
                leftPx = Math.Max(0, leftPx - margin);
                topPx = Math.Max(0, topPx - margin);
                widthPx = widthPx + (margin * 2);
                heightPx = heightPx + (margin * 2);

                // Page bounds must be expressed in the same pixel space as the coordinates
                // we pass to CopyRegionToGdPictureImage (physical pixels for rendered
                // documents, native pixels for raster images).
                int pagePhysW = (int)(viewer.PagePixelWidth * dpiScaleX);
                int pagePhysH = (int)(viewer.PagePixelHeight * dpiScaleY);
                if (leftPx + widthPx > pagePhysW)
                    widthPx = pagePhysW - leftPx;
                if (topPx + heightPx > pagePhysH)
                    heightPx = pagePhysH - topPx;

                if (widthPx <= 0 || heightPx <= 0)
                {
                    return null;
                }

                int extractedImageId = viewer.CopyRegionToGdPictureImage(leftPx, topPx, widthPx, heightPx);
                if (extractedImageId <= 0)
                {
                    return null;
                }

                double angleDegrees = CalculateAngle(mouseStart, mouseEnd, widthPx, heightPx);
                double dx = mouseEnd.X - mouseStart.X;
                double dy = mouseEnd.Y - mouseStart.Y;

                if (IsDiagonal(angleDegrees, widthPx, heightPx))
                {
                    RotateToHorizontal(extractedImageId, angleDegrees);

                    using (var img = new GdPictureImaging())
                    {
                        int imgHeight = img.GetHeight(extractedImageId);
                        int rectHeight = imgHeight * 35 / 100;
                        img.CropTop(extractedImageId, rectHeight);
                        img.CropBottom(extractedImageId, rectHeight);
                    }
                }
                else
                {
                    ApplyIntelligentRotation(extractedImageId, dx, dy);
                }

                // OCR first, then keep the crop for the OCR result window (see SaveCrop).
                string text = OcrPipeline.RunOcr(extractedImageId);
                OcrPipeline.SaveCrop(extractedImageId);
                return text;
            }
            catch
            {
                return null;
            }
        }

        private void ApplyIntelligentRotation(int imageId, double dx, double dy)
        {
            using (var img = new GdPictureImaging())
            {
                try
                {
                    double angleRadians = Math.Atan2(dy, dx);
                    double angleDegrees = angleRadians * (180.0 / Math.PI);

                    while (angleDegrees < 0) angleDegrees += 360;
                    while (angleDegrees >= 360) angleDegrees -= 360;

                    float rotationAngle;
                    if (angleDegrees >= 315 || angleDegrees < 45)
                        rotationAngle = 0;
                    else if (angleDegrees >= 45 && angleDegrees < 135)
                        rotationAngle = 270;
                    else if (angleDegrees >= 135 && angleDegrees < 225)
                        rotationAngle = 180;
                    else
                        rotationAngle = 90;

                    if (rotationAngle != 0)
                    {
                        img.RotateAngle(imageId, rotationAngle);
                    }
                }
                catch { }
            }
        }

        private double CalculateAngle(System.Windows.Point mouseStart, System.Windows.Point mouseEnd, int widthPx, int heightPx)
        {
            double dx = mouseEnd.X - mouseStart.X;
            double dy = mouseEnd.Y - mouseStart.Y;

            if (Math.Abs(dx) > 1.0 || Math.Abs(dy) > 1.0)
            {
                return Math.Atan2(dy, dx) * (180.0 / Math.PI);
            }

            double ratio = (double)widthPx / heightPx;
            if (Math.Abs(ratio - 1.0) < 0.3)
                return 45.0;
            return Math.Atan2(heightPx, widthPx) * (180.0 / Math.PI);
        }

        private bool IsDiagonal(double angleDegrees, int widthPx, int heightPx)
        {
            bool dimensionsBalanced = widthPx > 20 && heightPx > 20 &&
                                     Math.Min(widthPx, heightPx) > Math.Max(widthPx, heightPx) * 0.3;

            double angle = angleDegrees;
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;

            double absAngle = Math.Abs(angle);
            bool isHorizontal = (absAngle <= 15) || (absAngle >= 165);
            bool isVertical = (absAngle >= 75 && absAngle <= 105);

            return dimensionsBalanced && !isHorizontal && !isVertical;
        }

        private void RotateToHorizontal(int imageId, double currentAngle)
        {
            using (var img = new GdPictureImaging())
            {
                try
                {
                    while (currentAngle > 180) currentAngle -= 360;
                    while (currentAngle < -180) currentAngle += 360;

                    float rotationAngle = (float)(-currentAngle);
                    while (rotationAngle > 180) rotationAngle -= 360;
                    while (rotationAngle < -180) rotationAngle += 360;

                    if (Math.Abs(rotationAngle) < 15)
                        return;

                    img.RotateAngle(imageId, rotationAngle);
                }
                catch { }
            }
        }

        private double ToDoubleSafe(object value)
        {
            if (value == null) return 0.0;
            if (value is double d) return d;
            if (value is float f) return Convert.ToDouble(f);
            if (value is int i) return Convert.ToDouble(i);
            if (value is long l) return Convert.ToDouble(l);
            if (value is decimal dec) return Convert.ToDouble(dec);

            string str = value.ToString();
            if (string.IsNullOrWhiteSpace(str)) return 0.0;
            if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                return result;
            if (double.TryParse(str, out result))
                return result;

            try { return Convert.ToDouble(value); }
            catch { return 0.0; }
        }
    }
}
