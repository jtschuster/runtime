// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mono.Linker.Tests.Cases.Expectations.Assertions;
using Mono.Linker.Tests.Cases.Expectations.Metadata;

namespace Mono.Linker.Tests.Cases.Inheritance.Interfaces.RecursiveInterfaces
{
	[SetupLinkerArgument ("--skip-unresolved", "true")]
	[TestCaseRequirements (TestRunCharacteristics.SupportsDefaultInterfaceMethods, "Requires support for default interface methods")]
	[Define ("IL_ASSEMBLY_AVAILABLE")]
	[SetupCompileBefore ("library.dll", new[] { "Dependencies/RecursiveGenericInterfaces.il" })]
	public class RecursiveGenericInterfaces
	{
		[Kept]
		public static void Main ()
		{
#if IL_ASSEMBLY_AVAILABLE
			UseIBase<char, double, float> (new MyClass ());
		}

		[Kept]
		public static void UseIBase<T, U, V> (IBase<T, U, V> myBase)
		{
			myBase.GetT ();
			myBase.GetU ();
			myBase.GetV ();
#endif
		}

		//public interface IBase<T, U, V>
		//{
		//	T GetT ();
		//	U GetU () => default;
		//	V GetV () => default;
		//}

		//public interface IMiddle<T, U> : IBase<T, U, int>, IBase<T, U, float>
		//{
		//	int IBase<T, U, int>.GetV () => 12;
		//	float IBase<T, U, float>.GetV () => 12.0f;
		//}

		//public interface IDerived<T> : IMiddle<T, long>, IMiddle<T, double>
		//{
		//	int IBase<T, long, int>.GetV () => 12;
		//	float IBase<T, long, float>.GetV () => 12.0f;

		//	long IBase<T, long, int>.GetU () => 12;
		//	long IBase<T, long, float>.GetU () => 12;

		//	double IBase<T, double, int>.GetU () => 12;
		//	double IBase<T, double, float>.GetU () => 12;
		//}

		//public class MyClass : IDerived<string>, IDerived<char>
		//{
		//	string IBase<string, long, float>.GetT () => throw new NotImplementedException ();
		//	string IBase<string, double, int>.GetT () => throw new NotImplementedException ();
		//	string IBase<string, double, float>.GetT () => throw new NotImplementedException ();
		//	string IBase<string, long, int>.GetT () => throw new NotImplementedException ();
		//	char IBase<char, long, int>.GetT () => throw new NotImplementedException ();
		//	char IBase<char, long, float>.GetT () => throw new NotImplementedException ();
		//	char IBase<char, double, int>.GetT () => throw new NotImplementedException ();
		//	char IBase<char, double, float>.GetT () => throw new NotImplementedException ();
		//}
	}
}
