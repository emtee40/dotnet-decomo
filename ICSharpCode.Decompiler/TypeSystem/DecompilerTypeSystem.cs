﻿// Copyright (c) 2018 Daniel Grunwald
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using ICSharpCode.Decompiler.Util;
using dnlib.DotNet;

namespace ICSharpCode.Decompiler.TypeSystem
{
	/// <summary>
	/// Options that control how metadata is represented in the type system.
	/// </summary>
	[Flags]
	public enum TypeSystemOptions
	{
		/// <summary>
		/// No options enabled; stay as close to the metadata as possible.
		/// </summary>
		None = 0,
		/// <summary>
		/// [DynamicAttribute] is used to replace 'object' types with the 'dynamic' type.
		///
		/// If this option is not active, the 'dynamic' type is not used, and the attribute is preserved.
		/// </summary>
		Dynamic = 1,
		/// <summary>
		/// Tuple types are represented using the TupleType class.
		/// [TupleElementNames] is used to name the tuple elements.
		///
		/// If this option is not active, the tuples are represented using their underlying type, and the attribute is preserved.
		/// </summary>
		Tuple = 2,
		/// <summary>
		/// If this option is active, [ExtensionAttribute] is removed and methods are marked as IsExtensionMethod.
		/// Otherwise, the attribute is preserved but the methods are not marked.
		/// </summary>
		ExtensionMethods = 4,
		/// <summary>
		/// Only load the public API into the type system.
		/// </summary>
		OnlyPublicAPI = 8,
		/// <summary>
		/// Do not cache accessed entities.
		/// In a normal type system (without this option), every type or member definition has exactly one ITypeDefinition/IMember
		/// instance. This instance is kept alive until the whole type system can be garbage-collected.
		/// When this option is specified, the type system avoids these caches.
		/// This reduces the memory usage in many cases, but increases the number of allocations.
		/// Also, some code in the decompiler expects to be able to compare type/member definitions by reference equality,
		/// and thus will fail with uncached type systems.
		/// </summary>
		Uncached = 16,
		/// <summary>
		/// If this option is active, [DecimalConstantAttribute] is removed and constant values are transformed into simple decimal literals.
		/// </summary>
		DecimalConstants = 32,
		/// <summary>
		/// Default settings: all features enabled.
		/// </summary>
		Default = Dynamic | Tuple | ExtensionMethods | DecimalConstants
	}

	/// <summary>
	/// Manages the NRefactory type system for the decompiler.
	/// </summary>
	/// <remarks>
	/// This class is thread-safe.
	/// </remarks>
	public class DecompilerTypeSystem : SimpleCompilation, IDecompilerTypeSystem
	{
		public static TypeSystemOptions GetOptions(DecompilerSettings settings)
		{
			var typeSystemOptions = TypeSystemOptions.None;
			if (settings.Dynamic)
				typeSystemOptions |= TypeSystemOptions.Dynamic;
			if (settings.TupleTypes)
				typeSystemOptions |= TypeSystemOptions.Tuple;
			if (settings.ExtensionMethods)
				typeSystemOptions |= TypeSystemOptions.ExtensionMethods;
			if (settings.DecimalConstants)
				typeSystemOptions |= TypeSystemOptions.DecimalConstants;
			return typeSystemOptions;
		}

		public DecompilerTypeSystem(PEFile mainModule)
			: this(mainModule, TypeSystemOptions.Default)
		{
		}

		public DecompilerTypeSystem(PEFile mainModule, DecompilerSettings settings)
			: this(mainModule, GetOptions(settings ?? throw new ArgumentNullException(nameof(settings))))
		{
		}

		public DecompilerTypeSystem(PEFile mainModule, TypeSystemOptions typeSystemOptions)
		{
			if (mainModule == null)
				throw new ArgumentNullException(nameof(mainModule));
			// Load referenced assemblies and type-forwarder references.
			// This is necessary to make .NET Core/PCL binaries work better.
			var moduleDefinition = mainModule.Module;
			// Load referenced assemblies and type-forwarder references.
			// This is necessary to make .NET Core/PCL binaries work better.
			var referencedAssemblies = new List<PEFile>();
			var assemblyReferenceQueue = new Queue<dnlib.DotNet.IAssembly>(moduleDefinition.GetAssemblyRefs());
			var processedAssemblyReferences = new HashSet<dnlib.DotNet.IAssembly>(KeyComparer.Create((dnlib.DotNet.IAssembly reference) => reference.FullName));
			while (assemblyReferenceQueue.Count > 0) {
				var asmRef = assemblyReferenceQueue.Dequeue();
				if (!processedAssemblyReferences.Add(asmRef))
					continue;
				var asm = moduleDefinition.Context.AssemblyResolver.Resolve(asmRef, moduleDefinition);
				if (asm != null) {
					referencedAssemblies.Add(new PEFile(asm.ManifestModule));
					foreach (var forwarder in asm.ManifestModule.ExportedTypes) {
						if (!forwarder.IsForwarder || !(forwarder.Scope is dnlib.DotNet.IAssembly forwarderRef)) continue;
						assemblyReferenceQueue.Enqueue(forwarderRef);
					}
				}
			}
			var mainModuleWithOptions = mainModule.WithOptions(typeSystemOptions);
			var referencedAssembliesWithOptions = referencedAssemblies.Select(file => file.WithOptions(typeSystemOptions));
			// Primitive types are necessary to avoid assertions in ILReader.
			// Fallback to MinimalCorlib to provide the primitive types.
			if (!HasType(KnownTypeCode.Void) || !HasType(KnownTypeCode.Int32)) {
				Init(mainModule.WithOptions(typeSystemOptions), referencedAssembliesWithOptions.Concat(new[] { MinimalCorlib.Instance }));
			} else {
				Init(mainModuleWithOptions, referencedAssembliesWithOptions);
			}
			this.MainModule = (MetadataModule)base.MainModule;

			bool HasType(KnownTypeCode code)
			{
				TopLevelTypeName name = KnownTypeReference.Get(code).TypeName;
				if (mainModule.GetTypeDefinition(name) != null)
					return true;
				foreach (var file in referencedAssemblies) {
					if (file.GetTypeDefinition(name) != null)
						return true;
				}
				return false;
			}
		}

		public new MetadataModule MainModule { get; }
	}
}
