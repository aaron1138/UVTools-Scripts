/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emgu.CV;
using UVtools.Core;
using UVtools.Core.Scripting;

namespace UVtools.ScriptSample;

/// <summary>
/// Applies a Look-Up Table (LUT) to the layers.
/// </summary>
public class ScriptLUTEngine : ScriptGlobals
{
    private readonly ScriptOpenFileDialogInput _lutFile = new()
    {
        Label = "LUT File",
        Filters = new List<ScriptFileDialogInput.ScriptFileDialogFilter>
        {
            new() { Name = "LUT Files", Extensions = new List<string> { "lut" } }
        },
        ToolTip = "Select the .lut file to apply to the layers."
    };

    private byte[] _loadedLut = new byte[256];

    /// <summary>
    /// Set configurations here, this function trigger just after load a script
    /// </summary>
    public void ScriptInit()
    {
        Script.Name = "LUT Engine";
        Script.Description = "Applies a Look-Up Table (LUT) to the layers.";
        Script.Author = "Jules (AI Agent)";
        Script.Version = new Version(0, 1);
        Script.MinimumVersionToRun = new Version(5, 2, 0);

        Script.UserInputs.Add(_lutFile);

        // Initialize with a linear LUT
        for (int i = 0; i < 256; i++)
        {
            _loadedLut[i] = (byte)i;
        }
    }

    /// <summary>
    /// Validate user inputs here, this function trigger when user click on execute
    /// </summary>
    /// <returns>A error message, empty or null if validation passes.</returns>
    public string? ScriptValidate()
    {
        if (string.IsNullOrEmpty(_lutFile.Value) || !File.Exists(_lutFile.Value))
        {
            return "Please select a valid LUT file.";
        }

        try
        {
            var json = File.ReadAllText(_lutFile.Value);
            var lutList = JsonSerializer.Deserialize<List<int>>(json);
            if (lutList == null || lutList.Count != 256)
            {
                return "Invalid LUT file format: Expected a list of 256 numbers.";
            }
            _loadedLut = lutList.Select(v => (byte)v).ToArray();
        }
        catch (Exception ex)
        {
            return $"Failed to load LUT file: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Execute the script, this function trigger when when user click on execute and validation passes
    /// </summary>
    /// <returns>True if executes successfully to the end, otherwise false.</returns>
    public bool ScriptExecute()
    {
        Progress.Reset("Applying LUT", (uint)Operation.LayerRangeCount);

        using Mat lutMat = new Mat(1, 256, Emgu.CV.CvEnum.DepthType.Cv8U, 1);
        lutMat.SetTo(_loadedLut);

        Parallel.For(Operation.LayerIndexStart, Operation.LayerIndexEnd + 1, CoreSettings.GetParallelOptions(Progress), i =>
        {
            Progress.PauseOrCancelIfRequested();

            var layer = SlicerFile[i];
            if (layer.IsEmpty)
            {
                Progress.LockAndIncrement();
                return;
            }
            using Mat originalImage = layer.LayerMat;
            using Mat transformedImage = new Mat();
            CvInvoke.LUT(originalImage, lutMat, transformedImage);
            layer.LayerMat = transformedImage.Clone();

            Progress.LockAndIncrement();
        });

        return !Progress.Token.IsCancellationRequested;
    }
}
