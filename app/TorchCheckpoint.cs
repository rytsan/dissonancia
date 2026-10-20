using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Dissonancia;

/// Reads the tensors of a PyTorch checkpoint (torch.save's zip: data.pkl + one file per storage)
/// by their state_dict key, without Python. data.pkl is unpickled by a small pickle machine that
/// understands what checkpoints use: dicts, lists, tuples, scalars, persistent storage ids and
/// torch._utils._rebuild_tensor_v2; any other class (hyperparameters, optimizer state) becomes an
/// opaque stub, since only the tensors are needed.
public sealed class TorchCheckpoint : IDisposable
{
    public sealed record Tensor(string Storage, int ElementSize, long Offset, long[] Size, long[] Stride)
    {
        public long Count => Size.Aggregate(1L, (a, b) => a * b);
    }

    readonly ZipArchive _zip;
    readonly string _prefix;
    /// The checkpoint's top-level dict (Lightning: "state_dict", "optimizer_states", ...).
    public Dictionary<object, object?> Root { get; }

    public TorchCheckpoint(string path)
    {
        _zip = ZipFile.OpenRead(path);
        var pkl = _zip.Entries.First(e => e.FullName.EndsWith("/data.pkl") || e.FullName == "data.pkl");
        _prefix = pkl.FullName[..^"data.pkl".Length];
        using var s = pkl.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Root = (Dictionary<object, object?>)new Unpickler(ms.ToArray()).Load()!;
    }

    /// The tensors of `state_dict` by key.
    public Dictionary<string, Tensor> State() =>
        ((Dictionary<object, object?>)Root["state_dict"]!).Where(kv => kv.Value is Tensor).ToDictionary(kv => (string)kv.Key, kv => (Tensor)kv.Value!);

    /// The tensor's elements in row-major order (strides applied), as raw little-endian bytes.
    public byte[] Read(Tensor t)
    {
        var entry = _zip.GetEntry(_prefix + "data/" + t.Storage) ?? throw new InvalidDataException("missing storage " + t.Storage);
        var storage = new byte[entry.Length];
        using (var s = entry.Open()) s.ReadExactly(storage);
        int es = t.ElementSize, dims = t.Size.Length;
        var dst = new byte[t.Count * es];
        var index = new long[dims];
        for (long i = 0; i < t.Count; i++)
        {
            long src = t.Offset;
            for (int d = 0; d < dims; d++) src += index[d] * t.Stride[d];
            Buffer.BlockCopy(storage, (int)(src * es), dst, (int)(i * es), es);
            for (int d = dims - 1; d >= 0 && ++index[d] == t.Size[d]; d--) index[d] = 0;
        }
        return dst;
    }

    public void Dispose() => _zip.Dispose();

    // ---------------------------------------------------------------- pickle

    sealed record Global(string Module, string Name);
    sealed record Storage(string Key, int ElementSize);
    sealed class Stub;   // any object the checkpoint holds that is not a tensor or a container

    sealed class Unpickler(byte[] data)
    {
        int _pos;
        readonly Stack<object?> _stack = new();
        readonly Stack<int> _marks = new();
        readonly Dictionary<long, object?> _memo = [];

        byte U8() => data[_pos++];
        ReadOnlySpan<byte> Take(long n) { var s = data.AsSpan(_pos, (int)n); _pos += (int)n; return s; }
        int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        string Utf8(long n) => Encoding.UTF8.GetString(Take(n));
        string Line() { int end = Array.IndexOf(data, (byte)'\n', _pos); var s = Encoding.ASCII.GetString(data, _pos, end - _pos); _pos = end + 1; return s; }
        List<object?> PopMark() { int m = _marks.Pop(); var items = new List<object?>(); while (_stack.Count > m) items.Add(_stack.Pop()); items.Reverse(); return items; }

        public object? Load()
        {
            while (true)
            {
                byte op = U8();
                switch (op)
                {
                    case 0x80: U8(); break;                                   // PROTO
                    case 0x95: Take(8); break;                                // FRAME
                    case (byte)'.': return _stack.Pop();                      // STOP
                    case (byte)'(': _marks.Push(_stack.Count); break;         // MARK
                    case (byte)'}': _stack.Push(new Dictionary<object, object?>()); break;
                    case (byte)']': _stack.Push(new List<object?>()); break;
                    case (byte)')': _stack.Push(Array.Empty<object?>()); break;
                    case 0x8f: _stack.Push(new List<object?>()); break;       // EMPTY_SET
                    case (byte)'N': _stack.Push(null); break;
                    case 0x88: _stack.Push(true); break;
                    case 0x89: _stack.Push(false); break;
                    case (byte)'J': _stack.Push((long)I32()); break;          // BININT
                    case (byte)'K': _stack.Push((long)U8()); break;           // BININT1
                    case (byte)'M': _stack.Push((long)BinaryPrimitives.ReadUInt16LittleEndian(Take(2))); break;
                    case 0x8a: _stack.Push(Long(Take(U8()))); break;          // LONG1
                    case 0x8b: _stack.Push(Long(Take(I32()))); break;         // LONG4
                    case (byte)'G': _stack.Push(BinaryPrimitives.ReadDoubleBigEndian(Take(8))); break;
                    case (byte)'X': _stack.Push(Utf8(I32())); break;          // BINUNICODE
                    case 0x8c: _stack.Push(Utf8(U8())); break;                // SHORT_BINUNICODE
                    case 0x8d: _stack.Push(Utf8(BinaryPrimitives.ReadInt64LittleEndian(Take(8)))); break;
                    case (byte)'T': _stack.Push(Encoding.Latin1.GetString(Take(I32()))); break;   // BINSTRING
                    case (byte)'U': _stack.Push(Encoding.Latin1.GetString(Take(U8()))); break;    // SHORT_BINSTRING
                    case (byte)'B': _stack.Push(Take(I32()).ToArray()); break;                    // BINBYTES
                    case (byte)'C': _stack.Push(Take(U8()).ToArray()); break;                     // SHORT_BINBYTES
                    case 0x8e: _stack.Push(Take(BinaryPrimitives.ReadInt64LittleEndian(Take(8))).ToArray()); break;
                    case (byte)'t': _stack.Push(PopMark().ToArray()); break;  // TUPLE
                    case 0x85: _stack.Push(new[] { _stack.Pop() }); break;
                    case 0x86: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(new[] { a, b }); break; }
                    case 0x87: { var c = _stack.Pop(); var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(new[] { a, b, c }); break; }
                    case (byte)'l': _stack.Push(PopMark()); break;            // LIST
                    case (byte)'d': { var items = PopMark(); var d = new Dictionary<object, object?>(); for (int i = 0; i + 1 < items.Count; i += 2) d[Key(items[i])] = items[i + 1]; _stack.Push(d); break; }
                    case 0x91: _stack.Push(PopMark()); break;                 // FROZENSET
                    case 0x90: { var items = PopMark(); if (_stack.Peek() is List<object?> l) l.AddRange(items); break; }   // ADDITEMS
                    case (byte)'a': { var v = _stack.Pop(); ((List<object?>)_stack.Peek()!).Add(v); break; }
                    case (byte)'e': { var items = PopMark(); ((List<object?>)_stack.Peek()!).AddRange(items); break; }
                    case (byte)'s': { var v = _stack.Pop(); var k = _stack.Pop(); if (_stack.Peek() is Dictionary<object, object?> d) d[Key(k)] = v; break; }
                    case (byte)'u': { var items = PopMark(); if (_stack.Peek() is Dictionary<object, object?> d) for (int i = 0; i + 1 < items.Count; i += 2) d[Key(items[i])] = items[i + 1]; break; }
                    case (byte)'q': _memo[U8()] = _stack.Peek(); break;       // BINPUT
                    case (byte)'r': _memo[I32()] = _stack.Peek(); break;      // LONG_BINPUT
                    case 0x94: _memo[_memo.Count] = _stack.Peek(); break;     // MEMOIZE
                    case (byte)'h': _stack.Push(_memo[U8()]); break;          // BINGET
                    case (byte)'j': _stack.Push(_memo[I32()]); break;         // LONG_BINGET
                    case (byte)'c': { var m = Line(); _stack.Push(new Global(m, Line())); break; }   // GLOBAL
                    case 0x93: { var n = (string)_stack.Pop()!; _stack.Push(new Global((string)_stack.Pop()!, n)); break; }   // STACK_GLOBAL
                    case (byte)'Q': _stack.Push(Persistent((object?[])_stack.Pop()!)); break;          // BINPERSID
                    case (byte)'R': { var args = (object?[])_stack.Pop()!; _stack.Push(Reduce(_stack.Pop(), args)); break; }
                    case 0x81: { _stack.Pop(); _stack.Pop(); _stack.Push(new Stub()); break; }          // NEWOBJ
                    case 0x92: { _stack.Pop(); _stack.Pop(); _stack.Pop(); _stack.Push(new Stub()); break; }   // NEWOBJ_EX
                    case (byte)'b': _stack.Pop(); break;                      // BUILD: object state, not needed
                    default: throw new InvalidDataException($"pickle opcode 0x{op:x2} at {_pos - 1} not supported");
                }
            }
        }

        static object Key(object? k) => k ?? "None";
        static long Long(ReadOnlySpan<byte> b) => b.Length == 0 ? 0 : (long)new System.Numerics.BigInteger(b, isUnsigned: false, isBigEndian: false);

        // ('storage', torch.FloatStorage, key, location, numel)
        static Storage Persistent(object?[] pid) => new((string)pid[2]!, ((Global)pid[1]!).Name switch
        {
            "DoubleStorage" or "LongStorage" => 8,
            "FloatStorage" or "IntStorage" => 4,
            "HalfStorage" or "BFloat16Storage" or "ShortStorage" => 2,
            _ => 1,
        });

        static object Reduce(object? callable, object?[] args) => callable switch
        {
            // (storage, storage_offset, size, stride, requires_grad, backward_hooks[, metadata])
            Global { Module: "torch._utils", Name: "_rebuild_tensor_v2" } when args[0] is Storage s =>
                new Tensor(s.Key, s.ElementSize, (long)args[1]!, [.. ((object?[])args[2]!).Select(v => (long)v!)], [.. ((object?[])args[3]!).Select(v => (long)v!)]),
            Global { Module: "collections", Name: "OrderedDict" } => new Dictionary<object, object?>(),
            _ => new Stub(),
        };
    }
}
