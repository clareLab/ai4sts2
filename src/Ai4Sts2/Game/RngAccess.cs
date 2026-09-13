using Ai4Sts2.Core;
using MegaCrit.Sts2.Core.Random;

namespace Ai4Sts2.Game;

public static class RngAccess
{
    public static RngState Capture(Rng rng)
    {
        var r = rng._random;
        return new RngState(rng._counter, r._s0, r._s1, r._s2, r._s3);
    }

    public static void Restore(Rng rng, in RngState state)
    {
        rng._counter = state.Counter;
        var r = rng._random;
        r._s0 = state.S0;
        r._s1 = state.S1;
        r._s2 = state.S2;
        r._s3 = state.S3;
    }
}
