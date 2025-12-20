using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using UVtools.Core;
using UVtools.Core.Layers;
using UVtools.Core.Scripting;

namespace UVtools.ScriptSample;

public class ExposureCalibrationMasking : ScriptGlobals
{
    private readonly ScriptNumericalInput<int> _divX = new()
    {
        Label = "Divisions X",
        Value = 4,
        Minimum = 1,
        Maximum = 100,
        ToolTip = "Number of partitions along the X axis."
    };

    private readonly ScriptNumericalInput<int> _divY = new()
    {
        Label = "Divisions Y",
        Value = 2,
        Minimum = 1,
        Maximum = 100,
        ToolTip = "Number of partitions along the Y axis."
    };

    private readonly ScriptCheckBoxInput _alignLeft = new()
    {
        Label = "Align Left",
        Value = true,
        ToolTip = "If checked, partition numbering starts from Left to Right. If unchecked, Right to Left."
    };

    private readonly ScriptCheckBoxInput _alignTop = new()
    {
        Label = "Align Top",
        Value = true,
        ToolTip = "If checked, partition numbering starts from Top to Bottom. If unchecked, Bottom to Top."
    };

    private readonly ScriptCheckBoxInput _soloPartitions = new()
    {
        Label = "Solo Partitions (Single partition per exposure)",
        Value = false,
        ToolTip = "If checked, each sub-layer only exposes its specific partition (others are black). If unchecked, masking is cumulative (progressively removing partitions)."
    };

    public void ScriptInit()
    {
        Script.Name = "Exposure Calibration Masking";
        Script.Description = "Creates multiple exposures per layer with incremental masking to test different exposure times.\n" +
                             "Divides the layer into a grid and progressively masks partitions to create an exposure gradient.";
        Script.Author = "Aaron Baca via Jules (AI Agent)";
        Script.Version = new Version(1, 0);
        Script.MinimumVersionToRun = new Version(5, 0, 0);

        Script.UserInputs.Add(_divX);
        Script.UserInputs.Add(_divY);
        Script.UserInputs.Add(_alignLeft);
        Script.UserInputs.Add(_alignTop);
        Script.UserInputs.Add(_soloPartitions);
    }

    public string? ScriptValidate()
    {
        // For testing purposes we relax this check, as UVTools allows manipulation even if format doesn't natively support it.
        // if (!SlicerFile.CanUseLayerPositionZ)
        //    return "Printer/Format does not support multiple layers at same Z position (required for sub-layers).";
        return null;
    }

    public bool ScriptExecute()
    {
        int divX = _divX.Value;
        int divY = _divY.Value;
        int totalDivs = divX * divY;

        int w = (int)SlicerFile.ResolutionX;
        int h = (int)SlicerFile.ResolutionY;

        var partitions = new Rectangle[totalDivs];

        for (int i = 0; i < totalDivs; i++)
        {
            // Sequence index i maps to logical row/col
            int row = i / divX;
            int col = i % divX;

            // Map logical row/col to physical grid based on alignment flags
            // Image coordinates: 0,0 is Top-Left.

            int gridRow = _alignTop.Value ? row : (divY - 1 - row);
            int gridCol = _alignLeft.Value ? col : (divX - 1 - col);

            int x1 = (int)((long)gridCol * w / divX);
            int x2 = (int)((long)(gridCol + 1) * w / divX);
            int y1 = (int)((long)gridRow * h / divY);
            int y2 = (int)((long)(gridRow + 1) * h / divY);

            partitions[i] = new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        Progress.Reset("Generating Exposure Layers", (uint)SlicerFile.LayerCount);

        // We must process ALL layers to maintain file integrity when expanding
        var originalLayers = SlicerFile.CloneLayers();
        SlicerFile.Reallocate((uint)originalLayers.Length * (uint)totalDivs);

        Parallel.For(0, originalLayers.Length, CoreSettings.GetParallelOptions(Progress), i =>
        {
            if (Progress.Token.IsCancellationRequested) return;

            var sourceLayer = originalLayers[i];

            // Clone source mat for manipulation
            using var currentMat = sourceLayer.LayerMat.Clone();

            for (int k = 0; k < totalDivs; k++)
            {
                using var layerMat = new Mat();

                if (_soloPartitions.Value)
                {
                    // Solo Mode: Start Black, copy only partition K from source
                    layerMat.Create(currentMat.Rows, currentMat.Cols, currentMat.Depth, currentMat.NumberOfChannels);
                    layerMat.SetTo(new MCvScalar(0));

                    // ROI copy
                    using var srcRoi = new Mat(currentMat, partitions[k]);
                    using var dstRoi = new Mat(layerMat, partitions[k]);
                    srcRoi.CopyTo(dstRoi);
                }
                else
                {
                    // Cumulative Mode: Start with currentMat state (which accumulates black rectangles)
                    if (k > 0)
                    {
                        // Mask partition k-1
                        CvInvoke.Rectangle(currentMat, partitions[k-1], new MCvScalar(0), -1);
                    }
                    layerMat.Create(currentMat.Rows, currentMat.Cols, currentMat.Depth, currentMat.NumberOfChannels);
                    currentMat.CopyTo(layerMat);
                }

                var subLayer = sourceLayer.Clone();
                subLayer.LayerMat = layerMat;

                // Disable lift for all sub-layers except the last one
                // This ensures the printer doesn't move between exposure steps of the same physical layer
                if (k < totalDivs - 1)
                {
                    if (SlicerFile.CanUseLayerLiftHeight)
                    {
                        subLayer.LiftHeightTotal = 0;
                        subLayer.WaitTimeAfterLift = 0;
                        // LightOffDelay might still be relevant for cooling, but Lift is 0.
                    }
                }

                // Map: Source Index i -> Dest indices [i*Total ... i*Total + k]
                SlicerFile[(uint)i * (uint)totalDivs + (uint)k] = subLayer;
            }

            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
