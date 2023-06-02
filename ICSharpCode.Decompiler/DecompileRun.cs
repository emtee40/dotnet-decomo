﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.TypeSystem;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler
{
	internal class DecompileRun
	{
		public HashSet<string> DefinedSymbols { get; } = new HashSet<string>();
		public HashSet<string> Namespaces { get; } = new HashSet<string>();
		public CancellationToken CancellationToken { get; set; }
		public DecompilerSettings Settings { get; }
		public IDocumentationProvider DocumentationProvider { get; set; }
		public Dictionary<ITypeDefinition, RecordDecompiler> RecordDecompilers { get; } = new Dictionary<ITypeDefinition, RecordDecompiler>();

		public Dictionary<ITypeDefinition, bool> TypeHierarchyIsKnown { get; } = new();

		Lazy<CSharp.TypeSystem.UsingScope> usingScope =>
			new Lazy<CSharp.TypeSystem.UsingScope>(() => CreateUsingScope(Namespaces));

		public UsingScope UsingScope => usingScope.Value;

		public DecompilerContext Context { get; set; }

		public DecompileRun(DecompilerSettings settings)
		{
			this.Settings = settings ?? throw new ArgumentNullException(nameof(settings));
		}

		UsingScope CreateUsingScope(HashSet<string> requiredNamespacesSuperset)
		{
			var usingScope = new UsingScope();
			var copyForThreadSafety = requiredNamespacesSuperset.ToImmutableHashSet();
			foreach (var ns in copyForThreadSafety)
			{
				string[] parts = ns.Split('.');
				AstType nsType = new SimpleType(parts[0]);
				for (int i = 1; i < parts.Length; i++)
				{
					nsType = new MemberType { Target = nsType, MemberName = parts[i] };
				}

				if (nsType.ToTypeReference(CSharp.Resolver.NameLookupMode.TypeInUsingDeclaration) is TypeOrNamespaceReference reference)
					usingScope.Usings.Add(reference);
			}
			return usingScope;
		}

		public EnumValueDisplayMode? EnumValueDisplayMode { get; set; }
	}

	enum EnumValueDisplayMode
	{
		None,
		All,
		AllHex,
		FirstOnly
	}
}
