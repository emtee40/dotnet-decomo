using System;

using ICSharpCode.Decompiler.CSharp.Syntax;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	public class CSharpTransformationPipeline
	{
		public static IAstTransform[] CreatePipeline(DecompilerContext context)
		{
			return new IAstTransform[] {
				new PatternStatementTransform(),
				new ReplaceMethodCallsWithOperators(), // must run before DeclareVariables.EnsureExpressionStatementsAreValid
				new IntroduceUnsafeModifier(),
				new AddCheckedBlocks(),
				new DeclareVariables(), // should run after most transforms that modify statements
				new TransformFieldAndConstructorInitializers(), // must run after DeclareVariables
				new DecimalConstantTransform(),
				new PrettifyAssignments(), // must run after DeclareVariables
				new IntroduceUsingDeclarations(),
				new IntroduceExtensionMethods(), // must run after IntroduceUsingDeclarations
				new IntroduceQueryExpressions(), // must run after IntroduceExtensionMethods
				new CombineQueryExpressions(),
				new NormalizeBlockStatements(),
				new FlattenSwitchBlocks(),
			};
		}

		public static void RunTransformationsUntil(AstNode node, Predicate<IAstTransform> abortCondition, DecompilerContext context, TransformContext transformContext)
		{
			if (node is null)
				return;

			var pipeline = context.Cache.GetCSharpPipeline();
			try {
				foreach (var transform in pipeline) {
					//transform.Reset(context);
					context.CancellationToken.ThrowIfCancellationRequested();
					if (abortCondition is not null && abortCondition(transform))
						return;
					transform.Run(node, transformContext);
				}
			}
			finally {
				context.Cache.Return(pipeline);
			}
		}
	}
}
