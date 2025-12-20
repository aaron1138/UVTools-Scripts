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

public class ScriptLutInterpolationDithering : ScriptGlobals
{
    // --- CONFIGURATION ---
    private readonly ScriptOpenFileDialogInput _lutFile = new() 
    { 
        Label = "LUT File (Source Map)", 
        Filters = new List<ScriptFileDialogInput.ScriptFileDialogFilter> 
        {
            new() { Name = "LUT Files", Extensions = new List<string> { "txt", "csv", "json", "lut" } },
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
    private readonly ScriptNumericalInput<int> _bitDepth = new() 
    { 
        Label = "Target Bit-depth", 
        Value = 3, 
        Minimum = 1, 
        Maximum = 8, 
        ToolTip = "3 bits = 8 Levels (0, 36, 73...)" 
    };

    private readonly ScriptCheckBoxInput _noFloydSteinberg = new() 
    { 
        Label = "Disable Error Diffusion", 
        Value = false, 
        ToolTip = "Uncheck for smooth dithering." 
    };

    public void ScriptInit()
    {
        Script.Name = "Super-Res LUT Dithering (Fixed)";
        Script.Description = "Applies 8-bit LUT to Linear Source with Float Interpolation.\n" +
                             "Dithers to Fixed Hardware Palette (0-255).\n" +
                             "Fixes erosion by allowing dither between palette steps (e.g. 73/109).\n" +
                             "Protects 0-Floor and Max-Ceiling from smoothing artifacts.";
        Script.Author = "Jules (AI Agent)";
        Script.Version = new Version(2, 1, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _lutFile,
            _interpolateLut,
            _gamma,
            _bitDepth,
            _noFloydSteinberg
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
        
        // 1. LOAD LUT (STRICT MODE)
        // If this fails, we throw an Exception to halt the script completely.
        double[] rawLut = new double[256];
        
        try {
            if (string.IsNullOrWhiteSpace(_lutFile.Value) || !File.Exists(_lutFile.Value))
                throw new FileNotFoundException("LUT file path is invalid or file does not exist.");

            string rawText = File.ReadAllText(_lutFile.Value);
            // Sanitize brackets/quotes
            string cleanText = rawText.Replace("[", " ").Replace("]", " ").Replace("\"", " ");
            
            // Robust parsing: ignore empty entries caused by trailing commas
            var numbers = new List<double>();
            var tokens = cleanText.Split(new[] { ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var token in tokens)
            {
                if (double.TryParse(token, out double val)) numbers.Add(val);
            }

            if (numbers.Count == 0)
                throw new Exception("File parsed successfully but contained no valid numeric data.");

            for(int i=0; i<256; i++) {
                rawLut[i] = (i < numbers.Count) ? numbers[i] : numbers.Last();
            }
        }
        catch (Exception ex) { 
            // HARD FAIL: Throw exception to stop script execution completely.
            // Do NOT fall back to linear.
            throw new Exception($"FATAL LUT ERROR: {ex.Message}");
        }

        // 2. DETECT LUT CEILING (For Ceiling Lock Only)
        // We DO NOT change the hardware floor based on LUT. Hardware is fixed.
        double lutMax = 0;
        foreach(var val in rawLut) if (val > lutMax) lutMax = val;
        
        // 3. INTERPOLATE LUT (Smart Smoothing)
        double[] processedLut = new double[256];
        if (_interpolateLut.Value)
        {
            for (int i = 0; i < 256; i++)
            {
                double prev = (i > 0) ? rawLut[i - 1] : rawLut[i];
                double curr = rawLut[i];
                double next = (i < 255) ? rawLut[i + 1] : rawLut[i];
                
                // --- PROTECTION LOGIC ---
                
                // A. FLOOR LOCK: If I am valid (100) but prev is 0, ignore prev.
                // This keeps the "Step" sharp at the bottom.
                if (prev == 0 && curr > 0) prev = curr;
                if (next == 0 && curr > 0) next = curr;

                // B. CEILING LOCK: If I am at the LUT's max, stay there.
                // Prevents 255 -> 254.75 which causes white dithering.
                if (curr >= lutMax) {
                    processedLut[i] = curr; 
                    continue; 
                }
                
                // C. FLAT AREA LOCK: If neighbors match, no smoothing needed.
                if (prev == curr && next == curr) {
                    processedLut[i] = curr;
                    continue;
                }

                // Smooth
                processedLut[i] = (prev * 0.25) + (curr * 0.5) + (next * 0.25);
                
                // Hard 0 Safety
                if (curr == 0) processedLut[i] = 0;
            }
        }
        else 
        {
            Array.Copy(rawLut, processedLut, 256);
        }

        // 4. GENERATE SOURCE ENERGY MAP
        // Map: Input(Linear) -> LUT(PWM) -> Energy
        for (int i = 0; i < 256; i++)
        {
            double pwm = processedLut[i];
            _sourceEnergyMap[i] = (float)Math.Pow(pwm / 255.0, g);
        }

        // 5. GENERATE TARGET PALETTE (Fixed Hardware)
        // Palette is ALWAYS 0 to 255 distributed by bit-depth.
        int levels = 1 << _bitDepth.Value;
        _targetPaletteBytes = new byte[levels];
        _targetPaletteEnergy = new float[levels];
        
        for (int i = 0; i < levels; i++)
        {
            // Linear distribution 0..255
            double val = i * (255.0 / (levels - 1));
            byte pwmByte = (byte)Math.Clamp(Math.Round(val), 0, 255);
            
            _targetPaletteBytes[i] = pwmByte;
            // Gamma corrected ENERGY of that hardware state
            _targetPaletteEnergy[i] = (float)Math.Pow(pwmByte / 255.0, g);
        }
    }

    public bool ScriptExecute()
    {
        PreCalculateTables(); // This will now throw if LUT fails
        
        bool useFS = !_noFloydSteinberg.Value;
        int startLayer = (int)Operation.LayerIndexStart;
        int endLayer = (int)Operation.LayerIndexEnd;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = Progress.Token };
        
        Rectangle roi = Operation.HaveROI ? Operation.ROI : new Rectangle(0, 0, (int)SlicerFile.ResolutionX, (int)SlicerFile.ResolutionY);
        roi.Intersect(new Rectangle(0, 0, (int)SlicerFile.ResolutionX, (int)SlicerFile.ResolutionY));
        if (roi.Width <= 0 || roi.Height <= 0) return true;

        Progress.Reset("Super-Res Dither", (uint)(endLayer - startLayer + 1));

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

            // Row-by-Row Copy
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

                            // A. Get Energy from Map
                            byte originalPixel = srcPtr[idx];
                            float energy = srcEnergies[originalPixel];

                            // B. Add Error
                            energy += errPtr[idx];

                            // C. Find closest hardware state
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

                            // D. Set Output
                            outPtr[idx] = targetBytes[bestIndex];

                            // E. Distribute Error
                            if (useFS)
                            {
                                float quantError = energy - targetEnergies[bestIndex];
                                
                                // Standard Floyd-Steinberg
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

            // Write Back
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