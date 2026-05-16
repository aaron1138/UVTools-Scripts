/*
 * GNU AFFERO GENERAL PUBLIC LICENSE
 * Version 3, 19 November 2007
 * Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 * Everyone is permitted to copy and distribute verbatim copies
 * of this license document, but changing it is not allowed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing; 
using Emgu.CV;
using Emgu.CV.Structure;
using Emgu.CV.CvEnum;
using UVtools.Core;
using UVtools.Core.Scripting;

namespace UVtools.ScriptSample;

public class ScriptSDFLUTROI : ScriptGlobals
{
    // --- 1. Preservation Settings ---
    private readonly ScriptCheckBoxInput _enablePreserveGray = new() 
    { 
        Label = "Existing Grayscale Preservation (2x SDF)", 
        Value = false,
        ToolTip = "If enabled, intermediate grayscale pixels are COPIED from the source and untouched.\nSDF only operates on explicit White/Black regions defined below."
    };

    private readonly ScriptNumericalInput<int> _preserveWhiteThresh = new() 
    { 
        Label = "Preserve: White Threshold", 
        Value = 255, 
        Minimum = 1, Maximum = 255
    };

    private readonly ScriptNumericalInput<int> _preserveBlackThresh = new() 
    { 
        Label = "Preserve: Black Threshold", 
        Value = 0, 
        Minimum = 0, Maximum = 254
    };

    // --- 2. Toggles & Protection ---
    private readonly ScriptCheckBoxInput _enableInterior = new() { Label = "Enable Interior Fade", Value = true };
    private readonly ScriptCheckBoxInput _enableExterior = new() { Label = "Enable Exterior Fade", Value = false };
    
    // Interior Protection
    private readonly ScriptCheckBoxInput _enableThinWallProtect = new() 
    { 
        Label = "Protect Thin Walls (Interior)", 
        Value = true
    };
    private readonly ScriptNumericalInput<double> _intProtectScale = new()
    {
        Label = "Int. Protect Scale",
        Value = 1.2, Minimum = 0.1, Maximum = 5.0, Increment = 0.1
    };

    // Exterior Protection
    private readonly ScriptCheckBoxInput _enableConcavityProtect = new() 
    { 
        Label = "Protect Concavity (Exterior)", 
        Value = false
    };
    private readonly ScriptNumericalInput<double> _extProtectScale = new()
    {
        Label = "Ext. Protect Scale",
        Value = 1.2, Minimum = 0.1, Maximum = 5.0, Increment = 0.1
    };

    // --- 3. Anisotropy Settings ---
    private readonly ScriptCheckBoxInput _factorAnisotropy = new()
    {
        Label = "Factor Anisotropy",
        Value = true
    };
    private readonly ScriptNumericalInput<int> _xPixelSize = new() { Label = "X Pixel Size (µm)", Value = 19, Minimum = 1, Maximum = 10000 };
    private readonly ScriptNumericalInput<int> _yPixelSize = new() { Label = "Y Pixel Size (µm)", Value = 24, Minimum = 1, Maximum = 10000 };

    // --- 4. Distance Thresholds ---
    private readonly ScriptNumericalInput<double> _intThreshold = new()
    {
        Label = "Interior Distance (px)",
        Value = 5.0, Minimum = 0.1, Maximum = 1000.0, Increment = 0.1
    };

    private readonly ScriptNumericalInput<double> _extThreshold = new()
    {
        Label = "Exterior Distance (px)",
        Value = 3.0, Minimum = 0.1, Maximum = 1000.0, Increment = 0.1
    };

    // --- 5. LUT Inputs ---
    private readonly ScriptTextBoxInput _intLutCsv = new()
    {
        Label = "Interior LUT (0-255)",
        Value = "255, 240, 220, 220, 230, 240"
    };

    private readonly ScriptTextBoxInput _extLutCsv = new()
    {
        Label = "Exterior LUT (0-255)",
        Value = "120, 110, 100, 100"
    };

    private readonly ScriptToggleSwitchInput _lutMode = new()
    {
        Label = "LUT Mode",
        Value = false,
        OnText = "Interpolated",
        OffText = "Absolute"
    };

    private readonly ScriptNumericalInput<int> _threadCount = new()
    {
        Label = "Thread Count",
        Value = Environment.ProcessorCount, Minimum = 1, Maximum = 64
    };

    public void ScriptInit()
    {
        Script.Name = "SDF Generation (ROI + ROI Support)";
        Script.Description = "Anisotropic SDF with LUT mapping.\n" +
                             "Supports Global processing or Region of Interest (ROI). \n" +
							 "Use +1 or +2 LUT elements than selected distance for anisotropy with absolute mode. Distance scales by lower pixel dimension.";
        Script.Author = "Aaron Baca via Gemini";
        Script.Version = new Version(1, 6, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _enablePreserveGray, _preserveWhiteThresh, _preserveBlackThresh,
            _enableInterior, _enableExterior, 
            _enableThinWallProtect, _intProtectScale,
            _enableConcavityProtect, _extProtectScale,
            _factorAnisotropy, _xPixelSize, _yPixelSize,
            _intThreshold, _extThreshold,
            _intLutCsv, _extLutCsv,
            _lutMode, _threadCount
        });
    }

    public string? ScriptValidate()
    {
        try 
        {
            if (_enableInterior.Value) ParseLut(_intLutCsv.Value);
            if (_enableExterior.Value) ParseLut(_extLutCsv.Value);
            if (_enablePreserveGray.Value && _preserveBlackThresh.Value >= _preserveWhiteThresh.Value)
                return "Black Threshold must be lower than White Threshold.";
        }
        catch (Exception ex) { return $"Error parsing LUT: {ex.Message}"; }
        return null;
    }

    private byte[] ParseLut(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<byte>();
        var clean = input.Replace(" ", "");
        var tokens = clean.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Array.Empty<byte>();
        var result = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (byte.TryParse(tokens[i], out byte val)) result[i] = val;
            else result[i] = 128; 
        }
        return result;
    }

    // --- Core Pipeline Logic ---
    // Encapsulated to run on either the full image OR the ROI sub-mat
    private unsafe Mat ProcessImage(Mat sourceImage, 
        byte[] lutInt, byte[] lutExt,
        bool interpolate, bool enabledInt, bool enabledExt,
        bool protectThin, bool protectConcave, bool preserveGray,
        float intDistLimit, float extDistLimit,
        int threshWhite, int threshBlack,
        bool doAnisotropy, double aspectRatio,
        Mat kernelInt, Mat kernelExt)
    {
        Mat resultImage = sourceImage.Clone();

        // --- 1. Create Masks ---
        using Mat maskWhite = new Mat();
        using Mat maskBlack = new Mat();

        if (preserveGray)
        {
            CvInvoke.Threshold(sourceImage, maskWhite, threshWhite - 1, 255, ThresholdType.Binary);
            CvInvoke.Threshold(sourceImage, maskBlack, threshBlack, 255, ThresholdType.BinaryInv);
        }
        else
        {
            CvInvoke.Threshold(sourceImage, maskWhite, 127, 255, ThresholdType.Binary);
            CvInvoke.BitwiseNot(maskWhite, maskBlack);
        }

        // --- 2. Generate Safety Masks ---
        using Mat maskThinInt = new Mat();
        if (protectThin && enabledInt)
        {
            using Mat maskSafe = new Mat();
            CvInvoke.MorphologyEx(maskWhite, maskSafe, MorphOp.Open, kernelInt, new Point(-1, -1), 1, BorderType.Constant, new MCvScalar(0));
            CvInvoke.Subtract(maskWhite, maskSafe, maskThinInt);
        }

        using Mat maskThinExt = new Mat();
        if (protectConcave && enabledExt)
        {
            using Mat maskSafe = new Mat();
            CvInvoke.MorphologyEx(maskBlack, maskSafe, MorphOp.Open, kernelExt, new Point(-1, -1), 1, BorderType.Constant, new MCvScalar(0));
            CvInvoke.Subtract(maskBlack, maskSafe, maskThinExt);
        }

        // --- 3. Interior Pipeline ---
        if (enabledInt)
        {
            using Mat inputInt = new Mat();
            if (doAnisotropy)
            {
                var scaledSize = new System.Drawing.Size((int)(maskWhite.Width * aspectRatio), maskWhite.Height);
                CvInvoke.Resize(maskWhite, inputInt, scaledSize, 0, 0, Inter.Nearest);
            }
            else maskWhite.CopyTo(inputInt);

            using Mat distInt = new Mat();
            CvInvoke.DistanceTransform(inputInt, distInt, null, DistType.L2, 5);

            using Mat distIntResized = new Mat();
            if (doAnisotropy) CvInvoke.Resize(distInt, distIntResized, maskWhite.Size, 0, 0, Inter.Linear);
            else distInt.CopyTo(distIntResized);

            byte* resPtr = (byte*)resultImage.DataPointer;
            byte* whitePtr = (byte*)maskWhite.DataPointer;
            byte* thinPtr = (protectThin) ? (byte*)maskThinInt.DataPointer : null;
            float* distPtr = (float*)distIntResized.DataPointer;
            int len = resultImage.Width * resultImage.Height;

            for(int p=0; p<len; p++)
            {
                if (whitePtr[p] > 0)
                {
                    if (thinPtr != null && thinPtr[p] > 0) resPtr[p] = 255;
                    else
                    {
                        float d = distPtr[p];
                        if (d >= intDistLimit) resPtr[p] = 255;
                        else resPtr[p] = GetLutValue(d, intDistLimit, lutInt, interpolate);
                    }
                }
            }
        }

        // --- 4. Exterior Pipeline ---
        if (enabledExt)
        {
            using Mat inputExt = new Mat();
            if (doAnisotropy)
            {
                var scaledSize = new System.Drawing.Size((int)(maskBlack.Width * aspectRatio), maskBlack.Height);
                CvInvoke.Resize(maskBlack, inputExt, scaledSize, 0, 0, Inter.Nearest);
            }
            else maskBlack.CopyTo(inputExt);

            using Mat distExt = new Mat();
            CvInvoke.DistanceTransform(inputExt, distExt, null, DistType.L2, 5);

            using Mat distExtResized = new Mat();
            if (doAnisotropy) CvInvoke.Resize(distExt, distExtResized, maskBlack.Size, 0, 0, Inter.Linear);
            else distExt.CopyTo(distExtResized);

            byte* resPtr = (byte*)resultImage.DataPointer;
            byte* blackPtr = (byte*)maskBlack.DataPointer;
            byte* thinPtr = (protectConcave) ? (byte*)maskThinExt.DataPointer : null;
            float* distPtr = (float*)distExtResized.DataPointer;
            int len = resultImage.Width * resultImage.Height;

            for(int p=0; p<len; p++)
            {
                if (blackPtr[p] > 0)
                {
                    if (thinPtr != null && thinPtr[p] > 0) resPtr[p] = 0;
                    else
                    {
                        float d = distPtr[p];
                        if (d >= extDistLimit) resPtr[p] = 0;
                        else resPtr[p] = GetLutValue(d, extDistLimit, lutExt, interpolate);
                    }
                }
            }
        }

        return resultImage;
    }

    public bool ScriptExecute()
    {
        Progress.Reset("Generating SDF...", Operation.LayerRangeCount);

        // Pre-parse settings
        byte[] lutInt = _enableInterior.Value ? ParseLut(_intLutCsv.Value) : Array.Empty<byte>();
        byte[] lutExt = _enableExterior.Value ? ParseLut(_extLutCsv.Value) : Array.Empty<byte>();
        
        bool interpolate = _lutMode.Value; 
        bool enabledInt = _enableInterior.Value;
        bool enabledExt = _enableExterior.Value;
        
        bool protectThin = _enableThinWallProtect.Value;
        bool protectConcave = _enableConcavityProtect.Value;
        bool preserveGray = _enablePreserveGray.Value;
        
        float intDistLimit = (float)_intThreshold.Value;
        float extDistLimit = (float)_extThreshold.Value;
        int threshWhite = _preserveWhiteThresh.Value;
        int threshBlack = _preserveBlackThresh.Value;

        bool doAnisotropy = _factorAnisotropy.Value && _xPixelSize.Value != _yPixelSize.Value;
        double aspectRatio = doAnisotropy ? (double)_xPixelSize.Value / (double)_yPixelSize.Value : 1.0;

        // Kernels
        int intRad = (int)Math.Ceiling(intDistLimit * _intProtectScale.Value);
        int intDim = intRad * 2 + 1;
        using Mat kernelInt = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(intDim, intDim), new Point(-1, -1));

        int extRad = (int)Math.Ceiling(extDistLimit * _extProtectScale.Value);
        int extDim = extRad * 2 + 1;
        using Mat kernelExt = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(extDim, extDim), new Point(-1, -1));

        // ROI State
        bool haveRoi = Operation.HaveROI;
        Rectangle opRoi = Operation.ROI;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _threadCount.Value };
        
        Parallel.For((int)Operation.LayerIndexStart, (int)Operation.LayerIndexEnd + 1, parallelOptions, i =>
        {
            Progress.PauseOrCancelIfRequested();
            var layer = SlicerFile[i];
            if (layer.IsEmpty) { Progress.LockAndIncrement(); return; }

            // Decide Processing Target
            Mat finalMat;

            if (haveRoi)
            {
                // ROI LOGIC
                finalMat = layer.LayerMat.Clone();
                Rectangle validRoi = Rectangle.Intersect(new Rectangle(Point.Empty, finalMat.Size), opRoi);
                
                if (!validRoi.IsEmpty)
                {
                    // Extract ROI Sub-Matrix
                    using (Mat roiSrc = new Mat(layer.LayerMat, validRoi)) // Read from original
                    {
                        // Process the Sub-Matrix
                        using (Mat roiProcessed = ProcessImage(roiSrc, lutInt, lutExt, interpolate, enabledInt, enabledExt,
                                                               protectThin, protectConcave, preserveGray, intDistLimit, extDistLimit,
                                                               threshWhite, threshBlack, doAnisotropy, aspectRatio, kernelInt, kernelExt))
                        {
                            // Paste result back into the clone at the ROI position
                            using (Mat roiDest = new Mat(finalMat, validRoi))
                            {
                                roiProcessed.CopyTo(roiDest);
                            }
                        }
                    }
                }
            }
            else
            {
                // GLOBAL LOGIC
                finalMat = ProcessImage(layer.LayerMat, lutInt, lutExt, interpolate, enabledInt, enabledExt,
                                        protectThin, protectConcave, preserveGray, intDistLimit, extDistLimit,
                                        threshWhite, threshBlack, doAnisotropy, aspectRatio, kernelInt, kernelExt);
            }

            layer.LayerMat = finalMat;
            Progress.LockAndIncrement();
        });
        
        kernelInt.Dispose();
        kernelExt.Dispose();
        return !Progress.Token.IsCancellationRequested;
    }

    private byte GetLutValue(float distance, float limit, byte[] lut, bool interpolate)
    {
        if (lut.Length == 0) return 255;

        if (interpolate)
        {
            float t = distance / limit; 
            float indexFloat = t * (lut.Length - 1);
            int idx = (int)indexFloat;
            float frac = indexFloat - idx;

            if (idx >= lut.Length - 1) return lut[lut.Length - 1];
            if (idx < 0) return lut[0];

            float val = lut[idx] * (1.0f - frac) + lut[idx + 1] * frac;
            return (byte)val;
        }
        else // Absolute
        {
            int idx = (int)distance;
            if (idx >= lut.Length) return lut[lut.Length - 1]; 
            if (idx < 0) return lut[0];
            return lut[idx];
        }
    }
}