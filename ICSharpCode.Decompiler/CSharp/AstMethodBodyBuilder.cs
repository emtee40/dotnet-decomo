using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using dnlib.DotNet;
using dnlib.DotNet.Emit;

using dnSpy.Contracts.Decompiler;

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.ControlFlow;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp
{
	internal class AstMethodBodyBuilder
	{
		internal static BlockStatement CreateMethodBody(MethodDef methodDef,
			Decompiler.TypeSystem.IMethod tsMethod,
			DecompileRun decompileRun,
			ITypeResolveContext resolveContext,
			IDecompilerTypeSystem typeSystem,
			DecompilerContext context,
			IEnumerable<ParameterDeclaration> parameters,
			bool valueParameterIsKeyword,
			StringBuilder sb,
			EntityDeclaration entityDeclaration,
			out MethodDebugInfoBuilder stmtsBuilder,
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
				foreach (var transform in GetILTransforms())
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
						context.CancellationToken
					);
					body = statementBuilder.ConvertAsBlock(function.Body);

					Comment prev = null;
					foreach (string warning in function.Warnings)
					{
						body.InsertChildAfter(prev, prev = new Comment(warning), Roles.Comment);
					}
				}

				var stateMachineKind = StateMachineKind.None;
				if (function.IsIterator)
					stateMachineKind = StateMachineKind.IteratorMethod;
				if (function.IsAsync)
					stateMachineKind = StateMachineKind.AsyncMethod;

				var param = function.Variables.Where(x => x.Kind == VariableKind.Parameter);
				var moveNext = (MethodDef)function.MoveNextMethod?.MetadataToken;

				stmtsBuilder = new MethodDebugInfoBuilder(context.SettingsVersion, stateMachineKind, moveNext ?? methodDef, moveNext is not null ? methodDef : null,
					CreateSourceLocals(function), CreateSourceParameters(param), null);

				return body;
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				throw new DecompilerException((IDnlibDef)methodDef, ex);
			}
		}

		internal static void AddAnnotationsToDeclaration(ICSharpCode.Decompiler.TypeSystem.IMethod method, EntityDeclaration entityDecl, ILFunction function)
		{
			int i = 0;
			var parameters = function.Variables.Where(v => v.Kind == VariableKind.Parameter).ToDictionary(v => v.Index);
			foreach (var parameter in entityDecl.GetChildrenByRole(Roles.Parameter))
			{
				if (parameters.TryGetValue(i, out var v))
					parameter.AddAnnotation(new ILVariableResolveResult(v, method.Parameters[i].Type));
				i++;
			}
			entityDecl.AddAnnotation(function);
		}

		internal static bool IsWindowsFormsInitializeComponentMethod(ICSharpCode.Decompiler.TypeSystem.IMethod method)
		{
			return method.ReturnType.Kind == TypeKind.Void && method.Name == "InitializeComponent" && method.DeclaringTypeDefinition.GetNonInterfaceBaseTypes().Any(t => t.FullName == "System.Windows.Forms.Control");
		}

		static SourceLocal[] CreateSourceLocals(ILFunction function) {
			// Does not work for Local functions, lambdas, and anything else which inlines a different method in the current one.
			var dict = new Dictionary<Local, SourceLocal>();
			foreach (var v in function.Variables) {
				if (v.OriginalVariable is null)
					continue;
				if (dict.TryGetValue(v.OriginalVariable, out var existing))
				{
					v.sourceParamOrLocal = existing;
				}
				else
				{
					dict[v.OriginalVariable] = v.GetSourceLocal();
				}
			}
			var array = dict.Values.ToArray();
			//sourceLocalsList.Clear();
			return array;
		}

		static SourceParameter[] CreateSourceParameters(IEnumerable<ILVariable> variables) {
			List<SourceParameter> sourceParametersList = new List<SourceParameter>();
			foreach (var v in variables) {
				sourceParametersList.Add(v.GetSourceParameter());
			}
			var array = sourceParametersList.ToArray();
			//sourceParametersList.Clear();
			return array;
		}

		public static List<IILTransform> GetILTransforms()
		{
			return new List<IILTransform> {
				new ControlFlowSimplification(),
				// Run SplitVariables only after ControlFlowSimplification duplicates return blocks,
				// so that the return variable is split and can be inlined.
				new SplitVariables(),
				new ILInlining(),
				new InlineReturnTransform(), // must run before DetectPinnedRegions
				new RemoveInfeasiblePathTransform(),
				new DetectPinnedRegions(), // must run after inlining but before non-critical control flow transforms
				new YieldReturnDecompiler(), // must run after inlining but before loop detection
				new AsyncAwaitDecompiler(),  // must run after inlining but before loop detection
				new DetectCatchWhenConditionBlocks(), // must run after inlining but before loop detection
				new DetectExitPoints(),
				new LdLocaDupInitObjTransform(),
				new EarlyExpressionTransforms(),
				new SplitVariables(), // split variables once again, because the stobj(ldloca V, ...) may open up new replacements
				// RemoveDeadVariableInit must run after EarlyExpressionTransforms so that stobj(ldloca V, ...)
				// is already collapsed into stloc(V, ...).
				new RemoveDeadVariableInit(),
				new ControlFlowSimplification(), //split variables may enable new branch to leave inlining
				new DynamicCallSiteTransform(),
				new SwitchDetection(),
				new SwitchOnStringTransform(),
				new SwitchOnNullableTransform(),
				new SplitVariables(), // split variables once again, because SwitchOnNullableTransform eliminates ldloca
				new IntroduceRefReadOnlyModifierOnLocals(),
				new BlockILTransform { // per-block transforms
					PostOrderTransforms = {
						// Even though it's a post-order block-transform as most other transforms,
						// let's keep LoopDetection separate for now until there's a compelling
						// reason to combine it with the other block transforms.
						// If we ran loop detection after some if structures are already detected,
						// we might make our life introducing good exit points more difficult.
						new LoopDetection()
					}
				},
				// re-run DetectExitPoints after loop detection
				new DetectExitPoints(),
				new PatternMatchingTransform(), // must run after LoopDetection and before ConditionDetection
				new BlockILTransform { // per-block transforms
					PostOrderTransforms = {
						new ConditionDetection(),
						new LockTransform(),
						new UsingTransform(),
						// CachedDelegateInitialization must run after ConditionDetection and before/in LoopingBlockTransform
						// and must run before NullCoalescingTransform
						new CachedDelegateInitialization(),
						new StatementTransform(
							// per-block transforms that depend on each other, and thus need to
							// run interleaved (statement by statement).
							// Pretty much all transforms that open up new expression inlining
							// opportunities belong in this category.
							new ILInlining(),
							// Inlining must be first, because it doesn't trigger re-runs.
							// Any other transform that opens up new inlining opportunities should call RequestRerun().
							new ExpressionTransforms(),
							new DynamicIsEventAssignmentTransform(),
							new TransformAssignment(), // inline and compound assignments
							new NullCoalescingTransform(),
							new NullableLiftingStatementTransform(),
							new NullPropagationStatementTransform(),
							new TransformArrayInitializers(),
							new TransformCollectionAndObjectInitializers(),
							new TransformExpressionTrees(),
							new IndexRangeTransform(),
							new DeconstructionTransform(),
							new NamedArgumentTransform(),
							new UserDefinedLogicTransform()
						),
					}
				},
				new ProxyCallReplacer(),
				new FixRemainingIncrements(),
				new FixLoneIsInst(),
				new CopyPropagation(),
				new DelegateConstruction(),
				new LocalFunctionDecompiler(),
				new TransformDisplayClassUsage(),
				new HighLevelLoopTransform(),
				new ReduceNestingTransform(),
				new RemoveRedundantReturn(),
				new IntroduceDynamicTypeOnLocals(),
				new IntroduceNativeIntTypeOnLocals(),
				new AssignVariableNames(),
			};
		}
	}
}
