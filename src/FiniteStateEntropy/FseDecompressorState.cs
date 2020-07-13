namespace FiniteStateEntropy
{
    public enum FseDecompressorState
    {
        NeedInput,
        WriteOutput,
        InvalidData,
        InvalidChecksum,
        Completed
    }
}
