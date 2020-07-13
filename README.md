# FiniteStateEntropy
C# port of Finite State Entropy codecs (https://github.com/Cyan4973/FiniteStateEntropy).

# Example
## Decompress
``` csharp
using FileStream fInput = File.OpenRead(@"C:\Data\input.dat.fse");
using FileStream fOutput = File.OpenWrite(@"C:\Data\output.dat");

using var fse = new FseStream(fInput, CompressionMode.Decompress, leaveOpen: true);

fse.CopyTo(fOutput);
```

## Compress
``` csharp
using FileStream fInput = File.OpenRead(@"C:\Data\input.dat");
using FileStream fOutput = File.OpenWrite(@"C:\Data\output.dat.fse");

using var fse = new FseStream(fOutput, CompressionMode.Compress, leaveOpen: true);

fInput.CopyTo(fse);
```
