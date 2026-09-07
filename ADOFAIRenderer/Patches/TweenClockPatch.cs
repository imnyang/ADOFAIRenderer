using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using DG.Tweening.Core;
using HarmonyLib;
using ADOFAIRenderer.Renderer;

namespace ADOFAIRenderer.Patches
{
    // DOTween's independent tweens measure realtimeSinceStartup, even with
    // captureFramerate enabled. Keep the original timestamp bookkeeping, but
    // replace its elapsed interval so UI/independent effects cannot race encoding.
    [HarmonyPatch(typeof(DOTweenComponent), "Update")]
    internal static class TweenClockPatch
    {
        static float Delta(float actual) => RendererController.ControlsTime ? 1f / RendererController.Instance.Clock.Fps : actual;
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(DOTweenComponent), "_unscaledDeltaTime");
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field))
                {
                    var adjustment = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TweenClockPatch), nameof(Delta)));
                    adjustment.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    yield return adjustment;
                    count++;
                }
                yield return instruction;
            }
            if (count == 0) throw new InvalidOperationException("Unsupported DOTween independent clock.");
        }
    }
}
