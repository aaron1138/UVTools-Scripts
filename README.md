## Vertical SuperSampling Instructions are [here.](https://diffusingerror.blogspot.com/2025/12/guide-vertical-super-sampling-3d-anti.html)



## VSB Migration Scripts

This directory contains C# scripts for UVTools, migrating functionality from Voxel-Stack-Blender (VSB) and adding new features.

## Script: Enhanced EDT Blending (`ScriptEnhancedEDT.cs` & `ScriptEnhancedEDTParallel.cs`)

**Purpose:** Anti-aliasing along the Z-axis using an Enhanced Euclidean Distance Transform. Smooths transitions between layers.

**UI Options:**
-   **Fade Distance:** Max distance (pixels) for blending.
-   **Receding Layers:** Number of previous layers to consider.
-   **Factor Anisotropy:** Account for non-square pixels.
-   **X/Y Pixel Size (µm):** Pixel dimensions.
-   **Thread Count:** (Parallel) Number of threads.

**Threading:** Parallel version uses `Parallel.For`.

## Script: Exposure Calibration Masking (`ExposureCalibrationMasking.cs`)

**Purpose:** Creates a calibration file by splitting each layer into multiple sub-exposures with incremental masking. This allows testing multiple exposure times on a single print by partitioning the build plate.

**UI Options:**
-   **Divisions X/Y:** Number of partitions along X and Y axes. Total partitions = X * Y.
-   **Align Left/Top:** Controls the ordering of the incremental exposure.

**Logic:** Each original layer is replicated `TotalDivisions` times (at the same Z height). For each subsequent sub-layer, one additional partition is masked out (black). This results in the first partition receiving 1 exposure unit, the second 2 units, and so on. Lift movements are disabled between sub-layers.

## Script: LUT Engine (`ScriptLUTEngine.cs`)

**Purpose:** Applies a 1D Look-Up Table (LUT) to layer brightness.

**UI Options:**
-   **LUT File:** Path to `.lut` file (JSON array of 256 ints).

**Threading:** Uses `Parallel.For`.

## Script: SDF Generation (`ScriptSDF.cs`)

**Purpose:** Generates a 2D Signed Distance Field from the layer image.

**UI Options:**
-   **Spread:** Max distance.
-   **Inside:** Generate inside/outside.

**Threading:** Uses `Parallel.For`.

## Script: SDF Generation (Enhanced) (`ScriptSDF-Enhanced.cs`)

**Purpose:** Advanced SDF with anisotropic scaling and grayscale mapping.

**UI Options:**
-   **Factor Anisotropy:** Enable scaling.
-   **X/Y Pixel Size:** Dimensions in µm.
-   **Thresholds:** Lower/Upper distance limits for mapping.
-   **Gamma:** Correction curve.

**Threading:** Uses `Parallel.For` with unsafe code for performance.

## Script: NanoDLP Multi-Exposure Import (`NanoDLPMultiExposureImport.cs`)

**Purpose:** Imports NanoDLP slice files (Zip or Folder) with multi-exposure per layer. Handles custom JSON arrays and file naming.

**UI Options:**
-   **NanoDLP File/Folder:** Source.
-   **Packed RGB (3:1):** Unpack RGB images to high-res grayscale.

**Threading:** **Batched Multi-threading.** reads files sequentially in batches, then processes (decodes/unpacks) them in parallel to balance IO and CPU usage while managing RAM.

## Script: NanoDLP Multi-Exposure Export (`NanoDLPMultiExposureExport.cs`)

**Purpose:** Exports to NanoDLP multi-exposure format (.nanodlp zip).

**UI Options:**
-   **Output File:** Destination zip.
-   **Divisor:** Sub-layers per layer.
-   **Exposure Times:** Comma-separated list.
-   **Pack into RGB:** Pack grayscale to 3:1 RGB.

**Threading:** **Batched Multi-threading.** Processes layers (encodes/packs) in parallel batches, then writes to Zip sequentially.
