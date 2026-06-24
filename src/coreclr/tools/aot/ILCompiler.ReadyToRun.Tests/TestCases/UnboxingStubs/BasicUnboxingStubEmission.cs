// Test: Basic unboxing-stub emission in R2R
// Validates that crossgen2 precompiles unboxing stubs ([UNBOX] entries) for non-generic
// value-type virtual/interface instance methods and for shared-generic (__Canon) value types,
// and that it does NOT emit them for the out-of-scope cases (reference types, static methods,
// exact-instantiated generic value types, generic methods).
using System;

public interface IUnboxValue
{
    int InterfaceGet();
}

public interface IUnboxExplicit
{
    int ExplicitGet();
}

// A struct large enough to be returned via a hidden return buffer on the major ABIs,
// to exercise the retbuf-vs-`this` register ordering in the unboxing stub.
public struct BigUnboxResult
{
    public long A;
    public long B;
    public long C;
    public long D;
}

public interface IBigReturner
{
    BigUnboxResult GetBig();
}

// --- Positives: non-generic value types whose virtual/interface instance methods must get [UNBOX] ---

public struct VirtualOverrideStruct
{
    public int Field;
    public override string ToString() => "VOS:" + Field;
    public override int GetHashCode() => Field;
    public override bool Equals(object obj) => obj is VirtualOverrideStruct other && other.Field == Field;
}

public struct ImplicitInterfaceStruct : IUnboxValue
{
    public int Field;
    public int InterfaceGet() => Field * 2;
}

public struct ExplicitInterfaceStruct : IUnboxExplicit
{
    public int Field;
    int IUnboxExplicit.ExplicitGet() => Field * 3;
}

public struct BigReturningStruct : IBigReturner
{
    public long Seed;
    public BigUnboxResult GetBig() => new BigUnboxResult { A = Seed, B = Seed + 1, C = Seed + 2, D = Seed + 3 };
}

// --- Negatives: must NOT get [UNBOX] ---

// Reference type: never needs an unboxing stub.
public class ReferenceTypeControl
{
    public int Field;
    public virtual int RefVirtualMethod() => Field;
}

// Static method on a value type: no `this`, so no unboxing stub.
public struct StaticMethodControl
{
    public int Field;
    public static int StaticControlMethod(int x) => x + 1;
}

// Boundary case: a generic value type whose only hard instantiation is over a *value* type (int).
// The exact GenericValueControl`1<int> instantiation is not shared, so crossgen2 does not precompile
// a stub for it (the runtime synthesizes one on demand). crossgen2 still compiles the canonical
// GenericValueControl`1<__Canon> body, which DOES get a precompiled shared-generic unboxing stub
// (Phase 2). This pins down the exact-vs-canonical boundary.
public struct GenericValueControl<T> : IUnboxValue
{
    public int Field;
    public int InterfaceGet() => Field;
}

// Out-of-scope: generic method on a non-generic value type.
public struct GenericMethodControl
{
    public int Field;
    public U GenericControlMethod<U>(U x) => x;
}

public interface IProduceValue<T>
{
    T ProduceValue();
}

// Shared-generic positive (Phase 2): a generic value type instantiated over a *reference* type
// compiles to the canonical __Canon form, so crossgen2 precompiles a shared-generic unboxing stub
// for its interface instance method.
public struct SharedGenericStruct<T> : IProduceValue<T>
{
    public T Field;
    public T ProduceValue() => Field;
}

// Driver: forces crossgen2 to compile the boxed/interface dispatch paths and the generic
// instantiations, so the negative assertions are meaningful (the instantiations are actually
// compiled, yet still must not produce [UNBOX] entries).
public static class UnboxDriver
{
    public static long Force()
    {
        long acc = 0;

        object boxedVirtual = new VirtualOverrideStruct { Field = 1 };
        acc += boxedVirtual.ToString().Length + boxedVirtual.GetHashCode();
        acc += boxedVirtual.Equals(new VirtualOverrideStruct { Field = 1 }) ? 1 : 0;

        IUnboxValue implicitIface = new ImplicitInterfaceStruct { Field = 2 };
        acc += implicitIface.InterfaceGet();

        IUnboxExplicit explicitIface = new ExplicitInterfaceStruct { Field = 3 };
        acc += explicitIface.ExplicitGet();

        IBigReturner bigReturner = new BigReturningStruct { Seed = 4 };
        acc += bigReturner.GetBig().A;

        // Out-of-scope generic instantiations (compiled, but must not be precompiled as unboxing stubs).
        IUnboxValue genericValue = new GenericValueControl<int> { Field = 5 };
        acc += genericValue.InterfaceGet();

        var genericMethodHolder = new GenericMethodControl { Field = 6 };
        acc += genericMethodHolder.GenericControlMethod<int>(7);

        // Shared-generic positive: a reference-type arg makes this the canonical __Canon form, which
        // gets a precompiled shared-generic unboxing stub.
        IProduceValue<string> sharedGeneric = new SharedGenericStruct<string> { Field = "shared" };
        acc += sharedGeneric.ProduceValue().Length;

        var refControl = new ReferenceTypeControl { Field = 8 };
        acc += refControl.RefVirtualMethod();
        acc += StaticMethodControl.StaticControlMethod(9);

        return acc;
    }
}
