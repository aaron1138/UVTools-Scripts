using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using UVtools.Core;
using UVtools.Core.FileFormats;
using UVtools.Core.Layers;
using UVtools.Core.Scripting;

namespace UVtools.Core.Scripting;

public class NanoDLPMultiExposureImport : ScriptGlobals
{
    readonly ScriptOpenFileDialogInput _inputFile = new()
    {
        Label = "NanoDLP File (.nanodlp)",
        ToolTip = "Select the .nanodlp (zip) file.",
        Filters = new List<ScriptFileDialogInput.ScriptFileDialogFilter> { new() { Name = "NanoDLP", Extensions = new List<string> { "nanodlp", "zip" } } }
    };

    readonly ScriptOpenFolderDialogInput _inputFolder = new()
    {
        Label = "NanoDLP Folder (Unzipped)",
        ToolTip = "Select the folder containing unzipped files. If set, overrides File input.",
        Value = ""
    };

    readonly ScriptCheckBoxInput _packedRGB = new()
    {
        Label = "Packed RGB (3:1)",
        ToolTip = "Enable if the images are RGB packed (e.g. 12K mono screens).",
        Value = false
    };

    public void ScriptInit()
    {
        Script.Name = "NanoDLP Multi-Exposure Import";
        Script.Description = "Imports NanoDLP files (Zip or Folder) with multi-exposure per layer.\n" +
                             "Supports 'Packed RGB' for high-res screens and batch multi-threading for performance.";
        Script.Version = new Version(1, 2);
        Script.Author = "Aaron Baca via Jules (AI Agent)";
        Script.MinimumVersionToRun = new Version(5, 0, 0);
        Script.UserInputs.Add(_inputFile);
        Script.UserInputs.Add(_inputFolder);
        Script.UserInputs.Add(_packedRGB);
    }

    public string? ScriptValidate()
    {
        bool hasFolder = !string.IsNullOrWhiteSpace(_inputFolder.Value) && Directory.Exists(_inputFolder.Value);
        bool hasFile = !string.IsNullOrWhiteSpace(_inputFile.Value) && File.Exists(_inputFile.Value);

        if (!hasFolder && !hasFile)
            return "Please select a valid folder OR a .nanodlp file.";
        return null;
    }

    public bool ScriptExecute()
    {
        string? folderPath = null;
        ZipArchive? zip = null;
        List<string> fileNames = new();
        Func<string, Stream?>? openStream = null;

        if (!string.IsNullOrWhiteSpace(_inputFolder.Value) && Directory.Exists(_inputFolder.Value))
        {
            folderPath = _inputFolder.Value;
            fileNames = Directory.GetFiles(folderPath).Select(Path.GetFileName).ToList()!;
            openStream = (name) => File.OpenRead(Path.Combine(folderPath, name));
        }
        else if (!string.IsNullOrWhiteSpace(_inputFile.Value) && File.Exists(_inputFile.Value))
        {
            zip = ZipFile.OpenRead(_inputFile.Value);
            fileNames = zip.Entries.Select(e => e.Name).ToList();
            openStream = (name) => zip.GetEntry(name)?.Open();
        }

        if (openStream == null) return false;

        JsonNode? root = null;
        if (fileNames.Contains("plate.json"))
        {
            try
            {
                using var stream = openStream("plate.json");
                if (stream != null)
                    root = JsonNode.Parse(stream);
            }
            catch { /* Ignore */ }
        }

        var cureTimesNode = root?["CureTimes"]?.AsArray();
        List<float> cureTimes = new();
        if (cureTimesNode is not null)
        {
            foreach (var node in cureTimesNode)
                cureTimes.Add(node?.GetValue<float>() ?? 0f);
        }

        var layerFiles = new List<(int Main, int Sub, string Name)>();
        var regex = new Regex(@"^(\d+)(?:-(\d+))?\.png$");

        foreach (var name in fileNames)
        {
            if (name.Equals("3d.png", StringComparison.OrdinalIgnoreCase)) continue;
            var match = regex.Match(name);
            if (match.Success)
            {
                int main = int.Parse(match.Groups[1].Value);
                int sub = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
                layerFiles.Add((main, sub, name));
            }
        }

        layerFiles.Sort((a, b) =>
        {
            int ret = a.Main.CompareTo(b.Main);
            if (ret != 0) return ret;
            return a.Sub.CompareTo(b.Sub);
        });

        if (layerFiles.Count == 0)
        {
            zip?.Dispose();
            return false;
        }

        // Determine resolution
        {
            using var stream = openStream(layerFiles[0].Name);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                using var tmp = new Mat();
                CvInvoke.Imdecode(bytes, ImreadModes.Unchanged, tmp);

                if (_packedRGB.Value)
                    SlicerFile.Resolution = new System.Drawing.Size(tmp.Width * 3, tmp.Height);
                else
                    SlicerFile.Resolution = tmp.Size;
            }
        }

        var newLayers = new Layer[layerFiles.Count];
        Progress.Reset("Importing layers", (uint)layerFiles.Count);

        int batchSize = FileFormat.DefaultParallelBatchCount;

        for (int i = 0; i < layerFiles.Count; i += batchSize)
        {
            if (Progress.Token.IsCancellationRequested) break;

            int count = Math.Min(batchSize, layerFiles.Count - i);
            var batchFiles = layerFiles.GetRange(i, count);
            var batchData = new List<(int Index, byte[] Data, int Sub)>();

            // Read batch sequentially
            foreach (var lf in batchFiles)
            {
                using var stream = openStream(lf.Name);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                // Note: Index in newLayers is i + relative index
                batchData.Add((layerFiles.IndexOf(lf), ms.ToArray(), lf.Sub));
            }

            // Process batch parallel
            Parallel.ForEach(batchData, CoreSettings.GetParallelOptions(Progress), item =>
            {
                Mat mat = new Mat();
                // Note: mat is passed to Layer, which takes ownership or compresses it. Do not dispose the final mat here.
                CvInvoke.Imdecode(item.Data, _packedRGB.Value ? ImreadModes.ColorBgr : ImreadModes.Grayscale, mat);

                if (_packedRGB.Value)
                {
                    CvInvoke.CvtColor(mat, mat, ColorConversion.Bgr2Rgb);
                    var unpacked = mat.Reshape(1);
                    mat.Dispose(); // Dispose the intermediate packed mat
                    mat = unpacked;
                }

                var layer = new Layer((uint)item.Index, mat, SlicerFile);

                if (cureTimes.Count > 0)
                {
                    int cureIndex = item.Sub;
                    if (cureIndex < cureTimes.Count)
                        layer.ExposureTime = cureTimes[cureIndex];
                }

                newLayers[item.Index] = layer;
                Progress.LockAndIncrement();
            });
        }

        zip?.Dispose();

        SlicerFile.Layers = newLayers;
        SlicerFile.CalculateLayersHash();
        SlicerFile.RebuildLayersProperties();

        return !Progress.Token.IsCancellationRequested;
    }
}
