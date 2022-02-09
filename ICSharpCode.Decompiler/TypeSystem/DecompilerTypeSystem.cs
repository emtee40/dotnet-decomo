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
using System.Threading.Tasks;

using ICSharpCode.Decompiler.Metadata;
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
		Uncached = 0x10,
		/// <summary>
		/// If this option is active, [DecimalConstantAttribute] is removed and constant values are transformed into simple decimal literals.
		/// </summary>
		DecimalConstants = 0x20,
		/// <summary>
		/// If this option is active, modopt and modreq types are preserved in the type system.
		///
		/// Note: the decompiler currently does not support handling modified types;
		/// activating this option may lead to incorrect decompilation or internal errors.
		/// </summary>
		KeepModifiers = 0x40,
		/// <summary>
		/// If this option is active, [IsReadOnlyAttribute] on parameters+structs is removed
		/// and parameters are marked as in, structs as readonly.
		/// Otherwise, the attribute is preserved but the parameters and structs are not marked.
		/// </summary>
		ReadOnlyStructsAndParameters = 0x80,
		/// <summary>
		/// If this option is active, [IsByRefLikeAttribute] is removed and structs are marked as ref.
		/// Otherwise, the attribute is preserved but the structs are not marked.
		/// </summary>
		RefStructs = 0x100,
		/// <summary>
		/// If this option is active, [IsUnmanagedAttribute] is removed from type parameters,
		/// and HasUnmanagedConstraint is set instead.
		/// </summary>
		UnmanagedConstraints = 0x200,
		/// <summary>
		/// If this option is active, [NullableAttribute] is removed and reference types with
		/// nullability annotations are used instead.
		/// </summary>
		NullabilityAnnotations = 0x400,
		/// <summary>
		/// If this option is active, [IsReadOnlyAttribute] on methods is removed
		/// and the method marked as ThisIsRefReadOnly.
		/// </summary>
		ReadOnlyMethods = 0x800,
		/// <summary>
		/// [NativeIntegerAttribute] is used to replace 'IntPtr' types with the 'nint' type.
		/// </summary>
		NativeIntegers = 0x1000,
		/// <summary>
		/// Allow function pointer types. If this option is not enabled, function pointers are
		/// replaced with the 'IntPtr' type.
		/// </summary>
		FunctionPointers = 0x2000,
		/// <summary>
		/// Default settings: typical options for the decompiler, with all C# languages features enabled.
		/// </summary>
		Default = Dynamic | Tuple | ExtensionMethods | DecimalConstants | ReadOnlyStructsAndParameters
			| RefStructs | UnmanagedConstraints | NullabilityAnnotations | ReadOnlyMethods
			| NativeIntegers | FunctionPointers
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
			if (settings.IntroduceRefModifiersOnStructs)
				typeSystemOptions |= TypeSystemOptions.RefStructs;
			if (settings.IntroduceReadonlyAndInModifiers)
				typeSystemOptions |= TypeSystemOptions.ReadOnlyStructsAndParameters;
			if (settings.IntroduceUnmanagedConstraint)
				typeSystemOptions |= TypeSystemOptions.UnmanagedConstraints;
			if (settings.NullableReferenceTypes)
				typeSystemOptions |= TypeSystemOptions.NullabilityAnnotations;
			if (settings.ReadOnlyMethods)
				typeSystemOptions |= TypeSystemOptions.ReadOnlyMethods;
			if (settings.NativeIntegers)
				typeSystemOptions |= TypeSystemOptions.NativeIntegers;
			if (settings.FunctionPointers)
				typeSystemOptions |= TypeSystemOptions.FunctionPointers;
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
			var referencedAssemblies = new List<PEFile>();
			var assemblyReferenceQueue = new Queue<IAssembly>(moduleDefinition.GetAssemblyRefs());
			var processedAssemblyReferences = new HashSet<IAssembly>(AssemblyNameComparer.CompareAll);
			while (assemblyReferenceQueue.Count > 0) {
				var asmRef = assemblyReferenceQueue.Dequeue();
				if (!processedAssemblyReferences.Add(asmRef))
					continue;
				var asm = moduleDefinition.Context.AssemblyResolver.Resolve(asmRef, moduleDefinition);
				if (asm != null) {
					referencedAssemblies.Add(new PEFile(asm.ManifestModule));
					foreach (var forwarder in asm.ManifestModule.ExportedTypes) {
						if (!forwarder.IsForwarder || !(forwarder.Scope is IAssembly forwarderRef)) continue;
						assemblyReferenceQueue.Enqueue(forwarderRef);
					}
				}
			}
			var mainModuleWithOptions = mainModule.WithOptions(typeSystemOptions);
			var referencedAssembliesWithOptions = referencedAssemblies.Select(file => file.WithOptions(typeSystemOptions));
			// Primitive types are necessary to avoid assertions in ILReader.
			// Other known types are necessary in order for transforms to work (e.g. Task<T> for async transform).
			// Figure out which known types are missing from our type system so far:
			var missingKnownTypes = KnownTypeReference.AllKnownTypes.Where(IsMissing).ToList();
			if (missingKnownTypes.Count > 0) {
				Init(mainModule.WithOptions(typeSystemOptions), referencedAssembliesWithOptions.Concat(new[] { MinimalCorlib.CreateWithTypes(missingKnownTypes) }));
			} else {
				Init(mainModuleWithOptions, referencedAssembliesWithOptions);
			}
			this.MainModule = (MetadataModule)base.MainModule;

			bool IsMissing(KnownTypeReference knownType)
			{
				var name = knownType.TypeName;
				if (mainModule.GetTypeDefinition(name) != null)
					return false;
				foreach (var file in referencedAssemblies) {
					if (file.GetTypeDefinition(name) != null)
						return false;
				}
				return true;
			}
		}

		public new MetadataModule MainModule { get; }
	}
}
