using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Filters.BuiltIn;

/// <summary>Random sequence used while generating a CxEncryption bytecode program.</summary>
public enum CxRandomFamily
{
    Standard,
    Nana,
}

/// <summary>
/// Parameter set for the common KiriKiri CxEncryption program generator.
/// The control block contains the deobfuscated 32-bit words obtained from the matching TPM plugin.
/// </summary>
public sealed record CxSchemeConfiguration(
    uint Mask,
    uint Offset,
    IReadOnlyList<byte> PrologOrder,
    IReadOnlyList<byte> OddBranchOrder,
    IReadOnlyList<byte> EvenBranchOrder,
    IReadOnlyList<uint> ControlBlock,
    CxRandomFamily RandomFamily = CxRandomFamily.Standard,
    uint? RandomSeed = null);

/// <summary>
/// Configurable implementation of KiriKiri's CxEncryption content filter. It uses entry Adler-32
/// metadata and decoded-entry logical offsets, so it is safe across XP3 segment boundaries.
/// </summary>
public sealed class CxContentFilter : IContentFilter
{
    private const int ControlBlockLength = 0x400;
    private readonly uint _mask;
    private readonly uint _offset;
    private readonly byte[] _prologOrder;
    private readonly byte[] _oddBranchOrder;
    private readonly byte[] _evenBranchOrder;
    private readonly uint[] _controlBlock;
    private readonly CxRandomFamily _randomFamily;
    private readonly uint? _randomSeed;
    private readonly CxProgram?[] _programs = new CxProgram[0x80];
    private readonly Lock _programLock = new();

    public CxContentFilter(CxSchemeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _mask = configuration.Mask;
        _offset = configuration.Offset;
        _prologOrder = CopyPermutation(configuration.PrologOrder, 3, nameof(configuration.PrologOrder));
        _oddBranchOrder = CopyPermutation(configuration.OddBranchOrder, 6, nameof(configuration.OddBranchOrder));
        _evenBranchOrder = CopyPermutation(configuration.EvenBranchOrder, 8, nameof(configuration.EvenBranchOrder));
        _controlBlock = CopyControlBlock(configuration.ControlBlock);
        _randomFamily = configuration.RandomFamily;
        _randomSeed = configuration.RandomSeed;

        if (_randomFamily == CxRandomFamily.Nana && _randomSeed is null)
        {
            throw new ArgumentException("The Nana CxEncryption variant requires a randomSeed value.", nameof(configuration));
        }
    }

    public ContentFilterDescriptor Descriptor { get; } =
        new("builtin.cx-encryption", "KiriKiri CxEncryption", "1.0");

    public ValueTask TransformAsync(
        ContentFilterContext context,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Adler32 is null)
        {
            throw new ContentFilterException(
                "CX_ADLER32_REQUIRED",
                "CxEncryption requires the entry Adler-32 value from the XP3 index.");
        }

        if (context.LogicalOffset < 0 || context.LogicalOffset > long.MaxValue - buffer.Length)
        {
            throw new ContentFilterException(
                "CX_LOGICAL_OFFSET_INVALID",
                "CxEncryption received an invalid decoded-entry logical offset.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Decrypt(context.Adler32.Value, context.LogicalOffset, buffer.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private void Decrypt(uint hash, long logicalOffset, Span<byte> buffer, CancellationToken cancellationToken)
    {
        var key = hash;
        var baseOffset = unchecked((hash & _mask) + _offset);
        if (logicalOffset < baseOffset)
        {
            var firstLength = (int)Math.Min((long)baseOffset - logicalOffset, buffer.Length);
            Decode(key, logicalOffset, buffer[..firstLength], cancellationToken);
            logicalOffset += firstLength;
            buffer = buffer[firstLength..];
        }

        if (!buffer.IsEmpty)
        {
            key = (key >> 16) ^ key;
            Decode(key, logicalOffset, buffer, cancellationToken);
        }
    }

    private void Decode(uint key, long logicalOffset, Span<byte> buffer, CancellationToken cancellationToken)
    {
        var results = ExecuteXCode(key);
        var firstSpecialOffset = results.Second >> 16;
        var secondSpecialOffset = results.Second & 0xffff;
        var repeatingKey = (byte)results.First;
        if (firstSpecialOffset == secondSpecialOffset)
        {
            secondSpecialOffset++;
        }

        if (repeatingKey == 0)
        {
            repeatingKey = 1;
        }

        var endOffset = logicalOffset + buffer.Length;
        if ((long)secondSpecialOffset >= logicalOffset && secondSpecialOffset < endOffset)
        {
            buffer[(int)(secondSpecialOffset - logicalOffset)] ^= (byte)(results.First >> 16);
        }

        if ((long)firstSpecialOffset >= logicalOffset && firstSpecialOffset < endOffset)
        {
            buffer[(int)(firstSpecialOffset - logicalOffset)] ^= (byte)(results.First >> 8);
        }

        for (var index = 0; index < buffer.Length; index++)
        {
            if ((index & 0xfff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            buffer[index] ^= repeatingKey;
        }
    }

    private (uint First, uint Second) ExecuteXCode(uint hash)
    {
        var seed = hash & 0x7f;
        var program = GetProgram(seed);
        hash >>= 7;
        return (program.Execute(hash), program.Execute(~hash));
    }

    private CxProgram GetProgram(uint seed)
    {
        lock (_programLock)
        {
            return _programs[seed] ??= GenerateProgram(seed);
        }
    }

    private CxProgram GenerateProgram(uint seed)
    {
        var program = _randomFamily == CxRandomFamily.Nana
            ? new CxProgramNana(seed, _randomSeed!.Value, _controlBlock)
            : new CxProgram(seed, _controlBlock);

        for (var stage = 5; stage > 0; stage--)
        {
            if (EmitCode(program, stage))
            {
                return program;
            }

            program.Clear();
        }

        throw new ContentFilterException("CX_PROGRAM_TOO_LARGE", "The CxEncryption parameter set generated an overly large bytecode program.");
    }

    private bool EmitCode(CxProgram program, int stage) =>
        program.EmitNop(5) &&
        program.Emit(CxOpcode.MoveEdiArgument, 4) &&
        EmitBody(program, stage) &&
        program.EmitNop(5) &&
        program.Emit(CxOpcode.Return);

    private bool EmitBody(CxProgram program, int stage)
    {
        if (stage == 1)
        {
            return EmitProlog(program);
        }

        if (!program.Emit(CxOpcode.PushEbx))
        {
            return false;
        }

        var firstSucceeded = (program.GetRandom() & 1) != 0
            ? EmitBody(program, stage - 1)
            : EmitBody2(program, stage - 1);
        if (!firstSucceeded || !program.Emit(CxOpcode.MoveEbxEax, 2))
        {
            return false;
        }

        var secondSucceeded = (program.GetRandom() & 1) != 0
            ? EmitBody(program, stage - 1)
            : EmitBody2(program, stage - 1);
        return secondSucceeded && EmitOddBranch(program) && program.Emit(CxOpcode.PopEbx);
    }

    private bool EmitBody2(CxProgram program, int stage)
    {
        if (stage == 1)
        {
            return EmitProlog(program);
        }

        var bodySucceeded = (program.GetRandom() & 1) != 0
            ? EmitBody(program, stage - 1)
            : EmitBody2(program, stage - 1);
        return bodySucceeded && EmitEvenBranch(program);
    }

    private bool EmitProlog(CxProgram program)
    {
        return _prologOrder[program.GetRandom() % 3] switch
        {
            2 => program.EmitNop(5) &&
                program.Emit(CxOpcode.MoveEaxImmediate, 2) &&
                program.EmitUInt32(program.GetRandom() & 0x3ff) &&
                program.Emit(CxOpcode.MoveEaxIndirect, 0),
            1 => program.Emit(CxOpcode.MoveEaxEdi, 2),
            0 => program.Emit(CxOpcode.MoveEaxImmediate) && program.EmitRandom(),
            _ => throw new ContentFilterException("CX_PROLOG_ORDER_INVALID", "The CxEncryption prolog order contains an invalid opcode."),
        };
    }

    private bool EmitEvenBranch(CxProgram program)
    {
        return _evenBranchOrder[program.GetRandom() & 7] switch
        {
            0 => program.Emit(CxOpcode.NotEax, 2),
            1 => program.Emit(CxOpcode.DecrementEax),
            2 => program.Emit(CxOpcode.NegateEax, 2),
            3 => program.Emit(CxOpcode.IncrementEax),
            4 => program.EmitNop(5) &&
                program.Emit(CxOpcode.AndEaxImmediate) &&
                program.EmitUInt32(0x3ff) &&
                program.Emit(CxOpcode.MoveEaxIndirect, 3),
            5 => program.Emit(CxOpcode.PushEbx) &&
                program.Emit(CxOpcode.MoveEbxEax, 2) &&
                program.Emit(CxOpcode.AndEbxImmediate, 2) &&
                program.EmitUInt32(0xaaaaaaaa) &&
                program.Emit(CxOpcode.AndEaxImmediate) &&
                program.EmitUInt32(0x55555555) &&
                program.Emit(CxOpcode.ShiftRightEbxOne, 2) &&
                program.Emit(CxOpcode.ShiftLeftEaxOne, 2) &&
                program.Emit(CxOpcode.OrEaxEbx, 2) &&
                program.Emit(CxOpcode.PopEbx),
            6 => program.Emit(CxOpcode.XorEaxImmediate) && program.EmitRandom(),
            7 => ((program.GetRandom() & 1) != 0
                    ? program.Emit(CxOpcode.AddEaxImmediate)
                    : program.Emit(CxOpcode.SubtractEaxImmediate)) &&
                program.EmitRandom(),
            _ => throw new ContentFilterException("CX_EVEN_BRANCH_ORDER_INVALID", "The CxEncryption even branch order contains an invalid opcode."),
        };
    }

    private bool EmitOddBranch(CxProgram program)
    {
        return _oddBranchOrder[program.GetRandom() % 6] switch
        {
            0 => program.Emit(CxOpcode.PushEcx) &&
                program.Emit(CxOpcode.MoveEcxEbx, 2) &&
                program.Emit(CxOpcode.AndEcxLowNibble, 3) &&
                program.Emit(CxOpcode.ShiftRightEaxCl, 2) &&
                program.Emit(CxOpcode.PopEcx),
            1 => program.Emit(CxOpcode.PushEcx) &&
                program.Emit(CxOpcode.MoveEcxEbx, 2) &&
                program.Emit(CxOpcode.AndEcxLowNibble, 3) &&
                program.Emit(CxOpcode.ShiftLeftEaxCl, 2) &&
                program.Emit(CxOpcode.PopEcx),
            2 => program.Emit(CxOpcode.AddEaxEbx, 2),
            3 => program.Emit(CxOpcode.NegateEax, 2) && program.Emit(CxOpcode.AddEaxEbx, 2),
            4 => program.Emit(CxOpcode.MultiplyEaxEbx, 3),
            5 => program.Emit(CxOpcode.SubtractEaxEbx, 2),
            _ => throw new ContentFilterException("CX_ODD_BRANCH_ORDER_INVALID", "The CxEncryption odd branch order contains an invalid opcode."),
        };
    }

    private static byte[] CopyPermutation(IReadOnlyList<byte>? values, int length, string parameterName)
    {
        if (values is null || values.Count != length)
        {
            throw new ArgumentException($"{parameterName} must contain exactly {length} values.", parameterName);
        }

        var result = values.ToArray();
        var seen = new bool[length];
        foreach (var value in result)
        {
            if (value >= length || seen[value])
            {
                throw new ArgumentException($"{parameterName} must be a permutation of values 0 through {length - 1}.", parameterName);
            }

            seen[value] = true;
        }

        return result;
    }

    private static uint[] CopyControlBlock(IReadOnlyList<uint>? values)
    {
        if (values is null || values.Count != ControlBlockLength)
        {
            throw new ArgumentException($"ControlBlock must contain exactly {ControlBlockLength} 32-bit words.", nameof(values));
        }

        return values.ToArray();
    }

    private enum CxOpcode : uint
    {
        Nop,
        Return,
        MoveEdiArgument,
        PushEbx,
        PopEbx,
        PushEcx,
        PopEcx,
        MoveEaxEbx,
        MoveEbxEax,
        MoveEcxEbx,
        MoveEaxEdi,
        MoveEaxIndirect,
        AddEaxEbx,
        SubtractEaxEbx,
        MultiplyEaxEbx,
        AndEcxLowNibble,
        ShiftRightEbxOne,
        ShiftLeftEaxOne,
        ShiftRightEaxCl,
        ShiftLeftEaxCl,
        OrEaxEbx,
        NotEax,
        NegateEax,
        DecrementEax,
        IncrementEax,

        Immediate = 0x100,
        MoveEaxImmediate,
        AndEbxImmediate,
        AndEaxImmediate,
        XorEaxImmediate,
        AddEaxImmediate,
        SubtractEaxImmediate,
    }

    private class CxProgram(uint seed, uint[] controlBlock)
    {
        private const int LengthLimit = 0x80;
        private readonly List<uint> _code = new(LengthLimit);
        private readonly uint[] _controlBlock = controlBlock;
        private int _length;
        protected uint Seed = seed;

        public uint Execute(uint hash)
        {
            var context = new CxExecutionContext();
            uint immediate = 0;
            for (var index = 0; index < _code.Count; index++)
            {
                var opcode = (CxOpcode)_code[index];
                if ((opcode & CxOpcode.Immediate) == CxOpcode.Immediate)
                {
                    if (++index >= _code.Count)
                    {
                        throw new ContentFilterException("CX_PROGRAM_INCOMPLETE", "CxEncryption generated an incomplete immediate instruction.");
                    }

                    immediate = _code[index];
                }

                switch (opcode)
                {
                    case CxOpcode.Nop:
                    case CxOpcode.Immediate:
                        break;
                    case CxOpcode.MoveEdiArgument: context.Edi = hash; break;
                    case CxOpcode.PushEbx: context.Stack.Push(context.Ebx); break;
                    case CxOpcode.PopEbx: context.Ebx = context.Stack.Pop(); break;
                    case CxOpcode.PushEcx: context.Stack.Push(context.Ecx); break;
                    case CxOpcode.PopEcx: context.Ecx = context.Stack.Pop(); break;
                    case CxOpcode.MoveEbxEax: context.Ebx = context.Eax; break;
                    case CxOpcode.MoveEaxEdi: context.Eax = context.Edi; break;
                    case CxOpcode.MoveEcxEbx: context.Ecx = context.Ebx; break;
                    case CxOpcode.MoveEaxEbx: context.Eax = context.Ebx; break;
                    case CxOpcode.AndEcxLowNibble: context.Ecx &= 0x0f; break;
                    case CxOpcode.ShiftRightEbxOne: context.Ebx >>= 1; break;
                    case CxOpcode.ShiftLeftEaxOne: context.Eax <<= 1; break;
                    case CxOpcode.ShiftRightEaxCl: context.Eax >>= (int)context.Ecx; break;
                    case CxOpcode.ShiftLeftEaxCl: context.Eax <<= (int)context.Ecx; break;
                    case CxOpcode.OrEaxEbx: context.Eax |= context.Ebx; break;
                    case CxOpcode.NotEax: context.Eax = ~context.Eax; break;
                    case CxOpcode.NegateEax: context.Eax = unchecked(0U - context.Eax); break;
                    case CxOpcode.DecrementEax: context.Eax--; break;
                    case CxOpcode.IncrementEax: context.Eax++; break;
                    case CxOpcode.AddEaxEbx: context.Eax += context.Ebx; break;
                    case CxOpcode.SubtractEaxEbx: context.Eax -= context.Ebx; break;
                    case CxOpcode.MultiplyEaxEbx: context.Eax *= context.Ebx; break;
                    case CxOpcode.AddEaxImmediate: context.Eax += immediate; break;
                    case CxOpcode.SubtractEaxImmediate: context.Eax -= immediate; break;
                    case CxOpcode.AndEbxImmediate: context.Ebx &= immediate; break;
                    case CxOpcode.AndEaxImmediate: context.Eax &= immediate; break;
                    case CxOpcode.XorEaxImmediate: context.Eax ^= immediate; break;
                    case CxOpcode.MoveEaxImmediate: context.Eax = immediate; break;
                    case CxOpcode.MoveEaxIndirect:
                        if (context.Eax >= _controlBlock.Length)
                        {
                            throw new ContentFilterException("CX_CONTROL_BLOCK_OUT_OF_RANGE", "CxEncryption attempted to read beyond its control block.");
                        }

                        context.Eax = ~_controlBlock[context.Eax];
                        break;
                    case CxOpcode.Return:
                        if (context.Stack.Count != 0)
                        {
                            throw new ContentFilterException("CX_PROGRAM_STACK_IMBALANCED", "CxEncryption generated an imbalanced stack.");
                        }

                        return context.Eax;
                    default:
                        throw new ContentFilterException("CX_PROGRAM_OPCODE_INVALID", "CxEncryption generated an invalid bytecode opcode.");
                }
            }

            throw new ContentFilterException("CX_PROGRAM_RETURN_MISSING", "CxEncryption generated a program without a return instruction.");
        }

        public void Clear()
        {
            _length = 0;
            _code.Clear();
        }

        public bool EmitNop(int length)
        {
            if (_length + length > LengthLimit)
            {
                return false;
            }

            _length += length;
            return true;
        }

        public bool Emit(CxOpcode opcode, int length = 1)
        {
            if (_length + length > LengthLimit)
            {
                return false;
            }

            _length += length;
            _code.Add((uint)opcode);
            return true;
        }

        public bool EmitUInt32(uint value)
        {
            if (_length + sizeof(uint) > LengthLimit)
            {
                return false;
            }

            _length += sizeof(uint);
            _code.Add(value);
            return true;
        }

        public bool EmitRandom() => EmitUInt32(GetRandom());

        public virtual uint GetRandom()
        {
            var previousSeed = Seed;
            Seed = unchecked((1103515245U * previousSeed) + 12345U);
            return Seed ^ (previousSeed << 16) ^ (previousSeed >> 16);
        }

        private sealed class CxExecutionContext
        {
            public uint Eax { get; set; }

            public uint Ebx { get; set; }

            public uint Ecx { get; set; }

            public uint Edi { get; set; }

            public Stack<uint> Stack { get; } = new();
        }
    }

    private sealed class CxProgramNana(uint seed, uint randomSeed, uint[] controlBlock) : CxProgram(seed, controlBlock)
    {
        private uint _randomSeed = randomSeed;

        public override uint GetRandom()
        {
            var value = Seed ^ (Seed << 17);
            value ^= (value << 18) | (value >> 15);
            Seed = ~value;
            var random = _randomSeed ^ (_randomSeed << 13);
            random ^= random >> 17;
            _randomSeed = random ^ (random << 5);
            return Seed ^ _randomSeed;
        }
    }
}
