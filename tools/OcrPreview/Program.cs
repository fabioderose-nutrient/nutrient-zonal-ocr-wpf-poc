using System.Text.Json;
using GdPicture14;
using ZonalOcrViewer;

// Runs Line OCR without the WPF viewer: renders the page, crops around each line listed in
// lines.json, straightens the crop and runs OCR with the same OcrPipeline as the app.
//
// Usage: OcrPreview [document.pdf] [lines.json] [output-folder]

new LicenseManager().RegisterKEY(Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "");

string pdfPath = args.Length > 0 ? args[0] : Path.Combine("samples", "recorded-document.pdf");
string linesPath = args.Length > 1 ? args[1] : Path.Combine("samples", "lines.json");
string outDir = Path.GetFullPath(args.Length > 2 ? args[2] : "output");
Directory.CreateDirectory(outDir);

if (OcrPipeline.OcrResourcesFolder() == null)
{
    Console.Error.WriteLine("OCR resources (lat.traineddata) not found. Set OCR_RESOURCES_PATH.");
    return 1;
}

const float Dpi = 150f;
using var pdf = new GdPicturePDF();
if (pdf.LoadFromFile(pdfPath, false) != GdPictureStatus.OK)
{
    Console.Error.WriteLine($"Cannot open {pdfPath}");
    return 1;
}
pdf.SelectPage(1);
int pageImage = pdf.RenderPageToGdPictureImage(Dpi, true);

using var imaging = new GdPictureImaging();
int pageW = imaging.GetWidth(pageImage), pageH = imaging.GetHeight(pageImage);
var lines = JsonSerializer.Deserialize<List<LineSpec>>(File.ReadAllText(linesPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var results = new List<object>();

foreach (var line in lines)
{
    if (!OcrPipeline.LineCropRectangle(line.X1, line.Y1, line.X2, line.Y2, Dpi, Dpi, pageW, pageH,
            out int left, out int top, out int width, out int height))
        continue;

    int crop = imaging.CreateClonedGdPictureImageArea(pageImage, left, top, width, height);
    double angle = Math.Atan2(line.Y2 - line.Y1, line.X2 - line.X1) * 180.0 / Math.PI;
    OcrPipeline.StraightenLine(crop, angle);
    // OCR first, then save the crop (in evaluation mode saving stamps a watermark on it).
    string text = (OcrPipeline.RunOcr(crop) ?? "").Trim();
    OcrPipeline.SaveCrop(crop);
    string cropFile = Path.Combine(outDir, $"crop-{line.Name}.jpeg");
    File.Copy(OcrPipeline.CropImagePath, cropFile, true);
    imaging.ReleaseGdPictureImage(crop);

    Console.WriteLine($"{line.Name}: angle {angle:F1} deg, crop {width}x{height} px -> \"{text}\"");
    results.Add(new { line.Name, angle = Math.Round(angle, 1), crop = Path.GetFileName(cropFile), text });
}

imaging.SaveAsPNG(pageImage, Path.Combine(outDir, "page.png"));
imaging.ReleaseGdPictureImage(pageImage);
File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Results written to {outDir}");
return 0;

record LineSpec(string Name, double X1, double Y1, double X2, double Y2);
