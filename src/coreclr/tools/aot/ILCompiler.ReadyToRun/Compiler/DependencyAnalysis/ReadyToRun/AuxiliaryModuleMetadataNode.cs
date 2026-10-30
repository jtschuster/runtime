// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Internal.Text;

namespace ILCompiler.DependencyAnalysis.ReadyToRun;

public sealed class AuxiliaryModuleMetadataNode : HeaderTableNode
{
    public const int HeaderSize = 16;
    public const int EntryPointsStatusOffset = 0;

    public string OutputFilePath { get; set; }

    public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder builder)
        => builder.Append(nameMangler.CompilationUnitPrefix).Append("__AuxiliaryModuleMetadata"u8);

    public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
    {
        ObjectDataBuilder builder = new(factory, relocsOnly);
        builder.RequireInitialAlignment(4);
        builder.AddSymbol(this);
        int methodCount = 0;
        if (!relocsOnly)
        {
            foreach (MethodWithGCInfo method in factory.EnumerateCompiledMethods())
                methodCount = checked(methodCount + method.FrameInfos.Length);
        }

        builder.EmitInt(0); // EntryPointsModuleActivationStatus
        builder.EmitInt(methodCount); // PrimaryModuleMethodCount
        builder.EmitInt(0); // AuxiliaryModuleCount: method bodies remain in the primary module.
        builder.EmitReloc(this, RelocType.IMAGE_REL_BASED_ADDR32NB, HeaderSize);
        builder.EmitBytes(Encoding.UTF8.GetBytes(System.IO.Path.GetFileNameWithoutExtension(OutputFilePath) + ".entrypoints.wasm\0"));
        return builder.ToObjectData();
    }

    public override int ClassCode => 193275841;
}
