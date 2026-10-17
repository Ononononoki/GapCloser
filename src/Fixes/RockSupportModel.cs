using System;

namespace GapCloser.Fixes
{
    // the game's support rule for a mine rock (MineRock5.UpdateSupport), worked out in memory. per part: whether its box
    // touches support that does not depend on the rock itself (the ground, any solid object that is not destructible,
    // another destructible lower than the rock), and which of the rock's own parts its box touches. the game starts with
    // every part unsupported and makes 3 passes over the parts in list order: a part becomes supported by outside support
    // or by one of its own touching parts that is already supported at that moment
    internal static class RockSupportModel
    {
        public const int Passes = 3;

        // removed: a part taken out (its collider gone, so no other part counts it), or -1
        public static void Run(bool[] outside, int[][] own, int removed, bool[] supported) => Run(outside, own, removed, null, supported);

        // gone: parts whose colliders are gone (destroyed since the touching lists were taken), may be null
        public static void Run(bool[] outside, int[][] own, int removed, bool[] gone, bool[] supported)
        {
            int n = outside.Length;
            Array.Clear(supported, 0, n);
            for (int pass = 0; pass < Passes; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (supported[i]) continue;
                    if (outside[i])
                    {
                        supported[i] = true;
                        continue;
                    }
                    var touching = own[i];
                    if (touching == null) continue;
                    for (int k = 0; k < touching.Length; k++)
                    {
                        int j = touching[k];
                        if (j != removed && (gone == null || !gone[j]) && supported[j])
                        {
                            supported[i] = true;
                            break;
                        }
                    }
                }
            }
        }

        // the parts the game would destroy after a support check: alive, not the removed one, not supported
        public static int Falls(bool[] alive, bool[] supported, int removed)
        {
            int count = 0;
            for (int i = 0; i < alive.Length; i++)
                if (alive[i] && i != removed && !supported[i]) count++;
            return count;
        }

        // what the game does after a hit that broke the parts in broke (each break runs one support check, in this order):
        // every check destroys the alive parts left unsupported, whose colliders are then gone for the next check. dead
        // holds the parts dead before the hit and gets the ones dead after; returns how many fell (broken parts not counted)
        public static int Replay(bool[] outside, int[][] own, bool[] dead, int[] broke, bool[] scratch)
        {
            int fell = 0;
            foreach (int b in broke)
            {
                if (dead[b]) continue;
                dead[b] = true;
                Run(outside, own, -1, dead, scratch);
                for (int i = 0; i < dead.Length; i++)
                {
                    if (dead[i] || scratch[i]) continue;
                    dead[i] = true;
                    fell++;
                }
            }
            return fell;
        }

        // for every alive part: how many other parts fall when it breaks (the next check runs without it), how many of those
        // are supported now (holds), and which parts supported now fall at the break of some single part (hangs)
        public static void Classify(bool[] outside, int[][] own, bool[] alive, bool[] now, bool[] scratch, int[] falls, int[] holds, bool[] hangs)
        {
            Array.Clear(hangs, 0, hangs.Length);
            for (int x = 0; x < alive.Length; x++)
            {
                falls[x] = holds[x] = 0;
                if (!alive[x]) continue;
                Run(outside, own, x, scratch);
                for (int j = 0; j < alive.Length; j++)
                {
                    if (!alive[j] || j == x || scratch[j]) continue;
                    falls[x]++;
                    if (!now[j]) continue;
                    holds[x]++;
                    hangs[j] = true;
                }
            }
        }
    }
}
