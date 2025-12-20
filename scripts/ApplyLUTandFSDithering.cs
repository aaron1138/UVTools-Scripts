using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.Structure;
using UVtools.Core;
using UVtools.Core.Extensions;
using UVtools.Core.Layers;
using UVtools.Core.Scripting;

namespace UVtools.ScriptSample;

public class ApplyLUTandFSDithering : ScriptGlobals
{
    // --- CONFIGURATION ---
    private readonly ScriptOpenFileDialogInput _lutFile = new()
    {
        Label = "LUT File (Source Map)",
        Filters = new List<ScriptFileDialogInput.ScriptFileDialogFilter>
        {
            new() { Name = "LUT Files", Extensions = new List<string> { "lut", "json", "csv", "txt" } },
            new() { Name = "All Files", Extensions = new List<string> { "*" } }
        },
        ToolTip = "Load the 8-bit LUT (e.g., [0, 100, 100...])."
    };

    private readonly ScriptCheckBoxInput _interpolateLut = new()
    {
        Label = "Interpolate / Smooth LUT",
        Value = true,
        ToolTip = "Enables sub-integer precision for smoother gradients."
    };

    // --- PHYSICS ---
    private readonly ScriptNumericalInput<double> _gamma = new()
    {
        Label = "Device Gamma",
        Value = 3.0,
        Minimum = 0.1,
        Maximum = 5.0,
        Increment = 0.1,
        ToolTip = "Physics of the printer/LCD. Used to calculate Light Energy."
    };

    // --- TARGET ---
    private readonly ScriptCheckBoxInput _enableDithering = new()
    {
        Label = "Enable Dithering",
        Value = true,
        ToolTip = "Enable Floyd-Steinberg Error Diffusion."
    };

    private readonly ScriptNumericalInput<int> _bitDepth = new()
    {
        Label = "Target Bit-depth",
        Value = 3,
        Minimum = 1,
        Maximum = 8,
        ToolTip = "3 bits = 8 Levels (0, 36, 73...)"
    };

    public void ScriptInit()
    {
        Script.Name = "Apply LUT and FS Dithering";
        Script.Description = "One-step processor: Applies interpolated LUT and Floyd-Steinberg Dithering.\n" +
                             "Supports .lut, .json, .csv files.";
        Script.Author = "Jules (AI Agent)";
        Script.Version = new Version(1, 0, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _lutFile,
            _interpolateLut,
            _gamma,
            _enableDithering,
            _bitDepth
        });
    }

    public string? ScriptValidate()
    {
        if (!File.Exists(_lutFile.Value)) return "LUT file not found.";
        return null;
    }

    // -- Tables --
    private float[] _sourceEnergyMap;
    private byte[] _targetPaletteBytes;
    private float[] _targetPaletteEnergy;

    private void PreCalculateTables()
    {
        _sourceEnergyMap = new float[256];
        double g = _gamma.Value;

        // 1. LOAD LUT
        double[] rawLut = new double[256];
        try {
            string text = File.ReadAllText(_lutFile.Value);
            string clean = text.Replace("[", " ").Replace("]", " ").Replace("\"", " ");
            var tokens = clean.Split(new[] { ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var values = new List<double>();
            foreach (var t in tokens)
            {
                 if(double.TryParse(t, out double d)) values.Add(d);
            }
            if (values.Count == 0) throw new Exception("No valid numbers found.");

            for(int i=0; i<256; i++) {
                rawLut[i] = (i < values.Count) ? values[i] : values.Last();
            }
        }
        catch (Exception ex) {
            throw new Exception($"FATAL LUT ERROR: {ex.Message}");
        }

        double lutMax = 0;
        foreach(var val in rawLut) if (val > lutMax) lutMax = val;

        // 3. INTERPOLATE LUT
        double[] processedLut = new double[256];
        if (_interpolateLut.Value)
        {
            for (int i = 0; i < 256; i++)
            {
                double prev = (i > 0) ? rawLut[i - 1] : rawLut[i];
                double curr = rawLut[i];
                double next = (i < 255) ? rawLut[i + 1] : rawLut[i];

                if (prev == 0 && curr > 0) prev = curr;
                if (next == 0 && curr > 0) next = curr;
                if (curr >= lutMax) { processedLut[i] = curr; continue; }
                if (prev == curr && next == curr) { processedLut[i] = curr; continue; }

                processedLut[i] = (prev * 0.25) + (curr * 0.5) + (next * 0.25);
                if (curr == 0) processedLut[i] = 0;
            }
        }
        else
        {
            Array.Copy(rawLut, processedLut, 256);
        }

        // 4. GENERATE SOURCE ENERGY MAP
        for (int i = 0; i < 256; i++)
        {
            double pwm = processedLut[i];
            _sourceEnergyMap[i] = (float)Math.Pow(pwm / 255.0, g);
        }

        // 5. GENERATE TARGET PALETTE
        int levels = 1 << _bitDepth.Value;
        _targetPaletteBytes = new byte[levels];
        _targetPaletteEnergy = new float[levels];

        for (int i = 0; i < levels; i++)
        {
            double val = i * (255.0 / (levels - 1));
            byte pwmByte = (byte)Math.Clamp(Math.Round(val), 0, 255);
            _targetPaletteBytes[i] = pwmByte;
            _targetPaletteEnergy[i] = (float)Math.Pow(pwmByte / 255.0, g);
        }
    }

    public bool ScriptExecute()
    {
        PreCalculateTables();

        bool useFS = _enableDithering.Value;
        int startLayer = (int)Operation.LayerIndexStart;
        int endLayer = (int)Operation.LayerIndexEnd;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = Progress.Token };

        Rectangle roi = Operation.HaveROI ? Operation.ROI : new Rectangle(0, 0, (int)SlicerFile.ResolutionX, (int)SlicerFile.ResolutionY);
        roi.Intersect(new Rectangle(0, 0, (int)SlicerFile.ResolutionX, (int)SlicerFile.ResolutionY));
        if (roi.Width <= 0 || roi.Height <= 0) return true;

        Progress.Reset("Apply LUT & Dither", (uint)(endLayer - startLayer + 1));

        Parallel.For(startLayer, endLayer + 1, options, i =>
        {
            var layer = SlicerFile[i];
            if (layer.IsEmpty) { Progress.LockAndIncrement(); return; }

            var sourceMat = layer.LayerMat;
            using var roiMat = new Mat(sourceMat, roi);

            int width = roiMat.Width;
            int height = roiMat.Height;

            byte[] sourceBytes = new byte[width * height];
            int step = roiMat.Step;
            IntPtr rawDataPtr = roiMat.DataPointer;

            for(int y = 0; y < height; y++)
            {
                Marshal.Copy(rawDataPtr + (y * step), sourceBytes, y * width, width);
            }

            float[] errorBuffer = new float[width * height];
            byte[] outputBytes = new byte[width * height];

            unsafe
            {
                fixed (byte* srcPtr = sourceBytes)
                fixed (byte* outPtr = outputBytes)
                fixed (float* errPtr = errorBuffer)
                fixed (float* targetEnergies = _targetPaletteEnergy)
                fixed (byte* targetBytes = _targetPaletteBytes)
                fixed (float* srcEnergies = _sourceEnergyMap)
                {
                    int levels = _targetPaletteEnergy.Length;

                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * width;
                        int nextRowOffset = (y + 1) * width;
                        bool hasNextRow = y < height - 1;

                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x;

                            byte originalPixel = srcPtr[idx];
                            float energy = srcEnergies[originalPixel];
                            energy += errPtr[idx];

                            int bestIndex = 0;
                            float minDiff = float.MaxValue;

                            for(int k=0; k < levels; k++)
                            {
                                float diff = Math.Abs(energy - targetEnergies[k]);
                                if(diff < minDiff) {
                                    minDiff = diff;
                                    bestIndex = k;
                                }
                            }

                            outPtr[idx] = targetBytes[bestIndex];

                            if (useFS)
                            {
                                float quantError = energy - targetEnergies[bestIndex];
                                if (x < width - 1)
                                    errPtr[idx + 1] += quantError * 0.4375f;

                                if (hasNextRow)
                                {
                                    if (x > 0)
                                        errPtr[nextRowOffset + x - 1] += quantError * 0.1875f;
                                    errPtr[nextRowOffset + x] += quantError * 0.3125f;
                                    if (x < width - 1)
                                        errPtr[nextRowOffset + x + 1] += quantError * 0.0625f;
                                }
                            }
                        }
                    }
                }
            }

            for(int y = 0; y < height; y++)
            {
                Marshal.Copy(outputBytes, y * width, rawDataPtr + (y * step), width);
            }

            layer.LayerMat = sourceMat;
            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
