using Mono.Linker.Tests.Cases.Expectations.Assertions;

namespace Mono.Linker.Tests.Cases.Inheritance.Interfaces.DefaultInterfaceMethods
{
	[TestCaseRequirements (TestRunCharacteristics.SupportsDefaultInterfaceMethods, "Requires support for default interface methods")]
	class MostSpecificDefaultImplementationKeptStatic
	{
		[Kept]
		public static void Main ()
		{
#if SUPPORTS_DEFAULT_INTERFACE_METHODS
			M<UsedAsIBase> ();
			NotUsedInGeneric.Keep ();
			GenericType<UsedAsIBase2>.M ();
			GenericType2<UsedInUnconstrainedGeneric>.Keep ();
			Test();
#endif
		}

#if SUPPORTS_DEFAULT_INTERFACE_METHODS

		[Kept]
		static int M<T> () where T : IBase
		{
			return T.Value;
		}

		[Kept]
		interface IBase
		{
			[Kept]
			static virtual int Value {
				[Kept]
				get => 0;
			}

			static virtual int Value2 {
				get => 0;
			}
		}

		interface IMiddle : IBase
		{
			static int IBase.Value {
				get => 1;
			}

			static int IBase.Value2 {
				get => 1;
			}
		}

		[Kept]
		[KeptInterface (typeof (IBase))]
		interface IDerived : IMiddle
		{
			[Kept]
			static int IBase.Value {
				[Kept]
				get => 2;
			}

			static int IBase.Value2 {
				get => 1;
			}
		}

		[Kept]
		[KeptInterface (typeof (IBase))]
		interface IDerived2 : IMiddle
		{
			// https://github.com/dotnet/runtime/issues/97798
			// This shouldn't need to be kept. Implementor UsedInUnconstrainedGeneric is not passed as a constrained generic
			[Kept]
			static int IBase.Value {
				[Kept]
				get => 2;
			}
		}

		interface INotReferenced
		{ }

		[Kept]
		[KeptInterface (typeof (IDerived))]
		[KeptInterface (typeof (IBase))]
		class UsedAsIBase : IDerived, INotReferenced
		{
		}

		[Kept]
		class NotUsedInGeneric : IDerived, INotReferenced
		{
			[Kept]
			public static void Keep () { }
		}

		public interface IBaseUnused
		{
			public static virtual int Value {
				get => 0;
			}
		}

		public interface IMiddleUnused : IBaseUnused
		{
			static int IBaseUnused.Value {
				get => 1;
			}
		}

		public interface IDerivedUnused : IMiddleUnused
		{
			static int IBaseUnused.Value {
				get => 2;
			}
		}

		[Kept]
		[KeptInterface (typeof (IBase))]
		[KeptInterface (typeof (IDerived2))]
		class UsedInUnconstrainedGeneric : IDerived2, INotReferenced, IDerivedUnused
		{
		}


		[Kept]
		class GenericType<T> where T : IBase
		{
			[Kept]
			public static int M () => T.Value;
		}

		[Kept]
		class GenericType2<T>
		{
			[Kept]
			public static void Keep() { }
		}

		[Kept]
		[KeptInterface (typeof (IDerived))]
		[KeptInterface (typeof (IBase))]
		class UsedAsIBase2 : IDerived
		{
		}

		[Kept]
		public static void Test()
		{
			UseLevel0<MyImpl> ();
			UseLevel0<MyImpl2> ();
		}

		[Kept]
		static void UseLevel0<T> () where T : ILevel0 { T.Method(); }

		[Kept]
		interface ILevel0 { [Kept] static abstract void Method(); }
		interface ILevel1 : ILevel0 { static void ILevel0.Method() { } }
		[Kept]
		[KeptInterface(typeof(ILevel0))]
		interface ILevel1b : ILevel0 { [Kept] static void ILevel0.Method() { } }
		interface ILevel2 : ILevel1 { static void ILevel0.Method() { } }
		interface ILevel2b : ILevel1b { }
		[Kept]
		[KeptInterface(typeof(ILevel0))]
		interface ILevel3 : ILevel2 { [Kept] static void ILevel0.Method() { } }
		interface ILevel3b : ILevel2 { static void ILevel0.Method() { } }
		interface ILevel3c : ILevel2b {}
		// interface ILevel4: ILevel3 { [Kept] static void ILevel0.Method() { } }
		// interface ILevel4b: ILevel3 { [Kept] static void ILevel0.Method() { } }

		// Relies on ILevel3 DIM, nothing else
		[Kept]
		[KeptInterface(typeof(ILevel0))]
		[KeptInterface(typeof(ILevel3))]
		class MyImpl : ILevel3 { }

		// Relies on the ILevel1 DIM as the most derived
		[Kept]
		[KeptInterface(typeof(ILevel0))]
		[KeptInterface(typeof(ILevel1b))]
		class MyImpl2 : ILevel3c { }

		// Could use ILevel4 or ILevel4b DIM
		// [Kept]
		// [KeptInterface(typeof(ILevel0))]
		// [KeptInterface(typeof(ILevel4))]
		// [KeptInterface(typeof(ILevel4b))]
		// class MyImpl3 : ILevel4, ILevel4b { }

#endif
	}
}
