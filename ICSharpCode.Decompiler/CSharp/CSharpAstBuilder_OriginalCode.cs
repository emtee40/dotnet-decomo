using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using dnlib.DotNet;

using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.ControlFlow;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.CSharp
{
	public partial class CSharpAstBuilder
	{
		public static bool MemberIsHidden(IMDTokenProvider member, DecompilerSettings settings)
		{
			MethodDef method = member as MethodDef;
			if (method != null) {
				if (method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn)
					return true;
				if (method.Name == ".ctor" && method.RVA == 0 && method.DeclaringType.IsImport)
					return true;
				if (settings.ForceShowAllMembers)
					return false;
				if (settings.LocalFunctions && LocalFunctionDecompiler.IsLocalFunctionMethod(null, method))
					return true;
				if (settings.AnonymousMethods && method.HasGeneratedName() && method.IsCompilerGenerated())
					return true;
				if (settings.AsyncAwait && AsyncAwaitDecompiler.IsCompilerGeneratedMainMethod(method))
					return true;
			}

			TypeDef type = member as TypeDef;
			if (type != null) {
				if (settings.ForceShowAllMembers)
					return false;
				if (type.DeclaringType != null) {
					if (settings.LocalFunctions && LocalFunctionDecompiler.IsLocalFunctionDisplayClass(null, type))
						return true;
					if (settings.AnonymousMethods && IsClosureType(type))
						return true;
					if (settings.YieldReturn && YieldReturnDecompiler.IsCompilerGeneratorEnumerator(type))
						return true;
					if (settings.AsyncAwait && AsyncAwaitDecompiler.IsCompilerGeneratedStateMachine(type))
						return true;
					if (settings.AsyncEnumerator && AsyncAwaitDecompiler.IsCompilerGeneratorAsyncEnumerator(type))
						return true;
					if (settings.FixedBuffers && type.Name.StartsWith("<", StringComparison.Ordinal) && type.Name.Contains("__FixedBuffer"))
						return true;
				} else if (type.IsCompilerGenerated()) {
					if (settings.ArrayInitializers && type.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal))
						return true;
					if (settings.AnonymousTypes && type.IsAnonymousType())
						return true;
					if (settings.Dynamic && type.IsDelegate && (type.Name.StartsWith("<>A", StringComparison.Ordinal) || type.Name.StartsWith("<>F", StringComparison.Ordinal)))
						return true;
				}
				if (settings.ArrayInitializers && settings.SwitchStatementOnString && type.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal))
					return true;
			}

			FieldDef field = member as FieldDef;
			if (field != null) {
				if (settings.ForceShowAllMembers)
					return false;
				if (field.IsCompilerGenerated()) {
					if (settings.AnonymousMethods && IsAnonymousMethodCacheField(field))
						return true;
					if (settings.AutomaticProperties && IsAutomaticPropertyBackingField(field, out var propertyName))
					{
						if (!settings.GetterOnlyAutomaticProperties && IsGetterOnlyProperty(propertyName))
							return false;

						bool IsGetterOnlyProperty(string propertyName)
						{
							var properties = field.DeclaringType.Properties;
							foreach (var pd in properties)
							{
								if (pd.Name.String != propertyName)
									continue;
								return pd.GetMethod is not null && pd.SetMethod is null;
							}
							return false;
						}

						return true;
					}
					if (settings.SwitchStatementOnString && IsSwitchOnStringCache(field))
						return true;
				}
				// event-fields are not [CompilerGenerated]
				if (settings.AutomaticEvents && field.DeclaringType.Events.Any(ev => IsEventBackingFieldName(field.Name, ev.Name)))
					return true;
				if (settings.ArrayInitializers && field.DeclaringType.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)) {
					// hide fields starting with '__StaticArrayInit'
					if (field.Name.StartsWith("__StaticArrayInit", StringComparison.Ordinal))
						return true;
					if (field.FieldType.TypeName.StartsWith("__StaticArrayInit", StringComparison.Ordinal))
						return true;
					// hide fields starting with '$$method'
					if (field.Name.StartsWith("$$method", StringComparison.Ordinal))
						return true;
				}
			}

			return false;
		}

		internal static bool IsEventBackingFieldName(string fieldName, string eventName) {
			if (fieldName == eventName)
				return true;

			const string VB_PATTERN = "Event";
			return fieldName.Length == VB_PATTERN.Length + eventName.Length && fieldName.StartsWith(eventName, StringComparison.Ordinal) && fieldName.EndsWith(VB_PATTERN, StringComparison.Ordinal);
		}

		internal static bool IsEventBackingFieldName(string fieldName, string eventName, out int suffixLength)
		{
			suffixLength = 0;
			if (fieldName == eventName)
				return true;
			var vbSuffixLength = "Event".Length;
			if (fieldName.Length == eventName.Length + vbSuffixLength && fieldName.StartsWith(eventName, StringComparison.Ordinal) && fieldName.EndsWith("Event", StringComparison.Ordinal))
			{
				suffixLength = vbSuffixLength;
				return true;
			}
			return false;
		}

		static bool IsSwitchOnStringCache(FieldDef field)
		{
			return field.Name.StartsWith("<>f__switch", StringComparison.Ordinal);
		}

		static readonly Regex automaticPropertyBackingFieldRegex = new Regex(@"^<(.*)>k__BackingField$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		static bool IsAutomaticPropertyBackingField(FieldDef field, out string propertyName)
		{
			propertyName = null;
			var name = field.Name;
			var m = automaticPropertyBackingFieldRegex.Match(name);
			if (m.Success)
			{
				propertyName = m.Groups[1].Value;
				return true;
			}
			if (name.StartsWith("_", StringComparison.Ordinal))
			{
				propertyName = name.Substring(1);
				return field.IsCompilerGenerated();
			}
			return false;
		}

		static bool IsAnonymousMethodCacheField(FieldDef field)
		{
			return field.Name.StartsWith("CS$<>", StringComparison.Ordinal) || field.Name.StartsWith("<>f__am", StringComparison.Ordinal) || field.Name.StartsWith("<>f__mg", StringComparison.Ordinal);
		}

		static bool IsClosureType(TypeDef type)
		{
			if (!type.IsCompilerGenerated())
				return false;
			if (type.Name.StartsWith("_Closure$__"))
				return true;
			if (!type.HasGeneratedName())
				return false;
			if (type.Name.Contains("DisplayClass") || type.Name.Contains("AnonStorey")|| type.Name.Contains("Closure$"))
				return true;
			return type.BaseType.FullName == "System.Object" && !type.HasInterfaces;
		}

		void SetNewModifier(EntityDeclaration member)
		{
			var entity = (IEntity)member.GetSymbol();
			var lookup = new MemberLookup(entity.DeclaringTypeDefinition, entity.ParentModule);

			var baseTypes = entity.DeclaringType.GetNonInterfaceBaseTypes().Where(t => !entity.DeclaringType!.Equals(t)).ToList();

			// A constant, field, property, event, or type introduced in a class or struct hides all base class members with the same name.
			bool hideBasedOnSignature = !(entity is ITypeDefinition
				|| entity.SymbolKind == SymbolKind.Field
				|| entity.SymbolKind == SymbolKind.Property
				|| entity.SymbolKind == SymbolKind.Event);

			const GetMemberOptions options = GetMemberOptions.IgnoreInheritedMembers | GetMemberOptions.ReturnMemberDefinitions;

			if (HidesMemberOrTypeOfBaseType())
				member.Modifiers |= Modifiers.New;

			bool HidesMemberOrTypeOfBaseType()
			{
				var parameterListComparer = ParameterListComparer.WithOptions(includeModifiers: true);

				foreach (Decompiler.TypeSystem.IType baseType in baseTypes) {
					if (!hideBasedOnSignature) {
						if (baseType.GetNestedTypes(t => t.Name == entity.Name && lookup.IsAccessible(t, true), options).Any())
							return true;
						if (baseType.GetMembers(m => m.Name == entity.Name && m.SymbolKind != SymbolKind.Indexer && lookup.IsAccessible(m, true), options).Any())
							return true;
					} else {
						if (entity.SymbolKind == SymbolKind.Indexer) {
							// An indexer introduced in a class or struct hides all base class indexers with the same signature (parameter count and types).
							if (baseType.GetProperties(p => p.SymbolKind == SymbolKind.Indexer && lookup.IsAccessible(p, true))
									.Any(p => parameterListComparer.Equals(((IProperty)entity).Parameters, p.Parameters)))
							{
								return true;
							}
						} else if (entity.SymbolKind == SymbolKind.Method) {
							// A method introduced in a class or struct hides all non-method base class members with the same name, and all
							// base class methods with the same signature (method name and parameter count, modifiers, and types).
							if (baseType.GetMembers(m => m.SymbolKind != SymbolKind.Indexer
														 && m.SymbolKind != SymbolKind.Constructor
														 && m.SymbolKind != SymbolKind.Destructor
														 && m.Name == entity.Name && lookup.IsAccessible(m, true))
										.Any(m => m.SymbolKind != SymbolKind.Method ||
												  (((ICSharpCode.Decompiler.TypeSystem.IMethod)entity).TypeParameters.Count == ((ICSharpCode.Decompiler.TypeSystem.IMethod)m).TypeParameters.Count
												   && parameterListComparer.Equals(((ICSharpCode.Decompiler.TypeSystem.IMethod)entity).Parameters, ((ICSharpCode.Decompiler.TypeSystem.IMethod)m).Parameters))))
							{
								return true;
							}
						}
					}
				}

				return false;
			}
		}

		EnumValueDisplayMode DetectBestEnumValueDisplayMode(ITypeDefinition typeDef)
		{
			if (typeDef.HasAttribute(KnownAttribute.Flags))
				return EnumValueDisplayMode.AllHex;
			bool first = true;
			long firstValue = 0, previousValue = 0;
			bool allPowersOfTwo = true;
			bool allConsecutive = true;
			foreach (var field in typeDef.Fields)
			{
				if (MemberIsHidden(field.MetadataToken, context.Settings))
					continue;
				object constantValue = field.GetConstantValue();
				if (constantValue == null)
					continue;
				long currentValue = (long)CSharpPrimitiveCast.Cast(TypeCode.Int64, constantValue, false);
				allConsecutive = allConsecutive && (first || previousValue + 1 == currentValue);
				// N & (N - 1) == 0, iff N is a power of 2, for all N != 0.
				// We define that 0 is a power of 2 in the context of enum values.
				allPowersOfTwo = allPowersOfTwo && unchecked(currentValue & (currentValue - 1)) == 0;
				if (first)
				{
					firstValue = currentValue;
					first = false;
				}
				else if (currentValue <= previousValue)
				{
					// If the values are out of order, we fallback to displaying all values.
					return EnumValueDisplayMode.All;
				}
				else if (!allConsecutive && !allPowersOfTwo)
				{
					// We already know that the values are neither consecutive nor all powers of 2,
					// so we can abort, and just display all values as-is.
					return EnumValueDisplayMode.All;
				}
				previousValue = currentValue;
			}
			if (allPowersOfTwo)
			{
				if (previousValue > 8)
				{
					// If all values are powers of 2 and greater 8, display all enum values, but use hex.
					return EnumValueDisplayMode.AllHex;
				}
				else if (!allConsecutive)
				{
					// If all values are powers of 2, display all enum values.
					return EnumValueDisplayMode.All;
				}
			}
			if (context.Settings.AlwaysShowEnumMemberValues)
			{
				// The user always wants to see all enum values, but we know hex is not necessary.
				return EnumValueDisplayMode.All;
			}
			// We know that all values are consecutive, so if the first value is not 0
			// display the first enum value only.
			return firstValue == 0 ? EnumValueDisplayMode.None : EnumValueDisplayMode.FirstOnly;
		}

		void FixParameterNames(EntityDeclaration entity)
		{
			int i = 0;
			foreach (var parameter in entity.GetChildrenByRole(Roles.Parameter))
			{
				if (string.IsNullOrEmpty(parameter.Name) && !parameter.Type.IsArgList())
				{
					// needs to be consistent with logic in ILReader.CreateILVarable(ParameterDefinition)
					parameter.NameToken.Name = "P_" + i;
				}
				i++;
			}
		}

		private bool IsCovariantReturnOverride(IEntity entity)
		{
			if (!context.Settings.CovariantReturns)
				return false;
			if (!entity.HasAttribute(KnownAttribute.PreserveBaseOverrides))
				return false;
			return true;
		}

		internal static bool RemoveAttribute(EntityDeclaration entityDecl, KnownAttribute attributeType)
		{
			bool found = false;
			foreach (var section in entityDecl.Attributes)
			{
				foreach (var attr in section.Attributes)
				{
					var symbol = attr.Type.GetSymbol();
					if (symbol is ITypeDefinition td && td.FullTypeName == attributeType.GetTypeName()) {
						attr.Remove();
						found = true;
					}
				}
				if (section.Attributes.Count == 0)
				{
					section.Remove();
				}
			}
			return found;
		}

		internal static bool RemoveCompilerFeatureRequiredAttribute(EntityDeclaration entityDecl, string feature)
		{
			bool found = false;
			foreach (var section in entityDecl.Attributes)
			{
				foreach (var attr in section.Attributes)
				{
					var symbol = attr.Type.GetSymbol();
					if (symbol is ITypeDefinition td && td.FullTypeName == KnownAttribute.CompilerFeatureRequired.GetTypeName()
													 && attr.Arguments.Count == 1 && attr.Arguments.SingleOrDefault() is PrimitiveExpression pe
													 && pe.Value is string s && s == feature)
					{
						attr.Remove();
						found = true;
					}
				}
				if (section.Attributes.Count == 0)
				{
					section.Remove();
				}
			}
			return found;
		}

		internal static bool RemoveObsoleteAttribute(EntityDeclaration entityDecl, string message)
		{
			bool found = false;
			foreach (var section in entityDecl.Attributes)
			{
				foreach (var attr in section.Attributes)
				{
					var symbol = attr.Type.GetSymbol();
					if (symbol is ITypeDefinition td && td.FullTypeName == KnownAttribute.Obsolete.GetTypeName()
													 && attr.Arguments.Count >= 1 && attr.Arguments.First() is PrimitiveExpression pe
													 && pe.Value is string s && s == message)
					{
						attr.Remove();
						found = true;
					}
				}
				if (section.Attributes.Count == 0)
				{
					section.Remove();
				}
			}
			return found;
		}

		bool FindAttribute(EntityDeclaration entityDecl, KnownAttribute attributeType, out Syntax.Attribute attribute)
		{
			attribute = null;
			foreach (var section in entityDecl.Attributes)
			{
				foreach (var attr in section.Attributes)
				{
					var symbol = attr.Type.GetSymbol();
					if (symbol is ITypeDefinition td && td.FullTypeName == attributeType.GetTypeName()) {
						attribute = attr;
						return true;
					}
				}
			}
			return false;
		}

		IEnumerable<EntityDeclaration> AddInterfaceImplHelpers(EntityDeclaration memberDecl, ICSharpCode.Decompiler.TypeSystem.IMethod method, TypeSystemAstBuilder astBuilder)
		{
			if (!memberDecl.GetChildByRole(EntityDeclaration.PrivateImplementationTypeRole).IsNull)
			{
				yield break; // cannot create forwarder for existing explicit interface impl
			}
			if (method.IsStatic)
			{
				yield break; // cannot create forwarder for static interface impl
			}
			if (memberDecl.HasModifier(Modifiers.Extern))
			{
				yield break; // cannot create forwarder for extern method
			}
			var genericContext = new Decompiler.TypeSystem.GenericContext(method);
			var methodHandle = (MethodDef)method.MetadataToken;
			foreach (var h in methodHandle.Overrides) {
				ICSharpCode.Decompiler.TypeSystem.IMethod m = typeSystem.MainModule.ResolveMethod(h.MethodDeclaration, genericContext);
				if (m == null || m.DeclaringType.Kind != TypeKind.Interface)
					continue;
				var methodDecl = new MethodDeclaration();
				methodDecl.ReturnType = memberDecl.ReturnType.Clone();
				methodDecl.PrivateImplementationType = astBuilder.ConvertType(m.DeclaringType);
				methodDecl.Name = m.Name;
				methodDecl.TypeParameters.AddRange(memberDecl.GetChildrenByRole(Roles.TypeParameter)
												   .Select(n => (TypeParameterDeclaration)n.Clone()));
				methodDecl.Parameters.AddRange(memberDecl.GetChildrenByRole(Roles.Parameter).Select(n => n.Clone()));
				methodDecl.Constraints.AddRange(memberDecl.GetChildrenByRole(Roles.Constraint)
												.Select(n => (Constraint)n.Clone()));

				methodDecl.Body = new BlockStatement();
				methodDecl.Body.AddChild(new Comment(
					"ILSpy generated this explicit interface implementation from .override directive in " + memberDecl.Name),
					Roles.Comment);

				var member = new MemberReferenceExpression {
					Target = new ThisReferenceExpression().WithAnnotation(methodHandle.DeclaringType),
					MemberNameToken = Identifier.Create(memberDecl.Name).WithAnnotation(method.OriginalMember)
				}.WithAnnotation(method.OriginalMember);
				member.TypeArguments.AddRange(methodDecl.TypeParameters.Select(tp => new SimpleType(tp.Name)));

				var forwardingCall = new InvocationExpression(member,
					methodDecl.Parameters.Select(ForwardParameter)
				).WithAnnotation(method.OriginalMember);
				if (m.ReturnType.IsKnownType(KnownTypeCode.Void))
				{
					methodDecl.Body.Add(new ExpressionStatement(forwardingCall));
				}
				else
				{
					methodDecl.Body.Add(new ReturnStatement(forwardingCall));
				}
				yield return methodDecl;
			}
		}

		Expression ForwardParameter(ParameterDeclaration p)
		{
			switch (p.ParameterModifier)
			{
				case ParameterModifier.Ref:
					return new DirectionExpression(FieldDirection.Ref, new IdentifierExpression(p.Name));
				case ParameterModifier.Out:
					return new DirectionExpression(FieldDirection.Out, new IdentifierExpression(p.Name));
				default:
					return new IdentifierExpression(p.Name);
			}
		}

		internal static bool IsFixedField(ICSharpCode.Decompiler.TypeSystem.IField field, out ICSharpCode.Decompiler.TypeSystem.IType type, out int elementCount)
		{
			type = null;
			elementCount = 0;
			IAttribute attr = field.GetAttribute(KnownAttribute.FixedBuffer);
			if (attr != null && attr.FixedArguments.Length == 2) {
				if (attr.FixedArguments[0].Value is ICSharpCode.Decompiler.TypeSystem.IType trr && attr.FixedArguments[1].Value is int length) {
					type = trr;
					elementCount = length;
					return true;
				}
			}
			return false;
		}

		internal static void CleanUpMethodDeclaration(EntityDeclaration entityDecl, BlockStatement body, ILFunction function, bool decompileBody = true)
		{
			if (function.IsIterator) {
				if (decompileBody && !body.Descendants.Any(d => d is YieldReturnStatement || d is YieldBreakStatement)) {
					body.Add(new YieldBreakStatement());
				}
				if (function.IsAsync) {
					RemoveAttribute(entityDecl, KnownAttribute.AsyncIteratorStateMachine);
				} else {
					RemoveAttribute(entityDecl, KnownAttribute.IteratorStateMachine);
				}
				if (function.StateMachineCompiledWithMono) {
					RemoveAttribute(entityDecl, KnownAttribute.DebuggerHidden);
				}
				if (function.StateMachineCompiledWithLegacyVisualBasic)
				{
					RemoveAttribute(entityDecl, KnownAttribute.DebuggerStepThrough);
					if (function.Method?.IsAccessor == true && entityDecl.Parent is EntityDeclaration parentDecl)
					{
						RemoveAttribute(parentDecl, KnownAttribute.DebuggerStepThrough);
					}
				}
			}
			if (function.IsAsync) {
				entityDecl.Modifiers |= Modifiers.Async;
				RemoveAttribute(entityDecl, KnownAttribute.AsyncStateMachine);
				RemoveAttribute(entityDecl, KnownAttribute.DebuggerStepThrough);
			}
			foreach (var parameter in entityDecl.GetChildrenByRole(Roles.Parameter))
			{
				var variable = parameter.Annotation<ILVariableResolveResult>()?.Variable;
				if (variable != null && variable.HasNullCheck)
				{
					parameter.HasNullCheck = true;
				}
			}
		}

		void AddDefinesForConditionalAttributes(ILFunction function)
		{
			foreach (var call in function.Descendants.OfType<CallInstruction>()) {
				var attr = call.Method.GetAttribute(KnownAttribute.Conditional, inherit: true);
				var symbolName = attr?.FixedArguments.FirstOrDefault().Value as string;
				if (symbolName == null || !currentDecompileRun.DefinedSymbols.Add(symbolName))
					continue;
				syntaxTree.InsertChildAfter(null, new PreProcessorDirective(PreProcessorDirectiveType.Define, symbolName), Roles.PreProcessorDirective);
			}
		}
	}
}
