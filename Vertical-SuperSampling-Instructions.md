## [Examples from vertical supersampling.](https://imgur.com/a/vertical-supersampler-3d-antialiasing-msla-resin-prints-YH2rJ6e)

## Big Picture Steps:
1) Slice a model at an integer divisor of your layer height. For these examples I will use 5um slices for 40um print.  Recommend 5um as a good starting point. 
2) Collapse the layers to print height using averaging to bring in the most vertical gradient data. The first super sampling.
3) Sample in the layers above and below to blend additional grayscale and create vertically smooth transitions. 
4) Apply a remapping of the grayscale values to ones which produce fractional layer heights when printed. AKA lookup table / LUT. 
5) For < 8-bit grayscale printers, apply dithering. 16k printers - 3-bit grayscale, Anycubic - 4-bit grayscale.
6) Save and get the slice file to the printer.

## Tools you will need:
- Slicer of choice.  Recommend Chitubox for speed or PrusaSlicer for a little extra fidelity.
- UVTools 
- The UVTools scripts & LUT presets in this repo
  - Code -> Download Zip then unzip the downloaded folder.
  - Technically you only need VerticalSuperSampler-LUT-FSDithering.cs and EXP-100.lut from the scripts/ and LUTs/ folders respectively.
    
**Resin prerequisites:**
- Resin which responds well to grayscale.  I have successfully tested:
  - Anycubic "14k" Texture Gray / Beige / Peach / White
  - Siraya Tech Fast ABS-like Navy Gray / Gray
  - AceAddity Standard "Elite 8k" Gray / White.
  - Avoid black, dark gray, and clear consumer / hobby resins. Most have extremely strong light blocking by absorption which is difficult for this method.  The best resins are ones with little carbon black pigment and loads of white titanium dioxide which controls penetration depth by scattering light rather than absorption. 
  - ABS-like resins other than not-very-ABS-like Siraya Tech Fast ABS-like generally work poorly.  I have acceptable results with Elegoo's ABS-like Ultra Grey though. ABS-like resins will need more washing as they get a thicker clear sludge with lots of grayscale.
- Avoid brushing prints during the wash as the thicker grayscale is softer and may leave marks.  Ultrasonic washing may leave some microscopic craters.

## Detailed Steps:
## Step 1: Slicing the print
- Pick your multiplier.  Recommend at least 4-5 "sublayers" to build each print layer.  Better results will come all the way up to about 8, after which we get diminishing returns and significantly more computation overhead.  
- For our examples we will use a slicing layer height of 5um and a print layer height of 40um for a multiplier of 8x. 
- For this method, great print results start around 40um. 30um is a little better and retains more sharpness.  
- 50um layers are possible, but more difficult to get grayscale to "stick" and accumulate.
- Setup a slicing profile with a layer height of 5um / 0.005mm in your slicer.  Chitubox may give you a warning icon, but it will still do what is configured.  Satellite will give some warnings.  Have not tested Lychee, I would not trust it myself.
  - (Optional) Configure anti-aliasing.  Chitubox - use Anti-aliasing Level 2/4/8 or Grayscale Level 0-9 (these are all identical output at 12/16k in Chitubox 2.x).  
  - Do not use Image Blur unless you are going the Step 2 route without "Option A" and need anti-aliasing for surfaces nearly vertical to the build plate. In that case, a blur of 2-3 should be more than enough. Only have found this necessary so far with Dennys Wang's Ultimate AA test. 
  - Step 2 (Option A) applies a pyramid down / up sampling which produces more than enough XY blur. 
- Slice the model and save the slice file.
- If you need a "dummy" file for repacking, switch to your print height profile and slice the file again and save it with a different name.

## Step 2A (Option A): Collapse the layers
(faster* + more smoothing + couple extra steps)

Option A uses UVTools built-in layer re-height first and then performs the Out of Box sampling at print layer height.  The UVTools layer re-height will apply a pyramid down and pyramid up blur filter when averaging layers which is technically very lossy, but it does provide significantly smoother anti-aliasing to near vertical surfaces and is a much faster process. 
- Open the 5um slice file in UVTools.
- At the top right go to Actions -> Clone Layers. Choose the top or bottom layer and clone it enough times to make the total layer count evenly divisible by the multiplier, in this example 8 (skip this step if it is already evenly divisible).
- Cloning the bottom layer will thicken the rafts just a tad and is usually safest.
- Deleting a couple raft layers also works. Does not trim that much thickness. 
- Cloning the top layer too many times can put a funny point on top of the tallest object.
- Now go to Tools -> Adjust layer height. 
- Modifier: x8 -> XXXX layers at 0.040mm (or chosen multiplier / layer height)
- Anti-Aliasing: Average - Compute anti-aliasing by averaging the layer pixels
- Set the Bottom exposure and Normal exposure, BUT these will need to be rechecked later as the Bottom layer count is sometimes off.
- Kick it off and let the operation complete.
- Continue through Step 2 using the (Option A) settings for Out of Box sampling.

## Step 2 (full / Option B): Set Layer Height and Out of Box Sampling
(slower* + sharper while still getting excellent AA + one script)
Option B retains the maximum detail level but takes about 2x as long to complete.
- Open the 5um slice file in UVTools (already there if you came from Step 2A).
- Go to Tools -> Scripting and navigate to the folder where you unzipped my scripts.  Select the VerticalSuperSampler-LUT-FSDithering.cs script. 
- Enter your Target Layer Height, here we will use 40.
- Leave the box checked for Use Integer Accumulator and Integer Weight Zones on the default setting 100:1.
- Adjust the Out of Box (Below) and (Above) layer counts to 2x consolidation count / 2x print layer thickness above and below.  
  - (Option A) If you used the faster UVTools built-in Adjust layer height to consolidate layers to print thickness, set the Out of Box (Below) and (Above) layer counts **each to 2.**
  - (Option B) For 5um layers going into 40um, the multiplier is 8, so set Out of Box (Below) and (Above) **each to 16.**
- Adjust the Thread Count if necessary (e.g. high core count + low RAM, keep cores free to play video, etc.).  

## Step 3: Select the LUT
- For LUT File, click Select and go to the scripts/LUTs/ folder and choose the EXP-100.lut file.
- Leave Interpolate LUT checked.  

## Step 4: Dithering for 16k 3-bit or Anycubic 4-bit printers
- Enable Dithering.
- Leave the Gamma at 3.0 unless you have measured your grayscale curve.  This is the gamma / power curve I've found in testing 12k & 16k panels + Chitu mainboards and works successfully (The method even works with normal Chitu / Lychee blur radius slices and there is a standalone dithering script for this).
- Enter the bit-depth for your printer.  

## Step 5: Check output, settings, and save
- Scrub through the layers with the scrollbar and check for any visible defects.
- Verify the correct LayerHeight is shown in the Layer Data panel at the bottom left corner.
- Go to Tools -> Edit print parameters and verify bottom layers and normal layers counts and exposure times are correct as well as any other settings. 
- Now is the time to perform any other UVTools inspections you use for Issues as well as apply Suggestions settings (e.g. Wait time before cure 🤔).
- Once the above are complete, File -> Save / Save as...  
- Upload / copy slice file to flash drive and get printing. 


*Example times: Option A: with a 44mm tall, 5um x 8832 layers -> 40um x 1104, 12k slices this process takes 2-4 minutes for each stage for Adjust layer height and VerticalSuperSampler with LUT on an i9-9900k.  Option B takes about 16 minutes for the VerticalSuperSampler.  16k and Dithering adds a bit too. 
