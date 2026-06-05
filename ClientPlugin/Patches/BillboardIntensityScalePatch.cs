using System;
using System.Collections.Generic;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

// Boost emissive LDR billboards (thrusters, lasers, muzzle flashes) by a user-chosen
// factor K. Producer encodes emissive intent via ColorIntensity > 1; diffuse / UI
// billboards keep ColorIntensity = 1 (default) and are never touched.
//
// We hook MyBillboardRenderer.Gather and mutate ColorIntensity on every MyBillboard
// the renderer is about to read, then restore it in a Finalizer so producers and
// persistent billboards don't accumulate drift across frames. Finalizer (not Postfix)
// because Gather can throw (missing material, atlas miss, OOM on Array.Resize) and
// Postfix wouldn't run on the exception path — see Finalizer comment below.
//
// HarmonyPriority.First ensures our Prefix runs before any other Prefix on the same
// method. Plugins like Prism.RenderPerf prefix Gather and return false to replace
// SE's whole billboard pipeline with their own; they still read ColorIntensity
// from the same MyBillboard instances. Running first means our boost is already
// baked into the field by the time their reader runs.
//
// Selection rule: ColorIntensity > 1 - the explicit "brighter than SDR white"
// producer signal. Color magnitude alone is unreliable (dark-color emissive can
// have ColorIntensity=5 but Color.magnitude < 1 after baking).
[HarmonyPatch(typeof(MyBillboardRenderer), nameof(MyBillboardRenderer.Gather))]
[HarmonyPriority(Priority.First)]
internal static class BillboardIntensityScalePatch
{
    private static readonly List<(MyBillboard b, float orig)> Saved = new(4096);
    // Stock SE's MyTransparentGeometry.AddLineBillboard / AddPointBillboard / etc.
    // intentionally puts the same MyBillboard into both BillboardsWrite AND the
    // persistent linked list when the caller passes persistentBillboards != null
    // (engineer tool highlight, blueprint paste preview, block rotation hints,
    // grid selection box). After swap, the same instance shows up in both
    // BillboardsRead and PersistentBillboards, so naive iteration calls ScaleOne
    // twice and multiplies ColorIntensity by K twice (K^2). SavedSet uses
    // reference equality (MyBillboard does not override Equals/GetHashCode) to
    // dedupe by object identity, which is what we want.
    private static readonly HashSet<MyBillboard> SavedSet = new(4096);
    private static float _kFrame;
    private static readonly Action<MyBillboard> PersistentVisitor = ScaleOne;

    private static void ScaleOne(MyBillboard bb)
    {
        if (bb == null || bb.ColorIntensity <= 1.0f) return;
        if (!SavedSet.Add(bb)) return;
        Saved.Add((bb, bb.ColorIntensity));
        bb.ColorIntensity *= _kFrame;
    }

    private static void Prefix()
    {
        var k = Config.Current.LdrIntensity;
        if (k <= 1f) return;
        _kFrame = k;

        foreach (var bb in MyRenderProxy.BillboardsRead)
            ScaleOne(bb);

        var oncePool = MyBillboardRenderer.m_billboardsOncePool;
        var onceCount = oncePool.GetAllocatedCount();
        for (var i = 0; i < onceCount; i++)
            ScaleOne(oncePool.GetAllocatedItem(i));

        MyRenderProxy.ApplyActionOnPersistentBillboards(PersistentVisitor);
    }

    // Finalizer runs in Harmony's try/finally: covers the normal frame plus any
    // exception thrown from Prefix / original Gather / Postfix. A Postfix would
    // be skipped on the exception path, leaving persistent billboards with their
    // ColorIntensity permanently multiplied by K — corruption that compounds
    // every time Gather throws.
    //
    // void return (not Exception) keeps Harmony's rethrow IL intact and preserves
    // the original stack trace; returning Exception forces a throw-rewrite.
    private static void Finalizer()
    {
        for (var i = 0; i < Saved.Count; i++)
        {
            var (bb, orig) = Saved[i];
            bb.ColorIntensity = orig;
        }
        Saved.Clear();
        SavedSet.Clear();
    }
}
