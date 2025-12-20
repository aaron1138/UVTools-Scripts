using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using UVtools.Core;
using UVtools.Core.FileFormats;
using UVtools.Core.Layers;
using UVtools.Core.Scripting;

namespace UVtools.Core.Scripting;

public class NanoDLPMultiExposureExport : ScriptGlobals
{
    readonly ScriptSaveFileDialogInput _outputFile = new()
    {
        Label = "Output File (.nanodlp)",
        ToolTip = "Save as .nanodlp (zip) file.",
        DefaultExtension = "nanodlp",
        Filters = new List<ScriptFileDialogInput.ScriptFileDialogFilter> { new() { Name = "NanoDLP", Extensions = new List<string> { "nanodlp" } } }
    };

    readonly ScriptNumericalInput<int> _divisorInput = new()
    {
        Label = "Divisor (Sub-layers)",
        ToolTip = "Number of sub-layers per logical layer.",
        Value = 6,
        Minimum = 1,
        Maximum = 255
    };

    readonly ScriptTextBoxInput _exposureTimesInput = new()
    {
        Label = "Exposure Times (s)",
        ToolTip = "Comma-separated list of exposure times for each sub-layer (e.g., '1.5,0.5,0.5'). Must match divisor count.",
        Value = ""
    };

    readonly ScriptCheckBoxInput _packedRGB = new()
    {
        Label = "Pack into RGB (3:1)",
        ToolTip = "Enable to pack 3 horizontal pixels into 1 RGB pixel (for high-res screens). Resolution width must be divisible by 3.",
        Value = false
    };

    public void ScriptInit()
    {
        Script.Name = "NanoDLP Multi-Exposure Export";
        Script.Description = "Exports current layers to NanoDLP format (Zip) with multi-exposure support.\n" +
                             "Uses multi-threaded batch processing for image compression.";
        Script.Version = new Version(1, 2);
        Script.Author = "Aaron Baca via Jules (AI Agent)";
        Script.MinimumVersionToRun = new Version(5, 0, 0);
        Script.UserInputs.Add(_outputFile);
        Script.UserInputs.Add(_divisorInput);
        Script.UserInputs.Add(_exposureTimesInput);
        Script.UserInputs.Add(_packedRGB);
    }

    public string? ScriptValidate()
    {
        if (string.IsNullOrWhiteSpace(_outputFile.Value))
            return "Please select a valid output file.";

        if (_packedRGB.Value && SlicerFile.ResolutionX % 3 != 0)
            return "Resolution width must be divisible by 3 for RGB packing.";

        return null;
    }

    public bool ScriptExecute()
    {
        string zipPath = _outputFile.Value!;
        int divisor = _divisorInput.Value;
        var layers = SlicerFile.Layers;

        List<float> customCureTimes = new();
        if (!string.IsNullOrWhiteSpace(_exposureTimesInput.Value))
        {
            var parts = _exposureTimesInput.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (float.TryParse(p, out var v)) customCureTimes.Add(v);
            }
        }

        Progress.Reset("Exporting layers", (uint)layers.Length);

        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var plateNode = new JsonObject
        {
            ["PlateID"] = 1,
            ["CreatedDate"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Updated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["LayersCount"] = layers.Length / divisor,
            ["MC"] = new JsonObject
            {
                ["Count"] = divisor,
                ["X"] = new JsonArray(),
                ["Y"] = new JsonArray()
            },
            ["CureTimes"] = new JsonArray()
        };

        var cureTimesArray = plateNode["CureTimes"]!.AsArray();
        if (customCureTimes.Count > 0)
        {
            for (int i = 0; i < divisor; i++)
            {
                cureTimesArray.Add(i < customCureTimes.Count ? customCureTimes[i] : customCureTimes.Last());
            }
        }
        else
        {
            for(int i=0; i<divisor && i<layers.Length; i++)
            {
                cureTimesArray.Add(layers[i].ExposureTime);
            }
        }

        var xArray = plateNode["MC"]!["X"]!.AsArray();
        var yArray = plateNode["MC"]!["Y"]!.AsArray();
        for(int i=0; i<divisor; i++) { xArray.Add(0); yArray.Add(0); }

        {
            var entry = zip.CreateEntry("plate.json");
            using var stream = entry.Open();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            plateNode.WriteTo(writer);
        }

        int batchSize = FileFormat.DefaultParallelBatchCount;
        for (int i = 0; i < layers.Length; i += batchSize)
        {
            if (Progress.Token.IsCancellationRequested) break;

            int count = Math.Min(batchSize, layers.Length - i);
            var batchIndices = Enumerable.Range(i, count).ToList();
            var batchResults = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();

            // Process parallel
            Parallel.ForEach(batchIndices, CoreSettings.GetParallelOptions(Progress), idx =>
            {
                var layer = layers[idx];
                var mat = layer.LayerMat;
                Mat? toDispose = null;
                Mat toSave = mat;

                if (_packedRGB.Value)
                {
                    var clone = mat.Clone();
                    var reshaped = clone.Reshape(3);
                    CvInvoke.CvtColor(reshaped, reshaped, ColorConversion.Bgr2Rgb);
                    clone.Dispose();
                    toSave = reshaped;
                    toDispose = reshaped;
                }

                using var buf = new VectorOfByte();
                CvInvoke.Imencode(".png", toSave, buf);
                batchResults[idx] = buf.ToArray();

                toDispose?.Dispose();
                Progress.LockAndIncrement();
            });

            // Write sequential
            foreach (var idx in batchIndices)
            {
                if (!batchResults.TryGetValue(idx, out var data)) continue;

                int logical = (idx / divisor) + 1;
                int sub = idx % divisor;
                string filename = (sub == 0) ? $"{logical}.png" : $"{logical}-{sub}.png";

                var entry = zip.CreateEntry(filename);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return !Progress.Token.IsCancellationRequested;
    }
}
