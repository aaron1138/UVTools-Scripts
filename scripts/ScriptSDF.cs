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
/// Generates a Signed Distance Field (SDF) from the layers.
/// </summary>
public class ScriptSDF : ScriptGlobals
{
    private readonly ScriptNumericalInput<float> _spread = new()
    {
        Label = "Spread",
        Minimum = 1,
        Maximum = 1000,
        Increment = 1,
        Value = 10,
        ToolTip = "The spread of the distance field in pixels."
    };

    private readonly ScriptCheckBoxInput _inside = new()
    {
        Label = "Inside",
        Value = true,
        ToolTip = "Generate the distance field inside the shapes."
    };

    /// <summary>
    /// Set configurations here, this function trigger just after load a script
    /// </summary>
    public void ScriptInit()
    {
        Script.Name = "SDF Generation";
        Script.Description = "Generates a Signed Distance Field (SDF) from the layers.";
        Script.Author = "Jules (AI Agent)";
        Script.Version = new Version(0, 1);
        Script.MinimumVersionToRun = new Version(5, 2, 0);

        Script.UserInputs.AddRange(new ScriptBaseInput[] {
            _spread,
            _inside
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
    public bool ScriptExecute()
    {
        Progress.Reset("Generating SDF", (uint)Operation.LayerRangeCount);

        Parallel.For((int)Operation.LayerIndexStart, (int)Operation.LayerIndexEnd + 1, CoreSettings.GetParallelOptions(Progress), i =>
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

            using Mat distanceTransform = new Mat();
            CvInvoke.DistanceTransform(binaryImage, distanceTransform, null, Emgu.CV.CvEnum.DistType.L2, 5);

            // Normalize and convert to 8-bit
            CvInvoke.Normalize(distanceTransform, distanceTransform, 0, 255, Emgu.CV.CvEnum.NormType.MinMax);
            distanceTransform.ConvertTo(distanceTransform, Emgu.CV.CvEnum.DepthType.Cv8U);

            if (!_inside.Value)
            {
                CvInvoke.BitwiseNot(distanceTransform, distanceTransform);
            }

            layer.LayerMat = distanceTransform.Clone();

            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
