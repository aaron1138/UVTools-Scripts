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
/// Applies an Enhanced EDT blending to the layers to reduce Z-axis aliasing.
/// </summary>
public class ScriptEnhancedEDT : ScriptGlobals
{
    private readonly ScriptNumericalInput<int> _fadeDistance = new()
    {
        Label = "Fade Distance",
        Unit = "pixels",
        Minimum = 1,
        Maximum = 1000,
        Increment = 1,
        Value = 20,
        ToolTip = "The maximum distance over which the blending effect is applied."
    };

    private readonly ScriptCheckBoxInput _anisotropicCorrection = new()
    {
        Label = "Anisotropic Correction",
        Value = false,
        ToolTip = "Enables anisotropic correction to account for different X and Y pixel dimensions."
    };

    private readonly ScriptNumericalInput<double> _xFactor = new()
    {
        Label = "X Factor",
        Minimum = 0.1,
        Maximum = 10.0,
        Increment = 0.1,
        Value = 1.0,
        ToolTip = "The scaling factor for the X-axis."
    };

    private readonly ScriptNumericalInput<double> _yFactor = new()
    {
        Label = "Y Factor",
        Minimum = 0.1,
        Maximum = 10.0,
        Increment = 0.1,
        Value = 1.0,
        ToolTip = "The scaling factor for the Y-axis."
    };

    private readonly ScriptNumericalInput<int> _recedingLayers = new()
    {
        Label = "Receding Layers",
        Minimum = 1,
        Maximum = 50,
        Increment = 1,
        Value = 5,
        ToolTip = "The number of previous layers to consider for the blending effect."
    };

    /// <summary>
    /// Set configurations here, this function trigger just after load a script
    /// </summary>
    public void ScriptInit()
    {
        Script.Name = "Enhanced EDT Blending";
        Script.Description = "Applies an Enhanced EDT blending to the layers to reduce Z-axis aliasing.";
        Script.Author = "Jules (AI Agent)";
        Script.Version = new Version(0, 1);
        Script.MinimumVersionToRun = new Version(5, 2, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _fadeDistance,
            _recedingLayers,
            _anisotropicCorrection,
            _xFactor,
            _yFactor
        });
    }

    /// <summary>
    /// Validate user inputs here, this function trigger when user click on execute
    /// </summary>
    /// <returns>A error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        if (_anisotropicCorrection.Value && (_xFactor.Value <= 0 || _yFactor.Value <= 0))
        {
            return "Anisotropic correction factors must be positive.";
        }
        return null;
    }

    /// <summary>
    /// Execute the script, this function trigger when when user click on execute and validation passes
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public bool ScriptExecute()
    {
        Progress.Reset("Enhanced EDT Blending", Operation.LayerRangeCount);

        var priorBinaryMasks = new System.Collections.Generic.Queue<Mat>();

        for (int i = (int)Operation.LayerIndexStart; i <= (int)Operation.LayerIndexEnd; i++)
        {
            Progress.PauseOrCancelIfRequested();

            var currentLayer = SlicerFile[i];
            if (currentLayer.IsEmpty)
            {
                Progress.LockAndIncrement();
                continue;
            }
            using Mat originalImage = currentLayer.LayerMat.Clone();
            using Mat currentBinaryMask = new Mat();
            CvInvoke.Threshold(originalImage, currentBinaryMask, 127, 255, Emgu.CV.CvEnum.ThresholdType.Binary);

            if (priorBinaryMasks.Count > 0)
            {
                using Mat combinedPriorMask = new Mat(originalImage.Size, Emgu.CV.CvEnum.DepthType.Cv8U, 1);
                combinedPriorMask.SetTo(new MCvScalar(0));
                foreach (var mask in priorBinaryMasks)
                {
                    CvInvoke.BitwiseOr(combinedPriorMask, mask, combinedPriorMask);
                }

                using Mat invertedCurrentMask = new Mat();
                CvInvoke.BitwiseNot(currentBinaryMask, invertedCurrentMask);

                using Mat recedingWhiteAreas = new Mat();
                CvInvoke.BitwiseAnd(combinedPriorMask, invertedCurrentMask, recedingWhiteAreas);

                if (CvInvoke.CountNonZero(recedingWhiteAreas) > 0)
                {
                    using Mat distTransformSrc = invertedCurrentMask.Clone();
                    using Mat distanceMap = new Mat();

                    if (_anisotropicCorrection.Value)
                    {
                        var originalSize = distTransformSrc.Size;
                        var newSize = new System.Drawing.Size((int)(originalSize.Width * _xFactor.Value), (int)(originalSize.Height * _yFactor.Value));
                        if (newSize != originalSize)
                        {
                            using Mat resizedSrc = new Mat();
                            CvInvoke.Resize(distTransformSrc, resizedSrc, newSize, 0, 0, Emgu.CV.CvEnum.Inter.Nearest);
                            using Mat resizedDistMap = new Mat();
                            CvInvoke.DistanceTransform(resizedSrc, resizedDistMap, null, Emgu.CV.CvEnum.DistType.L2, 5);
                            CvInvoke.Resize(resizedDistMap, distanceMap, originalSize, 0, 0, Emgu.CV.CvEnum.Inter.Linear);
                        }
                        else { CvInvoke.DistanceTransform(distTransformSrc, distanceMap, null, Emgu.CV.CvEnum.DistType.L2, 5); }
                    }
                    else { CvInvoke.DistanceTransform(distTransformSrc, distanceMap, null, Emgu.CV.CvEnum.DistType.L2, 5); }

                    using Mat recedingDistanceMap = new Mat();
                    distanceMap.CopyTo(recedingDistanceMap, recedingWhiteAreas);

                    using Mat labels = new Mat();
                    int numLabels = CvInvoke.ConnectedComponents(recedingWhiteAreas, labels);

                    if (numLabels > 1)
                    {
                        using Mat finalGradient = ProcessEnhancedEDT(recedingDistanceMap, labels, numLabels, _fadeDistance.Value);
                        CvInvoke.Max(originalImage, finalGradient, originalImage);
                        currentLayer.LayerMat = originalImage;
                    }
                }
            }

            priorBinaryMasks.Enqueue(currentBinaryMask.Clone());
            if (priorBinaryMasks.Count > _recedingLayers.Value)
            {
                priorBinaryMasks.Dequeue().Dispose();
            }

            Progress.LockAndIncrement();
        }

        foreach (var mask in priorBinaryMasks)
        {
            mask.Dispose();
        }
        return !Progress.Token.IsCancellationRequested;
    }

    private unsafe Mat ProcessEnhancedEDT(Mat recedingDistanceMap, Mat labels, int numLabels, float fadeDistanceLimit)
    {
        var finalGradientMap = new Mat(recedingDistanceMap.Size, Emgu.CV.CvEnum.DepthType.Cv8U, 1);
        finalGradientMap.SetTo(new MCvScalar(0));

        var maxVals = new float[numLabels];
        int width = labels.Width;
        int height = labels.Height;

        // First pass: find max distance for each label
        for (int y = 0; y < height; y++)
        {
            int* labelsPtr = (int*)labels.DataPointer.ToPointer() + y * width;
            float* distMapPtr = (float*)recedingDistanceMap.DataPointer.ToPointer() + y * width;
            for (int x = 0; x < width; x++)
            {
                int label = labelsPtr[x];
                if (label > 0)
                {
                    float dist = distMapPtr[x];
                    if (dist > maxVals[label])
                    {
                        maxVals[label] = dist;
                    }
                }
            }
        }

        // Second pass: calculate final gradient
        for (int y = 0; y < height; y++)
        {
            int* labelsPtr = (int*)labels.DataPointer.ToPointer() + y * width;
            float* distMapPtr = (float*)recedingDistanceMap.DataPointer.ToPointer() + y * width;
            byte* finalGradientPtr = (byte*)finalGradientMap.DataPointer.ToPointer() + y * width;
            for (int x = 0; x < width; x++)
            {
                int label = labelsPtr[x];
                if (label > 0)
                {
                    float dist = distMapPtr[x];
                    float denominator = Math.Min(maxVals[label], fadeDistanceLimit);
                    if (denominator > 0)
                    {
                        float clippedDist = Math.Min(dist, denominator);
                        float normalized = clippedDist / denominator;
                        float inverted = 1.0f - normalized;
                        finalGradientPtr[x] = (byte)(inverted * 255);
                    }
                }
            }
        }
        return finalGradientMap;
    }
}
