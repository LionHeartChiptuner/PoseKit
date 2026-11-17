using FistVR;
using HarmonyLib;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // The support grip follows H3VR's own two-handed pistol stabilization (FVRFireArm.IsTwoHandStabilized:
    // the off hand empty and its palm within 15 cm of the shooting hand's palm, no button needed).
    // GlovesTan3 only keeps a pose on a hand while its grip button is pressed or it's "near a firearm" by
    // holding part of one, so a stabilizing hand would drop the pose every frame. This tells GlovesTan3 that
    // a hand showing the support grip counts as near the firearm.
    [HarmonyPatch(typeof(HandsPoseSystem), "IsHandNearFirearm")]
    static class SupportGripKeepPatch
    {
        public static PoseKitPlugin Plugin;

        static void Postfix(HandsPoseSystem __instance, ref bool __result)
        {
            if (!__result && Plugin != null && __instance != null && Plugin.SupportShowing(__instance.isLeft)) __result = true;
        }
    }

    public partial class PoseKitPlugin
    {
        internal bool SupportShowing(bool left)
        {
            HandPoseItem h;
            return _supportOn.TryGetValue(left, out h) && h != null;
        }

        // PoseKit's own (shorter) range, measured palm to palm like H3VR does. Once showing, it lets go a bit
        // further out so it doesn't flicker at the edge.
        bool WithinSupportRange(FVRFireArm gun, FVRViveHand freeHand, bool alreadyShowing)
        {
            var a = gun.m_hand.PalmTransform != null ? gun.m_hand.PalmTransform.position : gun.m_hand.transform.position;
            var b = freeHand.PalmTransform != null ? freeHand.PalmTransform.position : freeHand.transform.position;
            float range = Mathf.Clamp(_supportRange.Value, 0.01f, 0.15f) * (alreadyShowing ? 1.25f : 1f);
            return Vector3.Distance(a, b) <= range;
        }

        // H3VR's stabilization, limited to an empty off hand (a hand holding a flashlight or magazine keeps
        // its own pose).
        static bool StabilizingWith(FVRFireArm gun, FVRViveHand freeHand)
        {
            return gun != null && gun.IsHeld && gun.m_hand != null && gun.m_hand.OtherHand == freeHand
                   && freeHand != null && freeHand.CurrentInteractable == null && gun.IsTwoHandStabilized();
        }
    }
}
