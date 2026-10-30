using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdPicture14;

namespace ZonalOcrViewer
{
    /// <summary>
    /// Viewer-independent part of the zonal OCR: crop rectangle around a line, rotation of
    /// the crop toward horizontal, image clean-up and GdPicture OCR. Used by the WPF services
    /// and by the headless preview tool (tools/OcrPreview), which compiles this same file.
    /// </summary>
    public static class OcrPipeline
    {
        /// <summary>Padding added around the crop, in pixels, so the OCR engine gets some context.</summary>
        public const int CropMargin = 30;

        /// <summary>Where the last crop sent to OCR is saved, for the result window.</summary>
        public static string CropImagePath { get; } = Path.Combine(Path.GetTempPath(), "zonal-ocr-crop.jpeg");

        /// <summary>
        /// Axis-aligned rectangle, in pixels, around the segment (x1, y1)-(x2, y2) given in
        /// document inches, padded by <see cref="CropMargin"/> and clamped to the page.
        /// Returns false when the rectangle is empty.
        /// </summary>
        public static bool LineCropRectangle(double x1In, double y1In, double x2In, double y2In,
            double dpiX, double dpiY, int pageWidthPx, int pageHeightPx,
            out int left, out int top, out int width, out int height)
        {
            left = (int)Math.Floor(Math.Min(x1In, x2In) * dpiX);
            top = (int)Math.Floor(Math.Min(y1In, y2In) * dpiY);
            width = (int)Math.Ceiling(Math.Abs(x2In - x1In) * dpiX);
            height = (int)Math.Ceiling(Math.Abs(y2In - y1In) * dpiY);

            left = Math.Max(0, left - CropMargin);
            top = Math.Max(0, top - CropMargin);
            width += CropMargin * 2;
            height += CropMargin * 2;

            if (left + width > pageWidthPx) width = pageWidthPx - left;
            if (top + height > pageHeightPx) height = pageHeightPx - top;
            return width > 0 && height > 0;
        }

        /// <summary>
        /// Rotates the crop so that a line drawn at <paramref name="angleDegrees"/> (screen
        /// convention: y grows downward) becomes horizontal. Nearly horizontal lines (within
        /// 15 degrees) are left as is to avoid interpolation artefacts.
        /// </summary>
        public static void StraightenLine(int imageId, double angleDegrees)
        {
            double absAngle = Math.Abs(angleDegrees);
            while (absAngle > 180) absAngle -= 360;
            absAngle = Math.Abs(absAngle);
            if (absAngle <= 15 || absAngle >= 165) return;

            float rotation = (float)(-angleDegrees);
            while (rotation > 180) rotation -= 360;
            while (rotation < -180) rotation += 360;

            using (var img = new GdPictureImaging())
            {
                try { img.RotateAngle(imageId, rotation); } catch { }
            }
        }

        /// <summary>
        /// Saves the crop for the result window. Call it after <see cref="RunOcr"/>: in
        /// evaluation mode GdPicture stamps its watermark on the image when saving it, and
        /// OCR on a watermarked crop returns garbage.
        /// </summary>
        public static void SaveCrop(int imageId)
        {
            try
            {
                using (var img = new GdPictureImaging())
                {
                    img.SaveAsJPEG(imageId, CropImagePath);
                }
            }
            catch { }
        }

        /// <summary>
        /// Cleans the image up (upscale small crops, sharpen, 1-bit black and white) and runs
        /// GdPicture OCR on it. Returns null when the OCR resources are missing.
        /// </summary>
        public static string RunOcr(int imageId)
        {
            string resources = OcrResourcesFolder();
            if (resources == null) return null;

            using (var img = new GdPictureImaging())
            {
                int processedImageId = PreprocessImageForOcr(img, imageId);

                using (var ocr = new GdPictureOCR())
                {
                    ocr.ResourcesFolder = resources;
                    ocr.AddLanguage(OCRLanguage.Latin);
                    ocr.OCRMode = OCRMode.FavorAccuracy;
                    ocr.EnableSkewDetection = true;
                    ocr.EnableOrientationDetection = true;
                    ocr.OrientationDetectionAccuracyLevel = 10;

                    ocr.SetImage(processedImageId);
                    string result = ocr.RunOCR();

                    if (processedImageId != imageId)
                        img.ReleaseGdPictureImage(processedImageId);

                    return string.IsNullOrEmpty(result) ? null : ocr.GetOCRResultText(result);
                }
            }
        }

        // Works on a copy of the crop, so the crop itself stays as displayed. Upscales small
        // crops to a minimum dimension, sharpens edges and converts to 1-bit black and white.
        // Improves recognition on thin fonts and low-resolution sources.
        private static int PreprocessImageForOcr(GdPictureImaging img, int imageId)
        {
            int processedId = img.CreateClonedGdPictureImage(imageId);
            if (processedId <= 0) return imageId;
            try
            {
                int width = img.GetWidth(processedId);
                int height = img.GetHeight(processedId);

                const int minDimension = 300;
                if (width < minDimension || height < minDimension)
                {
                    float scaleFactor = Math.Max((float)minDimension / width, (float)minDimension / height);
                    int newWidth = (int)(width * scaleFactor);
                    int newHeight = (int)(height * scaleFactor);
#if GDPICTURE_API
                    // Cross-platform GdPicture.API (tools/OcrPreview).
                    img.Resize(processedId, newWidth, newHeight, GdPicture14.Imaging.GdPictureInterpolationMode.HighQualityBicubic);
#else
                    img.Resize(processedId, newWidth, newHeight, System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic);
#endif
                }

                img.FxSharpen(processedId);
                img.ConvertTo1Bpp(processedId);
                return processedId;
            }
            catch
            {
                return processedId;
            }
        }

        /// <summary>
        /// Folder with the OCR dictionaries (lat.traineddata): OCR_RESOURCES_PATH, an "ocr"
        /// folder next to the app, then the GdPicture.Resources package in the NuGet cache.
        /// </summary>
        public static string OcrResourcesFolder()
        {
            const string dictionary = "lat.traineddata";
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable("OCR_RESOURCES_PATH");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(Path.GetFullPath(env));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "ocr"));

            string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrEmpty(nugetRoot))
                nugetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            string package = Path.Combine(nugetRoot, "gdpicture.resources");
            if (Directory.Exists(package))
                foreach (string version in Directory.GetDirectories(package).OrderByDescending(v => v))
                    candidates.Add(Path.Combine(version, "runtimes", "any", "native"));

            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, dictionary)));
        }
    }
}
