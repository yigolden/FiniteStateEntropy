using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace FiniteStateEntropy
{
    internal static class SequenceHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<byte> GetFirstSpan(this ReadOnlySequence<byte> sequence)
        {
#if NO_READONLYSEQUENCE_FISTSPAN
            return sequence.First.Span;
#else
            return sequence.FirstSpan;
#endif
        }
    }
}
