using System;
using GdPicture14;

namespace ZonalOcrViewer
{
    // Supports the "Line OCR" feature. Given two viewer-space points (the endpoints of a
    // connected-lines annotation), it crops the page around the line, rotates the crop
    // toward horizontal and runs GdPicture OCR on it (see OcrPipeline).
    public class LineOcrService
    {
        // Identifies raster image formats whose page is displayed at its native pixel
        // resolution (no additional DPI scaling). For those formats the coordinates
        // passed to CopyRegionToGdPictureImage are in native image pixels, so the
        // Windows DPI scale factor must NOT be applied. For all other document formats
        // (PDF, Office, HTML, ...) GdPicture renders the page at the system's physical
        // DPI, and the scale factor must be applied to reach the correct pixel region.
        public static bool IsRasterImageFormat(GdPicture14.DocumentFormat fmt)
        {
            switch (fmt)
            {
                case GdPicture14.DocumentFormat.DocumentFormatBMP:
                case GdPicture14.DocumentFormat.DocumentFormatGIF:
                case GdPicture14.DocumentFormat.DocumentFormatJPEG:
                case GdPicture14.DocumentFormat.DocumentFormatPNG:
                case GdPicture14.DocumentFormat.DocumentFormatTIFF:
                case GdPicture14.DocumentFormat.DocumentFormatJ2K:
                case GdPicture14.DocumentFormat.DocumentFormatJP2:
                case GdPicture14.DocumentFormat.DocumentFormatEXR:
                case GdPicture14.DocumentFormat.DocumentFormatPBM:
                case GdPicture14.DocumentFormat.DocumentFormatTARGA:
                case GdPicture14.DocumentFormat.DocumentFormatWBMP:
                case GdPicture14.DocumentFormat.DocumentFormatWEBP:
                case GdPicture14.DocumentFormat.DocumentFormatWMF:
                case GdPicture14.DocumentFormat.DocumentFormatXPM:
                    return true;
                default:
                    return false;
            }
        }

        // Main entry point. Both input points are in viewer (WPF DIP) space. The method
        // converts them to document inches, computes the padded bounding rectangle,
        // extracts the underlying bitmap from the viewer, rotates it toward horizontal
        // when the line was drawn diagonally, and finally runs OCR on it.
        public string LineOcr(GdPicture14.WPF.GdViewer viewer, System.Windows.Point viewerPoint1, System.Windows.Point viewerPoint2)
        {
            try
            {
                double p1InX = 0, p1InY = 0, p2InX = 0, p2InY = 0;
                viewer.CoordViewerPixelToDocumentInch(viewerPoint1.X, viewerPoint1.Y, ref p1InX, ref p1InY);
                viewer.CoordViewerPixelToDocumentInch(viewerPoint2.X, viewerPoint2.Y, ref p2InX, ref p2InY);

                // Only apply the Windows DPI scale when the page is rendered by GdPicture
                // (PDF / Office / HTML). Raster images are displayed at their native
                // resolution and already match CopyRegionToGdPictureImage's pixel space.
                double dpiScaleX = 1.0, dpiScaleY = 1.0;
                if (!IsRasterImageFormat(viewer.DocumentFormat))
                {
                    try
                    {
                        var sysDpi = System.Windows.Media.VisualTreeHelper.GetDpi((System.Windows.Media.Visual)viewer);
                        dpiScaleX = sysDpi.DpiScaleX;
                        dpiScaleY = sysDpi.DpiScaleY;
                    }
                    catch { }
                }

                double dpiX = viewer.PageHorizontalResolution * dpiScaleX;
                double dpiY = viewer.PageVerticalResolution * dpiScaleY;
                int pagePhysW = (int)(viewer.PagePixelWidth * dpiScaleX);
                int pagePhysH = (int)(viewer.PagePixelHeight * dpiScaleY);

                if (!OcrPipeline.LineCropRectangle(p1InX, p1InY, p2InX, p2InY, dpiX, dpiY, pagePhysW, pagePhysH,
                        out int leftPx, out int topPx, out int widthPx, out int heightPx))
                    return null;

                int extractedImageId = viewer.CopyRegionToGdPictureImage(leftPx, topPx, widthPx, heightPx);
                if (extractedImageId <= 0)
                    return null;

                // Angle of the line from the two mouse points.
                double dx = viewerPoint2.X - viewerPoint1.X;
                double dy = viewerPoint2.Y - viewerPoint1.Y;
                OcrPipeline.StraightenLine(extractedImageId, Math.Atan2(dy, dx) * (180.0 / Math.PI));

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
    }
}
