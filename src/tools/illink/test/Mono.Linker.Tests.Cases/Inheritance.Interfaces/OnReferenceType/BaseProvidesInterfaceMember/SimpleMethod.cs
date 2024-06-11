using Mono.Linker.Tests.Cases.Expectations.Assertions;

namespace Mono.Linker.Tests.Cases.Inheritance.Interfaces.OnReferenceType.BaseProvidesInterfaceMember
{
	public class SimpleMethod
	{
		public static void Main ()
		{
			IFoo f = new FooWithBase ();
			f.Method ();
			f = new FooWithBase2 ();
			f.Method ();
			f = new FooWithImpl ();
		}

		[Kept]
		interface IFoo
		{
			[Kept]
			void Method ();
		}

		[Kept]
		[KeptMember (".ctor()")]
		class BaseFoo
		{
			[Kept]
			public void Method ()
			{
			}
		}

		[Kept]
		[KeptMember (".ctor()")]
		[KeptBaseType (typeof (BaseFoo))]
		[KeptInterface (typeof (IFoo))]
		class FooWithBase : BaseFoo, IFoo
		{
		}

		[Kept]
		[KeptMember (".ctor()")]
		class BaseFoo2
		{
			[Kept] // Should not be kept: https://github.com/dotnet/runtime/issues/103316
			public virtual void Method ()
			{
			}
		}

		[Kept]
		[KeptMember (".ctor()")]
		[KeptBaseType (typeof (BaseFoo2))]
		[KeptInterface (typeof (IFoo))]
		class FooWithBase2 : BaseFoo2, IFoo
		{
			[Kept]
			void IFoo.Method () { }
		}

		[Kept]
		[KeptInterface (typeof (IFoo))]
		[KeptMember (".ctor()")]
		class FooWithImpl : IFoo
		{
			[Kept]
			void IFoo.Method () { }
			[Kept] // Should not be kept: https://github.com/dotnet/runtime/issues/103316
			public virtual void Method () { }
		}
	}
}
