/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.Structure;
using UVtools.Core;
using UVtools.Core.Scripting;

namespace UVtools.ScriptSample;

/// <summary>
/// Generates a Signed Distance Field (SDF) from the layers with advanced options.
/// </summary>
public class ScriptSDFEnhanced : ScriptGlobals
{
    private readonly ScriptCheckBoxInput _factorAnisotropy = new()
    {
        Label = "Factor Anisotropy",
        Value = true,
        ToolTip = "Enable to account for non-square pixels based on the dimensions below."
    };

    private readonly ScriptNumericalInput<int> _xPixelSize = new()
    {
        Label = "X Pixel Size (µm)",
        Value = 50,
        Minimum = 1,
        Maximum = 10000,
        ToolTip = "The width of a pixel in micrometers."
    };

    private readonly ScriptNumericalInput<int> _yPixelSize = new()
    {
        Label = "Y Pixel Size (µm)",
        Value = 50,
        Minimum = 1,
        Maximum = 10000,
        ToolTip = "The height of a pixel in micrometers."
    };

    private readonly ScriptNumericalInput<double> _lowerThreshold = new()
    {
        Label = "Lower Threshold",
        Value = -1.0,
        Minimum = -100.0,
        Maximum = 100.0,
        Increment = 0.1,
        ToolTip = "The distance outside the model to begin the grayscale gradient. Negative values are outside the model."
    };

    private readonly ScriptNumericalInput<double> _upperThreshold = new()
    {
        Label = "Upper Threshold",
        Value = 1.0,
        Minimum = -100.0,
        Maximum = 100.0,
        Increment = 0.1,
        ToolTip = "The distance inside the model to end the grayscale gradient. Positive values are inside the model."
    };

    private readonly ScriptNumericalInput<double> _gamma = new()
    {
        Label = "Gamma",
        Value = 1.0,
        Minimum = 0.1,
        Maximum = 5.0,
        Increment = 0.1,
        ToolTip = "The gamma correction to apply to the grayscale mapping."
    };
    private readonly ScriptNumericalInput<int> _threadCount = new()
    {
        Label = "Thread Count",
        Value = Environment.ProcessorCount,
        Minimum = 1,
        Maximum = Environment.ProcessorCount,
        ToolTip = "The number of threads to use for parallel processing."
    };

    /// <summary>
    /// Set configurations here, this function trigger just after load a script
    /// </summary>
    public void ScriptInit()
    {
        Script.Name = "SDF Generation (Enhanced)";
        Script.Description = "Generates a Signed Distance Field (SDF) from the layers with advanced options for anisotropic pixels and grayscale mapping.";
        Script.Author = "Aaron Baca via Jules (AI Agent)";
        Script.Version = new Version(1, 0, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _factorAnisotropy,
            _xPixelSize,
            _yPixelSize,
            _lowerThreshold,
            _upperThreshold,
            _gamma,
            _threadCount
        });
    }

    /// <summary>
    /// Validate user inputs here, this function trigger when user click on execute
    /// </summary>
    /// <returns>A error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        if (_lowerThreshold.Value >= _upperThreshold.Value)
        {
            return "Lower Threshold must be less than Upper Threshold.";
        }
        return null;
    }

    /// <summary>
    /// Execute the script, this function trigger when when user click on execute and validation passes
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public unsafe bool ScriptExecute()
    {
        Progress.Reset("Generating Enhanced SDF", Operation.LayerRangeCount);

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _threadCount.Value };
        Parallel.For((int)Operation.LayerIndexStart, (int)Operation.LayerIndexEnd + 1, parallelOptions, i =>
        {
            Progress.PauseOrCancelIfRequested();

            var layer = SlicerFile[i];
            if (layer.IsEmpty)
            {
                Progress.LockAndIncrement();
                return;
            }

            using Mat originalImage = layer.LayerMat;
            using Mat binaryImage = new Mat();
            CvInvoke.Threshold(originalImage, binaryImage, 127, 255, Emgu.CV.CvEnum.ThresholdType.Binary);

            // Anisotropic Scaling
            using Mat scaledImage = new Mat();
            if (_factorAnisotropy.Value && _xPixelSize.Value != _yPixelSize.Value)
            {
                // Scale width by X/Y to match Y resolution (density)
                // If X=19, Y=24 (X is narrower/denser): We shrink width by 19/24 to match Y density.
                var aspectRatio = (double)_xPixelSize.Value / (double)_yPixelSize.Value;
                var scaledSize = new System.Drawing.Size((int)(binaryImage.Width * aspectRatio), binaryImage.Height);
                CvInvoke.Resize(binaryImage, scaledImage, scaledSize, 0, 0, Emgu.CV.CvEnum.Inter.Nearest);
            }
            else
            {
                binaryImage.CopyTo(scaledImage);
            }

            // Signed Distance Field Calculation
            using Mat distInside = new Mat();
            using Mat distOutside = new Mat();
            using Mat invertedImage = new Mat();
            CvInvoke.BitwiseNot(scaledImage, invertedImage);
            CvInvoke.DistanceTransform(scaledImage, distOutside, null, Emgu.CV.CvEnum.DistType.L2, 5);
            CvInvoke.DistanceTransform(invertedImage, distInside, null, Emgu.CV.CvEnum.DistType.L2, 5);

            using Mat sdf = new Mat();
            CvInvoke.Subtract(distOutside, distInside, sdf);

            // Resize back
            using Mat sdfResized = new Mat();
            if (_factorAnisotropy.Value && _xPixelSize.Value != _yPixelSize.Value)
            {
                CvInvoke.Resize(sdf, sdfResized, binaryImage.Size, 0, 0, Emgu.CV.CvEnum.Inter.Linear);
            }
            else
            {
                sdf.CopyTo(sdfResized);
            }

            // Thresholding and Mapping
            Mat resultImage = new Mat(sdfResized.Size, Emgu.CV.CvEnum.DepthType.Cv8U, 1);
            float* sdfPtr = (float*)sdfResized.DataPointer;
            byte* resultPtr = (byte*)resultImage.DataPointer;
            int totalPixels = resultImage.Width * resultImage.Height;
            float lower = (float)_lowerThreshold.Value;
            float upper = (float)_upperThreshold.Value;
            float range = upper - lower;
            float invGamma = 1.0f / (float)_gamma.Value;

            for (int p = 0; p < totalPixels; p++)
            {
                float sdfValue = sdfPtr[p];
                if (sdfValue < lower)
                {
                    resultPtr[p] = 0; // Black
                }
                else if (sdfValue > upper)
                {
                    resultPtr[p] = 255; // White
                }
                else
                {
                    float normalized = (sdfValue - lower) / range;
                    float corrected = (float)Math.Pow(normalized, invGamma);
                    resultPtr[p] = (byte)(1 + corrected * 253); // Map to 1-254
                }
            }

            layer.LayerMat = resultImage.Clone();
            resultImage.Dispose();
            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
