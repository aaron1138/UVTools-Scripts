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
/// Applies a CPU-based FXAA (Fast Approximate Anti-Aliasing) effect to each layer.
/// </summary>
public class ScriptFXAA : ScriptGlobals
{
    private readonly ScriptNumericalInput<double> _contrastThreshold = new()
    {
        Label = "Contrast Threshold",
        Value = 0.125,
        Minimum = 0.01,
        Maximum = 1.0,
        Increment = 0.01,
        ToolTip = "Minimum local contrast to trigger anti-aliasing. Lower is more sensitive."
    };

    private readonly ScriptNumericalInput<int> _edgeSearchSpan = new()
    {
        Label = "Edge Search Span",
        Value = 8,
        Minimum = 2,
        Maximum = 16,
        ToolTip = "How far to search along an edge to find its endpoints."
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
        Script.Name = "FXAA Anti-Aliasing";
        Script.Description = "Applies a high-performance, edge-aware anti-aliasing effect similar to FXAA by detecting edge patterns and applying a localized blend.\n" +
                             "Lower Contrast Threshold makes it more sensitive to subtle edges.";
        Script.Author = "Aaron Baca via Jules (AI Agent)";
        Script.Version = new Version(1, 0, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _contrastThreshold,
            _edgeSearchSpan,
            _threadCount
        });
    }

    /// <summary>
    /// Validate user inputs here, this function trigger when user click on execute
    /// </summary>
    /// <returns>A error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        return null;
    }

    /// <summary>
    /// Execute the script, this function trigger when when user click on execute and validation passes
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public unsafe bool ScriptExecute()
    {
        Progress.Reset("Applying FXAA", Operation.LayerRangeCount);

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
            using Mat resultImage = originalImage.Clone(); // We write to a new image to avoid race conditions with reads

            byte* originalPtr = (byte*)originalImage.DataPointer;
            byte* resultPtr = (byte*)resultImage.DataPointer;
            int width = originalImage.Width;
            int height = originalImage.Height;
            float threshold = (float)_contrastThreshold.Value * 255f;

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int offset = y * width + x;

                    // 1. Edge Detection
                    byte lumaM = originalPtr[offset];
                    byte lumaN = originalPtr[offset - width];
                    byte lumaS = originalPtr[offset + width];
                    byte lumaW = originalPtr[offset - 1];
                    byte lumaE = originalPtr[offset + 1];

                    float lumaMin = Math.Min(lumaM, Math.Min(Math.Min(lumaN, lumaS), Math.Min(lumaW, lumaE)));
                    float lumaMax = Math.Max(lumaM, Math.Max(Math.Max(lumaN, lumaS), Math.Max(lumaW, lumaE)));
                    float contrast = lumaMax - lumaMin;

                    if (contrast < threshold)
                    {
                        continue; // Not an edge, skip
                    }

                    // 2. Edge Direction
                    float edgeHorizontal = Math.Abs(lumaN - lumaS);
                    float edgeVertical = Math.Abs(lumaW - lumaE);
                    bool isHorizontal = edgeHorizontal >= edgeVertical;

                    // 3. Find Edge Endpoints
                    float lumaP = isHorizontal ? lumaN : lumaW;
                    float lumaN_end = isHorizontal ? lumaS : lumaE;

                    float gradP = Math.Abs(lumaP - lumaM);
                    float gradN = Math.Abs(lumaN_end - lumaM);

                    int step = isHorizontal ? width : 1;
                    int dir = isHorizontal ? -1 : 1;

                    // Search positive direction
                    int pOffset = offset;
                    int pDist = 0;
                    for (; pDist < _edgeSearchSpan.Value; pDist++)
                    {
                        pOffset += step * dir;
                        if (pOffset < 0 || pOffset >= width * height || Math.Abs(originalPtr[pOffset] - lumaM) > gradP) break;
                    }

                    // Search negative direction
                    int nOffset = offset;
                    int nDist = 0;
                    for (; nDist < _edgeSearchSpan.Value; nDist++)
                    {
                        nOffset -= step * dir;
                        if (nOffset < 0 || nOffset >= width * height || Math.Abs(originalPtr[nOffset] - lumaM) > gradN) break;
                    }

                    // 4. Subpixel Blending
                    float dist = Math.Min(pDist, nDist);
                    float blendFactor = 0.5f - dist / (pDist + nDist);

                    byte blendedColor = (byte)((lumaP + lumaN_end) / 2.0f);
                    resultPtr[offset] = (byte)(blendedColor * blendFactor + lumaM * (1.0f - blendFactor));
                }
            }

            layer.LayerMat = resultImage.Clone();
            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
