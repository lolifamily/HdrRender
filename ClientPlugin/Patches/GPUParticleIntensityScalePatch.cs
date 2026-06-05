using HarmonyLib;
using VRage.Render.Particles;
using VRageRender;

namespace ClientPlugin.Patches;

// GPU particle pipeline (smoke wisps, explosion sparks, dust, magic FX via
// .sbc particle effects) is independent of the CPU billboard path. Producer
// encodes emissive intent via two separate keyframe arrays:
//   - Intensity0..3:   "Color intensity" property, multiplies per-particle
//                      lighting in the shader (light = intensity.xxx).
//                      Diffuse particles use Intensity <= 1 (smoke ~0.5,
//                      dust ~1). Emissive particles push it way up
//                      (Muzzle_Flash 100, ExhaustFire 1000).
//   - Emissivity0..3:  Pure additive emissive, added to light before color
//                      multiply. Diffuse particles keep this at 0; some
//                      emissive particles (fireworks, weldercoat) use it
//                      instead of Intensity.
// Shader: Output.Color = (light + emissivity.xxx) * color
//
// Scaling both Intensity and Emissivity by K boosts the emissive contribution
// proportionally. Emissivity has no threshold because 0 * K = 0 is already a
// no-op for diffuse particles. Intensity does need a threshold because diffuse
// content uses Intensity values up to 30 (weather storms, smoke drilldust,
// muzzle smoke etc.) and stock emissive content starts at 50 - see
// IntensityThreshold below.
//
// We hook MyGPUParticleRenderer.Emit (called from Run, just before mapping
// the array into a GPU constant buffer) and only boost — no Postfix needed:
//   1. Emit immediately uploads emitterData[] into the GPU's
//      m_emitterStructuredBuffer (MyGPUParticleRenderer.Emit:184-188).
//      After that, Simulate/Render read GPU memory, not the CPU array.
//   2. Next frame's MyGPUParticleRenderer.Update runs before Run via an
//      explicit DependsOn (MyRenderScheduler:174-177), and Update->Gather->
//      MyGPUEmitter.Update does `data = EmitterData.Data` (line 141) — a
//      full struct copy that overwrites every slot we touched.
// So our writes are invisible by the time anything cares. Skipping the
// Postfix saves a redundant pass over 1024 slots per frame.
[HarmonyPatch(typeof(MyGPUParticleRenderer), "Emit")]
[HarmonyPriority(Priority.First)]
internal static class GpuParticleIntensityScalePatch
{
    // Empirical cutoff from scanning all stock GPU particles in Particles_B.sbc /
    // Particles_Weather.sbc: max Intensity is naturally bimodal. Diffuse content
    // (smoke, dust, weather storms, character steps, wheel marks, bubbles, fish,
    // terrarium critters) tops out at 30. Emissive content (Muzzle_Flash family,
    // Smoke_Firework, Tracer, Welder, Fireflies, ExhaustFire family) starts at
    // 50 and goes up to 1000+. The 30..50 gap has zero entries, so >30 is a
    // clean split with no ambiguous edge cases in stock content.
    private const float IntensityThreshold = 30f;

    private static void Prefix(int emitterCount, MyGPUEmitterLayoutData[] emitterData)
    {
        var k = Config.Current.LdrIntensity;
        if (k <= 1f) return;
        for (var i = 0; i < emitterCount; i++)
        {
            ref var d = ref emitterData[i];

            d.Emissivity0 *= k;
            d.Emissivity1 *= k;
            d.Emissivity2 *= k;
            d.Emissivity3 *= k;

            if (System.Math.Max(System.Math.Max(d.Intensity0, d.Intensity1),
                                System.Math.Max(d.Intensity2, d.Intensity3)) <= IntensityThreshold)
                continue;

            d.Intensity0 *= k;
            d.Intensity1 *= k;
            d.Intensity2 *= k;
            d.Intensity3 *= k;
        }
    }
}
