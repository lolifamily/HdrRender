// Port of the engine's Postprocess/EyeAdaptation/EyeAdaptation.hlsl, with the
// exposure it adapts toward bent. Above the environment's constant exposure (its
// exposure with eye adaptation off) the eye covers ADAPTATION of the way to a
// brighter scene, so bright scenes stay brighter. At and below it nothing changes:
// the engine's key value already adapts dark scenes only in part. The smoothing is
// linear in log2 exposure, so it keeps its speeds over a smaller swing.
//
// A copy, not an include: the engine's file starts with a UTF-8 byte order mark,
// which its include handler passes on to the compiler (X3000, illegal character),
// and Postprocess/Defines.hlsli, where CalculateExposure lives, has no include
// guard to override it through. The headers come in the engine file's order, and
// the body is the engine's as built on PC but for the two lines marked.
//
// CONSTANT_LUMINANCE is the environment's, from the CPU: while eye adaptation is
// on, the engine sends -1 in its place (MyPostprocessSettings.GetProcessedData).
// Both defines come from PartialEyeAdaptation, which has the engine build this file.

#include <Postprocess/EyeAdaptation/EyeAdaptation.hlsli>
#include <Postprocess/PostprocessBase.hlsli>
#include <Postprocess/Defines.hlsli>
#include <Frame.hlsli>

float2 __pixel_shader(PostprocessVertex input) : SV_Target0
{
    float totalSum = 0;
    uint i;
    for (i = 0; i < HISTOGRAM_BIN_COUNT; ++i)
    {
        totalSum += (float)_Histogram[i];
    }

    // avoid 0 total sum, it could theoretically happen with high luminance threshold
    totalSum = max(totalSum, 1);

    float oneOverTotalSum = 1 / totalSum;

    float2 sumMin = 0;
    float2 sumMax = 0;

    for (i = 0; i < HISTOGRAM_BIN_COUNT; ++i)
    {
        float binPercentage = (float)_Histogram[i] * oneOverTotalSum;
        float luminance = GetLuminanceFromHistogramBin(i);

        float binPercentageMin = min(binPercentage, HistogramMinFilter - sumMin.y);
        float binPercentageMax = min(binPercentage, HistogramMaxFilter - sumMax.y);

        sumMin += float2(luminance * binPercentageMin, binPercentageMin);
        sumMax += float2(luminance * binPercentageMax, binPercentageMax);
    }

    // avoid division by zero
    sumMin.y = max(sumMin.y, 0.0001);
    float2 filteredLuminance = sumMax - sumMin;

    float averageLuminance = clamp(filteredLuminance.x / filteredLuminance.y, HistogramMinBrightness, HistogramMaxBrightness);

    float desiredExposure = CalculateExposure(averageLuminance, frame_.Post.LuminanceExposure);
    // The two lines this port is for.
    float constantExposure = CalculateExposure(CONSTANT_LUMINANCE, frame_.Post.LuminanceExposure);
    desiredExposure += (1.0 - ADAPTATION) * max(constantExposure - desiredExposure, 0.0);
    float lastExposure = _LastAutoExposure.Sample(PointSampler, float2(0.5, 0.5)).y;

    float exposureDelta = desiredExposure - lastExposure;
    float speed = exposureDelta < 0 ? frame_.Post.EyeAdaptationSpeedUp : frame_.Post.EyeAdaptationSpeedDown;
    float exposure = lastExposure + exposureDelta * (1.0 - exp2(-frame_.frameTimeDelta * speed));

    return float2(desiredExposure, exposure);
}
