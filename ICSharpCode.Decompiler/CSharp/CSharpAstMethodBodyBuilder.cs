﻿#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using dnlib.DotNet;
using dnlib.DotNet.Emit;

using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.ControlFlow;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp
{
	internal static class CSharpAstMethodBodyBuilder
	{
		internal static BlockStatement DecompileMethodBody(MethodDef methodDef,
			Decompiler.TypeSystem.IMethod tsMethod,
			DecompileRun decompileRun,
			ITypeResolveContext resolveContext,
			IDecompilerTypeSystem typeSystem,
			DecompilerContext context,
			StringBuilder sb,
			EntityDeclaration entityDeclaration,
			out MethodDebugInfoBuilder? stmtsBuilder,
			out ILFunction function)
		{
			try {
				var ilReader = new ILReader(typeSystem.MainModule) {
					UseDebugSymbols = context.Settings.UseDebugSymbols,
					CalculateILSpans = context.CalculateILSpans
				};
				var body = BlockStatement.Null;
				function = ilReader.ReadIL(methodDef, cancellationToken: context.CancellationToken);
				function.CheckInvariant(ILPhase.Normal);

				AddAnnotationsToDeclaration(tsMethod, entityDeclaration, function);

				var localSettings = context.Settings.Clone();
				if (IsWindowsFormsInitializeComponentMethod(tsMethod))
				{
					localSettings.UseImplicitMethodGroupConversion = false;
					localSettings.UsingDeclarations = false;
					localSettings.AlwaysCastTargetsOfExplicitInterfaceImplementationCalls = true;
					localSettings.NamedArguments = false;
					localSettings.AlwaysQualifyMemberReferences = true;
				}

				var ilTransformContext = new ILTransformContext(function, typeSystem, localSettings) {
					CancellationToken = context.CancellationToken,
					DecompileRun = decompileRun,
					CalculateILSpans = context.CalculateILSpans
				};
				foreach (var transform in CSharpDecompiler.GetILTransforms())
				{
					context.CancellationToken.ThrowIfCancellationRequested();
					transform.Run(function, ilTransformContext);
					function.CheckInvariant(ILPhase.Normal);
					// When decompiling definitions only, we can cancel decompilation of all steps
					// after yield and async detection, because only those are needed to properly set
					// IsAsync/IsIterator flags on ILFunction.
					if (!localSettings.DecompileMemberBodies && transform is AsyncAwaitDecompiler)
						break;
				}

				if (localSettings.DecompileMemberBodies) {
					var statementBuilder = new StatementBuilder(
						typeSystem,
						resolveContext,
						function,
						localSettings,
						decompileRun,
						sb,
						context.CancellationToken
					);
					body = statementBuilder.ConvertAsBlock(function.Body);

					Comment? prev = null;
					foreach (string warning in function.Warnings)
					{
						body.InsertChildAfter(prev, prev = new Comment(warning), Roles.Comment);
					}
				}

				stmtsBuilder = CreateMethodDebugInfoBuilder(function, context.SettingsVersion);

				return body;
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				throw new DecompilerException((IDnlibDef)methodDef, ex);
			}
		}

		public static MethodDebugInfoBuilder? CreateMethodDebugInfoBuilder(ILFunction function, int settingsVersion)
		{
			var dnlibMethod = function.DnlibMethod;

			// Check if this is a ILFunction generated by TransformExpressionTrees
			if (dnlibMethod is null)
				return null;

			StateMachineKind stateMachineKind;
			if (function.IsAsync)
				stateMachineKind = StateMachineKind.AsyncMethod;
			else if (function.IsIterator)
				stateMachineKind = StateMachineKind.IteratorMethod;
			else
				stateMachineKind = StateMachineKind.None;

			var moveNext = (MethodDef?)function.MoveNextMethod?.MetadataToken;
			return new MethodDebugInfoBuilder(settingsVersion, stateMachineKind, moveNext ?? dnlibMethod,
				moveNext is not null ? dnlibMethod : null,
				CreateSourceLocals(function), CreateSourceParameters(function), function.AsyncMethodDebugInfo);
		}

		private static SourceLocal[] CreateSourceLocals(ILFunction function) {
			var dict = new Dictionary<Local, SourceLocal>();
			foreach (var v in function.Variables) {
				if (v.OriginalVariable is null || v.Kind == VariableKind.Parameter)
					continue;
				if (dict.TryGetValue(v.OriginalVariable, out var existing))
					v.sourceParamOrLocal = existing;
				else
					dict[v.OriginalVariable] = v.GetSourceLocal();
			}
			return dict.Values.ToArray();
		}

		private static SourceParameter[] CreateSourceParameters(ILFunction function) {
			List<SourceParameter> sourceParametersList = new List<SourceParameter>();
			foreach (var v in function.Variables) {
				if (v.Kind != VariableKind.Parameter)
					continue;
				sourceParametersList.Add(v.GetSourceParameter());
			}
			return sourceParametersList.ToArray();
		}

		internal static bool IsWindowsFormsInitializeComponentMethod(ICSharpCode.Decompiler.TypeSystem.IMethod method)
		{
			return method.ReturnType.Kind == TypeKind.Void && method.Name == "InitializeComponent" && method.DeclaringTypeDefinition.GetNonInterfaceBaseTypes().Any(t => t.FullName == "System.Windows.Forms.Control");
		}

		internal static void AddAnnotationsToDeclaration(ICSharpCode.Decompiler.TypeSystem.IMethod method, EntityDeclaration entityDecl, ILFunction function)
		{
			int i = 0;
			var parameters = function.Variables.Where(v => v.Kind == VariableKind.Parameter).ToDictionary(v => v.Index!.Value);
			foreach (var parameter in entityDecl.GetChildrenByRole(Roles.Parameter))
			{
				if (parameters.TryGetValue(i, out var v))
					parameter.AddAnnotation(new ILVariableResolveResult(v, method.Parameters[i].Type));
				i++;
			}
			entityDecl.AddAnnotation(function);
		}

		#region Empty Body

		internal static void DecompileEmptyBody(EntityDeclaration methodNode, MethodDef method, Decompiler.TypeSystem.IMethod tsMethod, IDecompilerTypeSystem typeSystem, TypeSystemAstBuilder typeSystemAstBuilder, IEnumerable<ParameterDeclaration>? parameters)
		{
			BlockStatement bs = new BlockStatement();
			var tsGenericContext = new GenericContext(tsMethod);
			if (method.IsInstanceConstructor) {
				var baseCtor = GetBaseConstructorForEmptyBody(method);
				if (baseCtor != null) {
					var methodSig = GetMethodBaseSig(method.DeclaringType.BaseType, baseCtor.MethodSig);
					var args = new List<Expression>();
					foreach (var argType in methodSig.Params)
						args.Add(new DefaultValueExpression(typeSystemAstBuilder.ConvertType(typeSystem.MainModule.ResolveType(argType.RemovePinnedAndModifiers(), tsGenericContext))));
					var stmt = new ExpressionStatement(
						new InvocationExpression(new MemberReferenceExpression(new BaseReferenceExpression(), method.Name), args)
							.WithAnnotation(
								(typeSystem.GetOrAddModule(baseCtor.Module) as MetadataModule)?.GetDefinition(baseCtor)));
					bs.Statements.Add(stmt);
				}
				if (method.DeclaringType.IsValueType && !method.DeclaringType.IsEnum) {
					foreach (var field in method.DeclaringType.Fields) {
						if (field.IsStatic)
							continue;
						var defVal = new DefaultValueExpression(typeSystemAstBuilder.ConvertType(typeSystem.MainModule.ResolveType(field.FieldType.RemovePinnedAndModifiers(), tsGenericContext)));
						var stmt = new ExpressionStatement(new AssignmentExpression(new MemberReferenceExpression(new ThisReferenceExpression(), field.Name), defVal));
						bs.Statements.Add(stmt);
					}
				}
			}
			if (parameters is not null)
			{
				foreach (var p in parameters) {
					if (p.ParameterModifier != ParameterModifier.Out)
						continue;
					var parameter = p.Annotation<Parameter>();
					var defVal = new DefaultValueExpression(typeSystemAstBuilder.ConvertType(typeSystem.MainModule.ResolveType(parameter.Type.RemovePinnedAndModifiers().Next, tsGenericContext)));
					var stmt = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(p.Name), defVal));
					bs.Statements.Add(stmt);
				}
			}
			if (method.MethodSig.GetRetType().RemovePinnedAndModifiers().GetElementType() != ElementType.Void) {
				if (method.MethodSig.GetRetType().RemovePinnedAndModifiers().GetElementType() == ElementType.ByRef) {
					var @throw = new ThrowStatement(new NullReferenceExpression());
					bs.Statements.Add(@throw);
				}
				else {
					var ret = new ReturnStatement(new DefaultValueExpression(typeSystemAstBuilder.ConvertType(typeSystem.MainModule.ResolveType(method.MethodSig.GetRetType().RemovePinnedAndModifiers(), tsGenericContext))));
					bs.Statements.Add(ret);
				}
			}
			methodNode.AddChild(bs, Roles.Body);
		}

		private static MethodDef? GetBaseConstructorForEmptyBody(MethodDef method) {
			var baseType = method.DeclaringType.BaseType.ResolveTypeDef();
			return baseType is null ? null : GetAccessibleConstructorForEmptyBody(baseType, method.DeclaringType);
		}

		private static MethodDef? GetAccessibleConstructorForEmptyBody(TypeDef baseType, TypeDef type) {
			var list = new List<MethodDef>(baseType.FindConstructors());
			if (list.Count == 0)
				return null;
			bool isAssem = baseType.Module.Assembly == type.Module.Assembly || type.Module.Assembly.IsFriendAssemblyOf(baseType.Module.Assembly);
			list.Sort((a, b) => {
				int c = GetAccessForEmptyBody(a, isAssem) - GetAccessForEmptyBody(b, isAssem);
				if (c != 0)
					return c;
				// Don't prefer ref/out ctors
				c = GetParamTypeOrderForEmtpyBody(a) - GetParamTypeOrderForEmtpyBody(b);
				if (c != 0)
					return c;
				return a.Parameters.Count - b.Parameters.Count;
			});
			return list[0];
		}

		private static int GetParamTypeOrderForEmtpyBody(MethodDef m) =>
			m.MethodSig.Params.Any(a => a.RemovePinnedAndModifiers() is ByRefSig) ? 1 : 0;

		private static int GetAccessForEmptyBody(MethodDef m, bool isAssem)
		{
			return m.Access switch {
				MethodAttributes.Public => 0,
				MethodAttributes.FamORAssem => 0,
				MethodAttributes.Family => 0,
				MethodAttributes.Assembly => isAssem ? 0 : 1,
				MethodAttributes.FamANDAssem => isAssem ? 0 : 1,
				MethodAttributes.Private => 2,
				MethodAttributes.PrivateScope => 3,
				_ => 3
			};
		}

		private static MethodBaseSig GetMethodBaseSig(ITypeDefOrRef type, MethodBaseSig msig, IList<TypeSig>? methodGenArgs = null)
		{
			IList<TypeSig>? typeGenArgs = null;
			if (type is TypeSpec ts) {
				var genSig = ts.TypeSig.ToGenericInstSig();
				if (genSig is not null)
					typeGenArgs = genSig.GenericArguments;
			}
			if (typeGenArgs is null && methodGenArgs is null)
				return msig;
			return GenericArgumentResolver.Resolve(msig, typeGenArgs, methodGenArgs)!;
		}

		#endregion
	}
}
